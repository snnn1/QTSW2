# GC1 Stop Brackets Failed — Full Investigation (2026-02-19)

**Date:** 2026-02-19  
**Stream:** GC1 (08:00 S1 slot)  
**Outcome:** Sell stop and long stop entry orders were NOT submitted. MGC instrument blocked.

---

## Executive Summary

| Item | Finding |
|------|---------|
| **Root cause** | IEA `EnqueueAndWait` timed out (5 seconds) while worker was blocked in NinjaTrader `CreateOrder`/`Submit` |
| **Failure mode** | Worker had never processed any work (`last_processed_sequence=0`); first work item blocked for >5s |
| **Timeout used** | 5000 ms (log evidence) — current source has 12000 ms; deployed DLL likely older build |
| **Consequence** | MGC instrument blocked; GC1 and GC2 streams frozen; no retry path until restart |

---

## Timeline (UTC)

| Time | Event | Source |
|------|-------|--------|
| 14:00:01.618 | GC1 RANGE_LOCKED (range_high=5040.9, range_low=4988.4) | robot_GC.jsonl |
| 14:00:01.618 | STREAM_STATE_TRANSITION RANGE_BUILDING → RANGE_LOCKED | robot_GC.jsonl |
| 14:00:01.820 | STOP_BRACKETS_SUBMIT_ENTERED (GC1, preconditions OK) | robot_ENGINE.jsonl |
| 14:00:01.830 | INTENT_REGISTERED, INTENT_POLICY_REGISTERED | robot_ENGINE.jsonl |
| 14:00:01.848 | ENTRY_SUBMIT_PRECHECK | robot_ENGINE.jsonl |
| 14:00:06.918 | **IEA_ENQUEUE_AND_WAIT_TIMEOUT** | robot_ENGINE.jsonl |
| 14:00:06.918 | STREAM_FROZEN_NO_STAND_DOWN (GC1, GC2, MGC) | robot_ENGINE.jsonl |
| 14:00:06.920 | CRITICAL_NOTIFICATION_REJECTED (IEA_ENQUEUE_FAILURE not whitelisted) | robot_ENGINE.jsonl |
| ~14:00:01.8xx | STOP_BRACKETS_SUBMIT_FAILED (long_success=False, short_success=False) | robot_GC.jsonl |
| 14:01:01.682 | RESTART_RETRY_STOP_BRACKETS (previous_attempt_failed=True) | robot_GC.jsonl |
| 14:01:01.682 | STOP_BRACKETS_SUBMIT_ENTERED (retry) | robot_GC.jsonl |

**Elapsed:** ~5.3 seconds from ENTRY_SUBMIT_PRECHECK to IEA timeout.

---

## Root Cause Analysis

### 1. IEA Worker Blocked

Log evidence:
```json
{
  "event": "IEA_ENQUEUE_AND_WAIT_TIMEOUT",
  "data": {
    "iea_instance_id": "5",
    "execution_instrument_key": "MGC",
    "timeout_ms": "5000",
    "enqueue_sequence": "1",
    "last_processed_sequence": "0",
    "policy": "IEA_FAIL_CLOSED_BLOCK_INSTRUMENT",
    "note": "Worker timeout — instrument blocked; no new intents until restart"
  }
}
```

- **enqueue_sequence=1:** First work item ever enqueued to MGC IEA for this session
- **last_processed_sequence=0:** Worker had never completed any work
- **Conclusion:** Worker received the GC1 long-bracket submission, started `CreateOrder`/`Submit`, and blocked. Caller waited 5 seconds and timed out.

### 2. Timeout Value Mismatch

| Location | Timeout | Notes |
|----------|---------|-------|
| **Log (actual)** | 5000 ms | From IEA_ENQUEUE_AND_WAIT_TIMEOUT payload |
| **Current source** | 12000 ms | `ENTRY_SUBMISSION_TIMEOUT_MS` in NinjaTraderSimAdapter.NT.cs |
| **IEA default** | 5000 ms | `EnqueueAndWait(work, int timeoutMs = 5000)` |

