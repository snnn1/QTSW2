# Execution Journal Reconciliation — Fix Proposal

**Date:** 2026-02-13  
**Issue:** Journals can show `TradeCompleted=false` when the broker is actually flat (orphaned journals).

---

## 1. Problem

Execution journals with `EntryFilled=true` and `TradeCompleted=false` can become orphaned when:

| Scenario | Cause |
|----------|-------|
| **Strategy stopped at slot expiry** | `HandleSlotExpiry` calls `Flatten()` only when the strategy is running. If the strategy was stopped before the next stream time (e.g. weekend), `Flatten()` is never called. |
| **Position closed externally** | Manual close, broker action, or another system closed the position. No `OnExecutionUpdate` from our flatten path. |
| **Execution update missed** | `Flatten()` was called, fill occurred, but `OnExecutionUpdate` wasn't processed (e.g. strategy stopped before callback, disconnect). |

**Result:** `GetActiveIntentsForBEMonitoring` returns these intents as "active" even though the broker is flat. BE detection may retry unnecessarily; state is inconsistent.

---

## 2. Proposed Fix: Broker–Journal Reconciliation

Add a **reconciliation pass** that runs when the adapter/engine starts (or periodically) to align journals with broker state.

### 2.1 When to Run

- **On adapter init** (when `SetNTContext` is called and account is available)
- **Optionally:** Once per trading day at engine start, or when `GetActiveIntentsForBEMonitoring` is called and cache is cold

### 2.2 Logic

```
For each journal file with EntryFilled=true and TradeCompleted=false:
  1. Get instrument from entry (e.g. MCL, M2K)
  2. Call GetCurrentPosition(instrument) — broker position
  3. If broker position == 0 (flat):
     - Call RecordReconciliationComplete(intentId, tradingDate, stream, utcNow)
     - Log RECONCILIATION_BROKER_FLAT event
```

### 2.3 New Method: `RecordReconciliationComplete`

**Location:** `ExecutionJournal.cs`

**Signature:**
```csharp
public void RecordReconciliationComplete(
    string intentId,
    string tradingDate,
    string stream,
    DateTimeOffset utcNow,
    string? slotInstanceKey = null)
```

**Behavior:**
- Load journal entry for (tradingDate, stream, intentId)
- If `TradeCompleted` already true → return (idempotent)
- If `EntryFilled` false → return (nothing to reconcile)
- Set `TradeCompleted = true`
- Set `CompletionReason = "RECONCILIATION_BROKER_FLAT"`
- Set `CompletedAtUtc = utcNow`
- For P&L: use `EntryAvgFillPrice` as exit price (conservative: P&L = 0) — or leave null if schema allows
- Save journal, emit `TRADE_RECONCILED` event (not `TRADE_COMPLETED` — different semantics)

**Note:** Using entry price as exit price yields 0 P&L. Real P&L is unknown. Alternative: add `RealizedPnLGross = null` with a `ReconciliationOnly` flag so downstream doesn't treat it as a real trade result.

---

## 3. Implementation Plan

### Phase 1: Add `RecordReconciliationComplete`

1. Add method to `ExecutionJournal`
2. Use entry price as exit price for P&L calc (0 points) — or add `ReconciliationPnLUnknown` flag
3. Emit `TRADE_RECONCILED` (new event type) for audit

### Phase 2: Reconciliation Trigger

**Option A — On `GetActiveIntentsForBEMonitoring` (lazy):**
- When building the active intents list, first run reconciliation for that instrument
- Pro: Runs only when needed
- Con: First call after startup may be slower

**Option B — On adapter init / `SetNTContext` (eager):**
- After NT context is set, scan all journals with `EntryFilled && !TradeCompleted`, reconcile by instrument
- Pro: Journals are clean before any trading
- Con: Startup cost; need to enumerate journal files

**Option C — Dedicated reconciliation on engine start:**
- `RobotEngine.Start()` or `RobotSimStrategy.StateChanged` (when transitioning to Realtime) triggers reconciliation for all instruments with open journals
- Pro: Single, explicit pass
- Con: Need to enumerate journals and map to instruments

**Recommendation:** Option B or C. Run reconciliation once when the strategy reaches Realtime and has NT context. Enumerate `data/execution_journals/*.json`, filter `EntryFilled && !TradeCompleted`, group by instrument, call `GetCurrentPosition` per instrument, reconcile flat ones.

### Phase 3: Event Type

Add to `RobotEventTypes`:
- `TRADE_RECONCILED` = "INFO"

Payload: `intent_id`, `stream`, `instrument`, `trading_date`, `reason`, `note`

---

## 4. Edge Cases

| Case | Handling |
|------|----------|
| Journal has multiple intents for same instrument | Reconcile each intent's journal file independently. Broker flat => all get reconciled. |
| Broker has position but journal says open | No reconciliation (broker is source of truth). |
| Journal says closed, broker has position | Out of scope — would indicate a different bug (e.g. double entry). |

---

## 5. Alternative: Flatten in `HandleForcedFlatten`

**Current behavior:** `HandleForcedFlatten` at session close only sets flags (`ExecutionInterruptedByClose`, `ForcedFlattenTimestamp`). It does **not** call `Flatten()`.

**Actual flatten:** Happens in `HandleSlotExpiry` when the next stream time is reached.

**If the intent is to close at session close:** Add `Flatten()` to `HandleForcedFlatten` so positions are closed when the session closes, not when the next slot starts. This would reduce the window where the strategy must be running for flatten to occur.

**Trade-off:** Streams can stay open until the next stream (per user). If session close should flatten, we need to add it. If not, reconciliation is the right fix for orphaned journals.

---

## 6. Summary

| Fix | Effort | Impact |
|-----|--------|--------|
| **RecordReconciliationComplete + startup reconciliation** | Medium | Fixes orphaned journals, aligns state with broker |
| **Add Flatten() to HandleForcedFlatten** | Low | Closes at session close (if desired) |
| **Both** | Medium | Robust: close at session close + reconcile any remaining orphans |

**Recommended:** Implement reconciliation (Phase 1 + 2). Re-evaluate adding `Flatten()` to `HandleForcedFlatten` based on product intent for session close.
