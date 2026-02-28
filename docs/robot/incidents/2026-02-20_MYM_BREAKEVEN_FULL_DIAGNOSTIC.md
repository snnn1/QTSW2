# MYM Break-Even Full Diagnostic — Why Stop Loss Did Not Change

**Date**: 2026-02-20  
**Stream**: YM1  
**Instrument**: MYM  
**Intent**: 8f962ae805d0a42d  

---

## Executive Summary

Break-even did not change the stop loss because:

1. **Primary (12:48 PM Chicago)**: BE modification ran on the IEA worker thread. NinjaTrader requires `account.Change()` on the strategy thread. This caused stop ping-pong (49604↔49305), repeated retries, and eventually `STOP_MODIFY_FAILED` (MAX_RETRIES_EXCEEDED) → `StandDownStream`.

2. **Secondary (9 PM Chicago)**: After stand-down, BE evaluation may have been blocked or the position may have been closed. This document provides a diagnostic checklist to determine why BE did not fire again.

---

## Part 1: Why the Stop Did Not Change at 12:48 PM

### Timeline (UTC → Chicago)

| Time (UTC) | Time (Chicago) | Event |
|------------|----------------|-------|
| 18:42:18 | 12:42 PM | Protective stop submitted @ 49305 |
| 18:48:01 | 12:48 PM | BE trigger reached; ChangePending 49604 → ChangeSubmitted 49305 → Working 49305 |
| 18:48:01 | 12:48 PM | Ping-pong: ChangePending 49604 → ChangeSubmitted 49305 (repeats every ~100–300ms) |
| 18:48:42 | 12:48 PM | STOP_MODIFY_FAILED (MAX_RETRIES_EXCEEDED) → StandDownStream |

### Root Cause: NinjaTrader Threading Violation

| Operation | Thread Used | NT Requirement | Result |
|-----------|-------------|----------------|--------|
| Entry submission | Was IEA worker | Strategy thread | **Fixed** (moved to strategy) |
| **BE modification** | **Was IEA worker** | **Strategy thread** | **Was broken** |

**Code path (before fix):**

```
OnMarketData(Last) 
  → _adapter.EvaluateBreakEven(tickPrice, ...)
  → _iea.EvaluateBreakEven(...)  // Enqueued to IEA worker
  → Worker: EvaluateBreakEvenCore
  → Executor.ModifyStopToBreakEven
  → ModifyStopToBreakEvenReal
  → account.Change(new[] { stopOrder })   // WRONG THREAD
```

**What happened:**

1. `Change()` was called from the IEA worker thread.
2. NinjaTrader expects `Change()` on the strategy thread (OnBarUpdate/OnMarketData context).
3. NT processed changes out of order or with stale state.
4. OrderUpdate (strategy thread) saw stop at 49604; worker kept retrying Change to 49305.
5. Stop oscillated: 49604 (range_high) ↔ 49305 (BE target).
6. Confirmation never arrived within timeout → retries exhausted → `STOP_MODIFY_FAILED` → `StandDownStream`.

### Retry and Timeout Constants

| Constant | Value | Location |
|----------|-------|----------|
| BE_CONFIRM_TIMEOUT_SEC | 15 | NinjaTraderSimAdapter.cs |
| BE_RETRY_INTERVAL_SEC | 5 | NinjaTraderSimAdapter.cs |
| BE_MAX_RETRY_ATTEMPTS | 3 | NinjaTraderSimAdapter.cs |
| BE_MODIFY_ATTEMPT_INTERVAL_MS | 200 | IEA / Adapter |
| BE_MODIFY_MAX_RETRIES (IEA) | 25 | InstrumentExecutionAuthority.NT.cs |

The ping-pong path hits the **adapter timeout** (15s × 3 retries) because `ModifyStopToBreakEven` returns Success (Change was called) but OrderUpdate never confirms the new stop price.

---

## Part 2: Why BE Did Not Fire Again at 9 PM

After `StandDownStream` at 12:48 PM, the stream entered `RecoveryManage` → `Commit` → stream state DONE. The position was **not** flattened by stand-down.

### Empirical Finding from Logs

**BE did run again** at 19:03 UTC (1:03 PM Chicago), ~15 minutes after stand-down. Logs show repeated `BE_TRIGGER_REACHED` and `STOP_MODIFY_REQUESTED` for intent 8f962ae805d0a42d. Stand-down did **not** stop BE evaluation — the intent remained in `GetActiveIntentsForBEMonitoring`. The stop still did not stick (same threading issue; fix may not have been deployed). For **9 PM Chicago** (03:00 UTC) specifically, check logs in that window; session may have ended before then.

