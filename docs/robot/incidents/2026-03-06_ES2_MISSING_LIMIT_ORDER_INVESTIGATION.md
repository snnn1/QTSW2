# ES2 Traded Without Limit Order — Investigation (2026-03-06)

## Summary

ES2 (MES) entry filled (2 contracts at 6762.5) but the protective **target (limit) order** is missing. The position is open and unprotected.

## Root Cause (Confirmed from Logs)

**NtAction queue backlog** — The protective command was enqueued but never executed because `DrainNtActions` did not run (or ran too late) for the strategy that received the fill.

### Log Evidence

| Time (UTC) | Event | Detail |
|------------|-------|--------|
| 15:30:05.054 | EXECUTION_FILLED | intent f6dee8795a328711, 2 @ 6762.5 |
| 15:30:05.122 | **NT_ACTION_ENQUEUED** | `PROTECTIVES:f6dee8795a328711`, queue_depth=4 |
| 15:30:10.131 | **PROTECTION_ENQUEUED_BACKLOG_WARN** | "Protectives enqueued for extended time - queue may be backed up", elapsed_sec=5 |

The queue had 4 items (3 ahead: CancelOrders x2, SubmitOrders from aggregation at 15:30:02). The protective command was 4th. `DrainNtActions` runs only from `OnBarUpdate` on the strategy thread. If the MES chart did not receive another tick/bar (e.g. 1‑min bars, next bar at 15:31), the queue was never drained and protectives were never submitted.

## Journal State

- **File**: `2026-03-06_ES2_f6dee8795a328711.json`
- **EntryOrderType**: ENTRY_STOP (filled)
- **EntryFilledQuantityTotal**: 2
- **StopPrice**: 6732.50, **TargetPrice**: 6772.50
- **ExitOrderType**: null (no exit yet)
- **TradeCompleted**: false
- **ProtectionSubmitted**: false (journal)

## Expected Flow

1. Entry stop fills → execution update fires
2. `HandleEntryFill` runs → enqueues `NtSubmitProtectivesCommand`
3. Next tick → `DrainNtActions` → `ExecuteSubmitProtectives`
4. Submit STOP (stop-market) + TARGET (limit) as OCO pair

The **target is the limit order**. Step 2 succeeded (command enqueued). Step 3 never ran in time.

## Other Possible Causes (Ruled Out)

### 1. RECONCILIATION_QTY_MISMATCH timing (likely)

- Reconciliation reported `journal_qty=0` (file lock) while broker had 2.
- Engine froze MES and stood down ES2 stream.
- **Protective submission does NOT go through RiskGate** — it should still run.
- However: if `HandleEntryFill` ran **before** the fill was fully processed, or if the IEA worker was blocked/delayed, the sequence could be disrupted.
- **Hypothesis**: Reconciliation ran, froze instrument, and the `blockInstrumentCallback` may have triggered a flatten. If flatten was enqueued, it would cancel protective orders. Check whether flatten ran and failed (position would remain but protectives cancelled).

### 2. HandleEntryFill never called

- Execution update routed to wrong strategy (e.g. ES1 received ES2 fill) → intent not found → flatten enqueued.
- **Check logs**: `EXEC_UPDATE_NO_ENDPOINT`, `INTENT_NOT_FOUND_FLATTEN_ENQUEUED`, `EXECUTION_UPDATE_GRACE_STARTED`.

### 3. HandleEntryFill blocked

- `IsExecutionAllowed()` false (recovery state).
- Intent incomplete (missing StopPrice, TargetPrice, Direction).
- `CanSubmitExit` failed (e.g. WOULD_OVER_CLOSE).
- **Check logs**: `PROTECTIVE_ORDERS_BLOCKED_RECOVERY`, `INTENT_INCOMPLETE_UNPROTECTED_POSITION`, `EXIT_VALIDATION_FAILED`.

### 4. Protective submission failed