**Implication:** The deployed `Robot.Core.dll` in NinjaTrader is likely an older build that either:
- Uses the default 5000 ms (does not pass 12000), or
- Was built before the 12000 fix (2026-02-18 YM1 incident remediation)

### 3. Submission Flow

1. `SubmitStopEntryBracketsAtLock` called for GC1 at RANGE_LOCKED
2. Submits **long** first, then **short** (sequential)
3. First call: `SubmitStopEntryOrder(longIntentId, ..., "Long", brkLong, ...)`
4. Adapter routes to `_iea.EnqueueAndWait(lambda, ENTRY_SUBMISSION_TIMEOUT_MS)`
5. Lambda: `_iea.SubmitStopEntryOrder` (aggregation) or `SubmitSingleEntryOrderCore` (fallback)
6. Worker executes lambda; blocks in `account.CreateOrder` or `order.Submit`
7. Main thread waits on `done.Wait(timeoutMs)` → times out at 5s
8. `_instrumentBlocked = true`; callback invokes `StandDownStreamsForInstrument("MGC")`
9. Second call (short) would hit `_instrumentBlocked` and fail immediately (or never run if first call blocks)

### 4. Why Worker Blocked

NinjaTrader's `Account.CreateOrder` and `Order.Submit` can block the calling thread for several seconds when:
- Multiple strategies/charts are active
- Sim connection has latency
- Internal NT locks are contended
- System under load

This matches the 2026-02-18 YM1 incident (worker blocked ~6s).

---

## Retry Behavior

- **RESTART_RETRY_STOP_BRACKETS** at 14:01:01 indicates the stream retried on next tick
- Retry would call `SubmitStopEntryBracketsAtLock` again
- By then, `_instrumentBlocked = true` for MGC
- `EnqueueAndWait` checks `_instrumentBlocked` first and rejects with `IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED`
- Result: Retry also fails; no brackets submitted until NinjaTrader restart

---

## Recommendations

### Immediate (Deploy)

1. **Rebuild and redeploy Robot.Core.dll**
   - Ensure `ENTRY_SUBMISSION_TIMEOUT_MS = 12000` is in the deployed build
   - Copy `Robot.Core.dll` and `Robot.Core.pdb` to `%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\`
   - Restart NinjaTrader to load new DLL

2. **Verify timeout in logs**
   - After deploy, trigger a bracket submission
   - If timeout occurs, `IEA_ENQUEUE_AND_WAIT_TIMEOUT` should show `timeout_ms: 12000`

### Short-Term (Code)

3. **Make entry timeout configurable**
   - Add `entry_submission_timeout_ms` to `configs/execution_policy.json` or similar
   - Default 12000; allow override without rebuild

4. **Add IEA_ENQUEUE_FAILURE_INSTRUMENT_BLOCKED to critical notification whitelist**
   - Log shows `CRITICAL_NOTIFICATION_REJECTED` because event type not whitelisted
   - Operators should receive alerts when instrument is blocked

### Medium-Term (Hardening)

5. **Flatten on block** (from 2026-02-18 assessment)
   - When `blockInstrumentCallback` fires, call `FlattenEmergency` for blocked instrument
   - Prevents unprotected positions when IEA blocks

6. **IEA worker stuck watchdog**
   - If `LastMutationUtc` stale for >30s, log CRITICAL
   - Helps detect worker deadlock

---

## Related

- [2026-02-18 Break-Even Full Assessment](2026-02-18_BREAK_EVEN_FULL_ASSESSMENT.md) — YM1 incident, EnqueueAndWait timeout increase
- [IEA Invariant Gaps](../IEA_IMPLEMENTATION_GAPS_AND_DECISIONS.md)
- [IEA Log Verification Guide](../IEA_LOG_VERIFICATION_GUIDE.md)

---

*End of Investigation*