### Possible Reasons BE Did Not Run at 9 PM

| # | Reason | How to Verify |
|---|--------|---------------|
| 1 | **Position was closed** | Check `hasExposure` / Account.Positions. If flattened (manual or other), BE path returns early. |
| 2 | **BE gate blocked** | Search logs for `BE_GATE_BLOCKED` around 03:00 UTC (9 PM Chicago). Reasons: WRONG_MARKETDATA_TYPE, INIT_FAILED, ENGINE_NOT_READY, STATE_NOT_REALTIME, INSTRUMENT_MISMATCH. |
| 3 | **Execution instrument mismatch** | `BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE`: account has exposure but `GetActiveIntentsForBEMonitoring` returned 0. Chart instrument vs intent ExecutionInstrument. |
| 4 | **Strategy restarted** | Session end, NT restart, or chart reload clears state. IntentMap would be empty. |
| 5 | **No ticks** | OnMarketData(Last) not firing for MYM. Check `ONMARKETDATA_LAST_DIAG`, `BE_PATH_ACTIVE`. |
| 6 | **Intent no longer active** | `GetActiveIntentsForBEMonitoring` filters by: execution instrument, journal EntryFilled, NOT IsBEModified. Stand-down does **not** remove intent from IntentMap. |

### BE Evaluation Flow (Reference)

```
OnMarketData(Last)
  → Gates: hasExposure, !_initFailed, _engineReady, State==Realtime, etc.
  → _adapter.EvaluateBreakEven(tickPrice, tickTimeFromEvent, executionInstrument)
  → CheckPendingBETimeouts()  // First: handle any pending BE timeouts
  → IEA: EvaluateBreakEvenDirect (strategy thread, under EntrySubmissionLock)
  → GetActiveIntentsForBEMonitoring(executionInstrument)
  → For each intent: if price crossed BE trigger → ModifyStopToBreakEven
```

### Log Search Commands (9 PM Chicago = 03:00 UTC)

```powershell
# BE activity around 9 PM Chicago
Select-String -Path "logs/robot/robot_MYM.jsonl","logs/robot/robot_YM.jsonl" -Pattern "BE_|STOP_MODIFY|STREAM_STAND_DOWN" | Where-Object { $_.Line -match "02:5[5-9]|03:0[0-5]" }

# BE gates
Select-String -Path "logs/robot/robot_*.jsonl" -Pattern "BE_GATE_BLOCKED|BE_PATH_ACTIVE|BE_FILTER_EXCLUDED" | Where-Object { $_.Line -match "02:5[5-9]|03:0[0-5]" }

# Stand-down and exposure
Select-String -Path "logs/robot/robot_ENGINE.jsonl" -Pattern "STREAM_STAND_DOWN|StandDown" | Where-Object { $_.Line -match "18:4[5-9]|18:5[0-9]|19:0[0-5]" }
```

---

## Part 3: Fix Implemented

**File**: `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs`

```csharp
// NT THREADING FIX: order.Change() must run on strategy thread (OnMarketData context).
if (_useInstrumentExecutionAuthority && _iea != null)
{
    lock (_iea.EntrySubmissionLock)
    {
        var eventTime = tickTimeFromEvent ?? DateTimeOffset.UtcNow;
        var hasEventTime = tickTimeFromEvent.HasValue;
        _iea.EvaluateBreakEvenDirect(tickPrice, eventTime, hasEventTime, executionInstrument);
    }
}
```

**Result**: BE evaluation runs on the strategy thread. `ModifyStopToBreakEven` → `account.Change()` executes in OnMarketData context. No more ping-pong from wrong-thread Change.

---

## Part 4: Verification Checklist

1. **Rebuild and deploy** to NinjaTrader bin.
2. **Run MYM** with BE trigger (e.g. 65%).
3. **Confirm**: `STOP_MODIFY_REQUESTED` → `STOP_MODIFY_CONFIRMED` (no repeated ChangePending 49604).
4. **Confirm**: Stop stays at BE (49305 for this intent).
5. **If 9 PM recurrence**: Run log search above; check BE_GATE_BLOCKED, BE_PATH_ACTIVE, and exposure state.

---

## Related Documents

- `2026-02-20_MYM_BREAKEVEN_PINGPONG_INVESTIGATION.md` — Ping-pong analysis and fix
- `docs/robot/IEA_IMPLEMENTATION_GAPS_AND_DECISIONS.md` — IEA threading patterns
