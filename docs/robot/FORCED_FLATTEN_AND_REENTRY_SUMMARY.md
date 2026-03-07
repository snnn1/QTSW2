# Forced Flatten and Reentry — Full Summary

**Last updated:** 2026-03-05  
**Scope:** Current behavior, recent changes, and reentry flow.

---

## 1. Forced Flatten — True Pre-Close Flatten

**HandleForcedFlatten** performs a **true pre-close flatten**: it closes positions immediately, cancels protective orders, and persists state for market-open reentry. The system is **flat before the exchange closes**.

### 1.1 Three-Phase Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 1: Session Close Trigger (Engine Tick, BEFORE stream.Tick)           │
├─────────────────────────────────────────────────────────────────────────────┤
│ • GetSessionCloseResultOrFallback(tradingDay, sessionClass)                   │
│ • If utcNow >= FlattenTriggerUtc (15:55 CT for 16:00 close):                 │
│   - MarkForcedFlattenTriggeredEmitted → _forced_flatten_markers.json         │
│   - Log FORCED_FLATTEN_TRIGGERED                                             │
│   - For each uncommitted stream in session: s.HandleForcedFlatten(utcNow)     │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 2: HandleForcedFlatten (StreamStateMachine) — TRUE FLATTEN             │
├─────────────────────────────────────────────────────────────────────────────┤
│ Pre-entry (no entry fill):                                                   │
│   → Commit with NO_TRADE_FORCED_FLATTEN_PRE_ENTRY                            │
│   → Slot ends for the day                                                    │
│                                                                              │
│ Post-entry (has entry fill):                                                 │
│   → Flatten position via adapter (retry up to 3x)                            │
│   → Cancel stop and target orders                                           │
│   → Set ExecutionInterruptedByClose = true, ForcedFlattenTimestamp          │
│   → Persist journal (bracket levels for reentry)                              │
│   → Verify no exposure (FORCED_FLATTEN_EXPOSURE_REMAINING if any)            │
│   → Log FORCED_FLATTEN_POSITION_CLOSED, FORCED_FLATTEN_ORDERS_CANCELLED      │
│   → Position state: FLAT                                                    │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 3: Slot Expiry — HandleSlotExpiry (when utcNow >= NextSlotTimeUtc)    │
├─────────────────────────────────────────────────────────────────────────────┤
│ • Original: skip if ExecutionInterruptedByClose (already flattened)         │
│ • Reentry: flatten if ReentryFilled (may still be open at expiry)            │
│ • Cancel orders (idempotent), SlotStatus = EXPIRED, Commit                  │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Timing

| Event | When |
|-------|------|
| `FlattenTriggerUtc` | Session close minus 300s (e.g. 15:55 CT for 16:00 close) |
| `HandleForcedFlatten` | First engine Tick after `utcNow >= FlattenTriggerUtc` — flattens immediately |
| `CheckMarketOpenReentry` | At session reopen when conditions met |
| `HandleSlotExpiry` | First stream Tick after `utcNow >= NextSlotTimeUtc` — only flattens reentry if still open |

**Position is flat** before market close. Reentry restores the trade at market open if slot is still valid.

---

## 2. Reentry — CheckMarketOpenReentry

When a stream has `ExecutionInterruptedByClose == true` (post-entry forced flatten), it can **re-enter at market open** with the same bracket levels.

### 2.1 Reentry Conditions

- `SlotStatus == ACTIVE`
- `ExecutionInterruptedByClose == true`
- `!ReentrySubmitted`
- `OriginalIntentId` present
- `utcNow >= RangeStartChicagoTime` (session open)
- `utcNow < NextSlotTimeUtc` (slot not expired)

### 2.2 Reentry Flow

1. Load bracket levels from `ExecutionJournalEntry` via `OriginalIntentId`
2. Generate `ReentryIntentId` = `{SlotInstanceKey}_REENTRY`
3. Submit MARKET entry with same direction/quantity as original
4. Submit protective stop/target at original bracket levels
5. On protection confirmed: `ExecutionInterruptedByClose = false`

### 2.3 Reentry vs. New Day

| Scenario | Behavior |
|----------|----------|
| **Post-entry forced flatten** | Slot stays ACTIVE; `CheckMarketOpenReentry` re-enters at market open with same brackets |
| **Pre-entry forced flatten** | Slot commits as NO_TRADE; no re-entry |
| **Slot expiry (HandleSlotExpiry)** | Actual flatten; slot commits as EXPIRED |
| **New trading day** | New streams from timetable; fresh ranges, new breakout levels |

