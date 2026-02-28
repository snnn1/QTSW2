# NQ1 Orders Not Submitted — IEA EnqueueAndWait Timeout

**Date**: 2026-02-20  
**Stream**: NQ1 (and NQ2 blocked)  
**Instrument**: MNQ  
**Root cause**: IEA worker thread never completes NT order submission; EnqueueAndWait times out and blocks instrument.

---

## Summary

NQ1 entry orders were submitted at 14:00 UTC (08:00 Chicago) but the **IEA worker never processed the work**. After 12 seconds, `EnqueueAndWait` timed out, the instrument was blocked, and all subsequent submissions (including NQ2) were rejected.

---

## Log Evidence

| Time (UTC) | Event | Notes |
|------------|-------|-------|
| 14:00:02 | ENTRY_SUBMIT_PRECHECK | NQ1 entry submission started |
| 14:00:14 | **IEA_ENQUEUE_AND_WAIT_TIMEOUT** | CRITICAL: Worker timeout after 12s |
| 14:00:14 | STREAM_FROZEN_NO_STAND_DOWN | NQ1 and NQ2 both blocked |
| 14:00:16 | IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED | Second submission (enqueue_seq=2) rejected |

**Critical data**:
```json
{
  "enqueue_sequence": "1",
  "last_processed_sequence": "0",
  "timeout_ms": "12000"
}
```

`last_processed_sequence: 0` means the IEA worker **never completed** the first work item. The worker either:
1. Never picked up the work (unlikely — worker thread is running)
2. Picked up the work but is **blocked inside** the work (e.g. NinjaTrader `Submit()` blocking)

---

## Root Cause

**NinjaTrader's `CreateOrder()` / `Account.Submit()` are invoked from the IEA worker thread.**

The IEA design runs order submission on a worker thread for serialization. The worker calls:
- `_iea.SubmitStopEntryOrder(...)` or `SubmitSingleEntryOrderCore(...)`
- Which calls `Executor.CreateStopMarketOrder()` and `Executor.SubmitOrders()` (NinjaTrader APIs)

NinjaTrader's order APIs likely require execution on the strategy/UI thread. When the worker thread calls them:
- The call may block indefinitely (waiting for NT internal dispatch)
- Or NT may block the worker while waiting for the strategy thread
- The strategy thread is blocked on `done.Wait(12000)` — **deadlock**

**Result**: Worker never completes → `done.Set()` never called → EnqueueAndWait times out → instrument blocked.

---

## Fix Options

### Option A: Marshal NT submission to strategy thread (recommended)

The IEA worker should **not** call NT APIs directly. Instead:
1. Worker enqueues a "submit request" (intent, prices, etc.)
2. Strategy thread (OnBarUpdate or similar) processes pending requests and calls `Submit()` on the thread NT expects
3. Worker waits for strategy thread to signal completion

### Option B: Run entry submission on strategy thread (simpler)

When `SubmitStopEntry` is called from the strategy thread (OnBarUpdate path), **skip** `EnqueueAndWait` and run the submission directly. Use a lock to serialize across multiple strategy instances sharing the same IEA. IEA still owns OrderMap/IntentMap for fill handling.

### Option C: Increase timeout (not recommended)

The comment already says "NT CreateOrder/Submit can block 6+ seconds under load" - timeout was increased from 5s to 12s. But the worker never completes at all; increasing timeout would not fix the underlying deadlock.

---

## Fix Implemented (2026-02-20)

**Option B: Run entry submission on strategy thread**

- Added `EntrySubmissionLock` to IEA for cross-instance serialization
- Replaced `EnqueueAndWait` with `lock (_iea.EntrySubmissionLock)` + direct call in `NinjaTraderSimAdapter.NT.cs`
- Entry submission now runs on the strategy thread (OnBarUpdate context) where NT expects it

**Files changed:**
- `InstrumentExecutionAuthority.cs` — added `EntrySubmissionLock`
- `NinjaTraderSimAdapter.NT.cs` — entry submission path uses lock instead of EnqueueAndWait

**Note:** Protective orders, BE modification, and flatten still run on the IEA worker. If similar timeouts occur for those paths, they will need the same treatment (marshal to strategy thread).

---

## Immediate Actions

1. **Restart strategy** after IEA timeout — instrument remains blocked until restart
2. **Add IEA_ENQUEUE_FAILURE_INSTRUMENT_BLOCKED** to critical notification whitelist so operators are alerted

---

## References

- `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.cs` — EnqueueAndWait, worker loop
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` — SubmitStopEntry, ENTRY_SUBMISSION_TIMEOUT_MS = 12000
- `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` — SubmitStopEntryOrder, TryAggregateWithExistingOrders
