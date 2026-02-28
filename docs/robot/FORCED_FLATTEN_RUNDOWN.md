# Forced Flatten — End-to-End Rundown

**Purpose:** Document exactly how forced flatten works, whether IEA should handle it, and potential issues/conflicts with NinjaTrader.

---

## 1. What "Forced Flatten" Actually Does

**Important:** The name is misleading. `HandleForcedFlatten` does **not** flatten positions. It marks the slot as *execution interrupted by close* and leaves the position open for re-entry.

### 1.1 End-to-End Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 1: Session Close Trigger (Engine Tick, BEFORE stream.Tick)             │
├─────────────────────────────────────────────────────────────────────────────┤
│ 1. TryGetSessionCloseResult(tradingDate, sessionClass)                       │
│ 2. If !HasSession || !FlattenTriggerUtc → skip (e.g. holiday)               │
│ 3. If utcNow < FlattenTriggerUtc → skip                                      │
│ 4. MarkForcedFlattenTriggeredEmitted(tradingDate, sessionClass)              │
│ 5. Log FORCED_FLATTEN_TRIGGERED                                              │
│ 6. For each stream in session where !Committed: s.HandleForcedFlatten(utcNow)  │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 2: HandleForcedFlatten (StreamStateMachine) — NO FLATTEN CALL          │
├─────────────────────────────────────────────────────────────────────────────┤
│ • Pre-entry: Commit with NO_TRADE_FORCED_FLATTEN_PRE_ENTRY                   │
│ • Post-entry:                                                                 │
│   - Set ExecutionInterruptedByClose = true                                   │
│   - Set ForcedFlattenTimestamp                                                │
│   - Store OriginalIntentId if missing                                         │
│   - Persist journal                                                           │
│   - Log FORCED_FLATTEN_MARKET_CLOSE                                           │
│   - Slot stays ACTIVE (for re-entry)                                          │
│   - Position REMAINS OPEN                                                    │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 3: Actual Flatten — HandleSlotExpiry (when utcNow >= NextSlotTimeUtc)  │
├─────────────────────────────────────────────────────────────────────────────┤
│ • Called from stream.Tick() when slot expired                                │
│ • _executionAdapter.Flatten(OriginalIntentId, instrument, utcNow)            │
│ • _executionAdapter.Flatten(ReentryIntentId, instrument, utcNow) if reentry  │
│ • Cancel orders for both intents                                              │
│ • SlotStatus = EXPIRED, Commit                                                │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Timing

| Event | When |
|-------|------|
| `FlattenTriggerUtc` | Session close minus buffer (e.g. 15:55 CT for 16:00 close) |
| `HandleForcedFlatten` | First engine Tick after `utcNow >= FlattenTriggerUtc` |
| `HandleSlotExpiry` | First stream Tick after `utcNow >= NextSlotTimeUtc` (next slot occurrence) |

So the position stays open from session close until `NextSlotTimeUtc` (e.g. next day 14:00 for a 14:00 slot). Re-entry is attempted via `CheckMarketOpenReentry` when market reopens.

---

## 2. Should Forced Flatten Be Handled by IEA?

### 2.1 Current State

| Path | Uses IEA? | Uses FlattenWithRetry? | Thread |
|------|-----------|------------------------|--------|
| **HandleSlotExpiry** (actual flatten at slot expiry) | **No** | **No** | Strategy (stream.Tick) |
| BE failure / FailClosed | Yes | Yes | IEA worker |
| Orphan fill (intent not found) | Yes (enqueued) | No | IEA worker |
| Untracked fill | No (direct) | Yes | Strategy (NT event thread) |
| Unknown order fill | Yes (enqueued) | No | IEA worker |

### 2.2 Recommendation

**HandleSlotExpiry flatten should go through IEA** for consistency and safety:

1. **Serialization:** Flatten at slot expiry runs on strategy thread while IEA worker may be processing fills. Direct `adapter.Flatten()` bypasses IEA queue → potential race with fill handling.
2. **Retries:** HandleSlotExpiry uses `adapter.Flatten()` with no retries. A transient failure leaves position open and slot committed.
3. **Consistency:** All other flatten paths (BE failure, orphan, etc.) either use FlattenWithRetry or run in IEA context. Slot expiry is the outlier.