---

## 3. Recent Changes (2026-03-04 Hardening)

### 3.1 Root Cause Fixed

**Session close cache was never populated** because `ResolveAndSetSessionCloseIfNeeded()` returned early (State != Realtime, Bars.Count == 0, etc.). With no cache, forced flatten never triggered.

### 3.2 Fail-Safe Fallback

When cache is empty, engine now uses **fallback** so forced flatten still runs:

| Path | Condition | Result |
|------|-----------|--------|
| Cache hit | `TryGetSessionCloseResult` returns result | Use cached (LIVE_RESOLVER) |
| Spec fallback | `spec != null`, `market_close_time` present | Compute from spec (America/Chicago, DST-aware) |
| Emergency fallback | Spec null or empty | Use 16:00 CT hard default; emit SESSION_CLOSE_FALLBACK_FAILED (CRITICAL) |

**No silent skip:** If spec and TradingHours are both missing, engine uses 16:00 CT and still flattens at 15:55 CT.

### 3.3 Timezone Contract

- `spec.entry_cutoff.market_close_time` is **America/Chicago local time** (DST-aware)
- `TimeService.ConstructChicagoTime` uses `GetUtcOffset(localDateTime)` for correct DST
- Unit test: `SessionCloseFallbackTimeZoneTests.RunDstBoundaryTests()` (March + November)

### 3.4 One-Shot Latches

Events emit **once per (tradingDay, sessionClass)** to avoid log spam:

- `SESSION_CLOSE_FALLBACK_USED`
- `SESSION_CLOSE_CACHE_MISSING`
- `SESSION_CLOSE_FALLBACK_FAILED`
- `FORCED_FLATTEN_TRIGGERED`

### 3.5 New Logging Events

| Event | Level | When |
|-------|-------|------|
| `SESSION_CLOSE_RESOLVED` | INFO | Cache set successfully |
| `SESSION_CLOSE_RESOLVE_SKIPPED` | WARN | Early return (reason, state, bars_count, instrument_key) |
| `SESSION_CLOSE_RESOLVER_FAILED` | ERROR | Resolve throws or invalid result |
| `SESSION_CLOSE_FALLBACK_USED` | WARN | Cache empty; using spec or emergency fallback |
| `SESSION_CLOSE_FALLBACK_FAILED` | CRITICAL | Spec null; using emergency 16:00 CT |
| `SESSION_CLOSE_CACHE_MISSING` | ERROR | Cache empty 5+ min, no fallback |

### 3.6 Ported to modules/robot/core

Forced flatten logic (fallback, emergency path, one-shot latches) is now in **modules/robot/core** so the harness can validate it without NinjaTrader.

---

## 4. Validation

### 4.1 DST Test

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test DST
```

### 4.2 Forced Flatten Validation

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --validate-forced-flatten
python scripts/check_forced_flatten_today.py 2026-03-04
```

### 4.3 Expected Outputs

- `FORCED_FLATTEN_TRIGGERED` in logs
- `FORCED_FLATTEN_POSITION_CLOSED`, `FORCED_FLATTEN_ORDERS_CANCELLED` (post-entry)
- `_forced_flatten_markers.json` in `logs/robot/journal/`
- `ForcedFlattenTimestamp` in slot journals (`tradingDate_stream.json`)

---

## 5. Known Limitations

| Limitation | Notes |
|------------|-------|
| **Early close days** | Spec uses fixed 16:00 CT; early-close days (e.g. day after Thanksgiving) not encoded |
| **HandleSlotExpiry bypasses IEA** | Direct `adapter.Flatten()`; potential race with IEA worker (HandleForcedFlatten has retries) |
| **NinjaTrader instrument-level flatten** | `account.Flatten(instrument)` flattens entire instrument; no per-intent flatten |
| **Holiday** | `SessionCloseResolver` returns `HasSession=false` → no forced flatten (SESSION_CLOSE_HOLIDAY) |

---

## 6. Related Docs

- `docs/robot/FORCED_FLATTEN_FIX_SUMMARY.md` — Root cause and implementation
- `docs/robot/FORCED_FLATTEN_RUNDOWN.md` — End-to-end flow, IEA considerations
- `FLATTEN_REENTRY_ISSUE_COMPLETE_SUMMARY.md` — Manual flatten / opposite re-entry fix (different issue)