- STOP or TARGET rejected by broker.
- **Check logs**: `STOP_SUBMIT_REQUESTED`, `STOP_SUBMIT_CONFIRMED`, `TARGET_SUBMIT_REQUESTED`, `TARGET_SUBMIT_CONFIRMED`, `PROTECTIVE_ORDERS_FAILED_FLATTENED`, `ORDER_CREATE_FAIL`.

### 5. blockInstrumentCallback (reconciliation) flattened

- On `RECONCILIATION_QTY_MISMATCH`, the callback does `blockInstrumentCallback` which:
  - Flattens the instrument
  - Calls `StandDownStreamsForInstrument`
- If flatten **succeeded**, position would be 0. User reports position exists → flatten likely **failed** or didn't run.
- If flatten **failed**, position remains but `CancelRobotOwnedWorkingOrdersReal` may have run first — that would cancel any working protective orders. So: protectives could have been placed, then cancelled by the flatten path, and flatten itself failed. Result: position + no protectives.

## Diagnostic Steps

1. **Search logs** for intent `f6dee8795a328711` and ES2/MES around fill time (15:30:05 UTC):

   ```powershell
   Select-String -Path "logs\robot\*.jsonl" -Pattern "f6dee8795a328711|STOP_SUBMIT|TARGET_SUBMIT|PROTECTIVE_ORDERS|RECONCILIATION_QTY_MISMATCH|FLATTEN" | Select-Object -First 50
   ```

2. **Check for flatten + cancel**:
   - `FLATTEN_REQUESTED`, `FLATTEN_SUBMITTED`, `FLATTEN_VERIFY_PASS` / `FLATTEN_VERIFY_FAIL`
   - If flatten was requested, `CancelRobotOwnedWorkingOrdersReal` runs first — that cancels protective orders.

3. **Check RECONCILIATION_QTY_MISMATCH timing**:
   - When did it first fire relative to 15:30:05?
   - The `blockInstrumentCallback` flattens on freeze. If that ran at 15:30:00–15:30:10, it could have cancelled protectives (or attempted flatten).

## Immediate Resolution

**Position is unprotected.** Options:

1. **Manual flatten** in NinjaTrader to close the position.
2. **Manual protective orders** — place stop and target manually at 6732.50 / 6772.50.
3. **Force reconcile** to unfreeze, then restart — robot may re-detect position and attempt recovery (if recovery path places protectives).

## Fix Applied (2026-03-06)

**Drain NtActions on every Last tick** — `OnMarketData(MarketDataType.Last)` now drains the NtAction queue *before* the `hasExposure` check. Previously, DrainNtActions ran only when `hasExposure` was true; immediately after a fill, `Account.Positions` can lag, so the early return skipped the drain. Now protective commands are processed on the next tick (milliseconds) instead of waiting for the next bar (up to 55+ seconds with 1-min bars).

- `RobotSimStrategy.cs` — DrainNtActions moved to run on every Last tick in Realtime, before hasExposure check.

## Other Mitigation Ideas (not implemented)

1. **Prioritize SUBMIT_PROTECTIVES** — Process protective commands before or ahead of non-critical deferred actions (e.g. cancels of already-filled orders).
2. **Trigger DrainNtActions on fill** — When an entry fill is processed, ensure the strategy thread drains the queue soon (e.g. via a timer or explicit signal) rather than waiting for the next bar.
3. **Reduce queue depth at lock** — Defer aggregation cancels/submits so they don’t block protective submission, or process protectives in a higher-priority path.

## Code References

- `InstrumentExecutionAuthority.NT.cs` — `HandleEntryFill`, protective enqueue
- `NinjaTraderSimAdapter.NT.cs` — `ExecuteSubmitProtectives`, `SubmitTargetOrderReal` (limit order)
- `Strategies/RobotSimStrategy.cs` — `OnMarketData(Last)` → `DrainNtActions` (every tick, before hasExposure); `OnBarUpdate` → `DrainNtActions` (strategy thread)
- `RobotEngine.cs` — `blockInstrumentCallback` (flatten on freeze), `StandDownStreamsForInstrument`
- `docs/robot/incidents/2026-03-06_MES_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md`