**Proposed change:** Route HandleSlotExpiry flatten through `FlattenWithRetry` so it uses `EnqueueFlattenAndWait` when IEA is enabled. That serializes with other mutations and adds retries.

---

## 3. Potential Issues and NinjaTrader Conflicts

### 3.1 NinjaTrader Threading

**Problem:** NinjaTrader `Account.Flatten(Instrument)` (and `Account.Change`, etc.) must run on the strategy thread. When IEA is enabled:

- **FlattenWithRetry** → `EnqueueFlattenAndWait` → work runs on **IEA worker thread**
- **FlattenIntentReal** (which calls `account.Flatten`) therefore runs on IEA worker for BE failure, orphan fill, etc.

This mirrors the break-even bug: `account.Change()` was called from IEA worker and had to be moved to strategy thread via `EntrySubmissionLock` and `EvaluateBreakEvenDirect`.

**Risk:** `account.Flatten()` from IEA worker may cause:
- Silent failures
- "Calculating" / strategy freeze
- Cross-thread exceptions

**Mitigation:** Same pattern as BE: run flatten on strategy thread under `EntrySubmissionLock` when IEA is enabled. The IEA would enqueue a "flatten request" that the strategy thread executes on its next Tick.

### 3.2 NinjaTrader Instrument-Level Flatten

NinjaTrader has no per-intent flatten. `account.Flatten(instrument)` flattens the **entire instrument position**.

- Multiple intents on same instrument → flattening one flattens all.
- Code comments acknowledge this; coordinator tracks remaining intents.
- Rare path: other intents would need re-entry.

### 3.3 HandleSlotExpiry Bypasses IEA and Retries

- **No retries:** Single `Flatten()` call; on failure, slot is committed anyway.
- **No IEA queue:** Direct adapter call; can race with IEA worker processing fills.
- **Swallowed exceptions:** `catch { /* log and continue */ }` — flatten failure is not surfaced to engine.

### 3.4 Session Close Prerequisite

`FORCED_FLATTEN_TRIGGERED` only fires when:

- `TryGetSessionCloseResult` returns `HasSession == true`
- `FlattenTriggerUtc` is set

If `SessionCloseResolver` returns `HasSession == false` (e.g. holiday, no eligible segments), forced flatten never runs. This caused the 2026-02-20 incident: `SESSION_CLOSE_HOLIDAY` → no `HandleForcedFlatten` for S1/S2.

### 3.5 Flatten Recognition Window

When we call `Flatten()`, the broker creates a close order **without** a QTSW2 tag. That fill would be untracked. To avoid a cascade:

- `_lastFlattenInstrument` and `_lastFlattenUtc` record recent flatten calls.
- If an untracked fill arrives within `FLATTEN_RECOGNITION_WINDOW_SECONDS` of a flatten for that instrument, we treat it as our own flatten fill and skip redundant flatten.

---

## 4. Code References

| Component | File | Key Methods |
|-----------|------|-------------|
| Engine forced flatten block | `RobotEngine.cs` | ~1440–1477 |
| HandleForcedFlatten | `StreamStateMachine.cs` | ~5636–5729 |
| HandleSlotExpiry | `StreamStateMachine.cs` | ~5735–5832 |
| Flatten / FlattenWithRetry | `NinjaTraderSimAdapter.cs` | ~1311–1416 |
| FlattenIntentReal | `NinjaTraderSimAdapter.NT.cs` | ~4052–4190 |
| EnqueueFlattenAndWait | `InstrumentExecutionAuthority.cs` | ~265–271 |
| Session close resolution | `SessionCloseResolver.cs` | Structural close: largest gap between segments; `FlattenTriggerUtc` = close − buffer |

---

## 5. Summary

| Topic | Summary |
|-------|---------|
| **What forced flatten does** | Marks slot as interrupted at session close; actual flatten happens at slot expiry |
| **IEA handling** | HandleSlotExpiry bypasses IEA; should use FlattenWithRetry for serialization and retries |
| **Threading risk** | Flatten from IEA worker (BE failure, orphan) may violate NT threading; consider strategy-thread execution like BE |
| **NT API limitation** | Instrument-level flatten only; no per-intent |
| **Session close dependency** | Requires `HasSession` and `FlattenTriggerUtc`; holidays can block forced flatten |
