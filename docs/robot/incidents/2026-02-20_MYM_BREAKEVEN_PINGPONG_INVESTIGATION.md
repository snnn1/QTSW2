# MYM Break-Even Not Sticking — Stop Ping-Pong (49604↔49305)

**Date**: 2026-02-20  
**Stream**: YM1  
**Instrument**: MYM  
**Intent**: 8f962ae805d0a42d

---

## Summary

User reported: MYM hit 65% (BE trigger) but nothing changed. Logs show:

1. **Stop oscillating** between 49604 and 49305 (ChangePending 49604 → ChangeSubmitted 49305 → Working → ChangePending 49604 → …)
2. **BE modification repeatedly attempted** but stop never stayed at BE
3. **Root cause**: IEA worker thread calling `account.Change()` — same NT threading violation as entry submission

---

## Log Evidence

| Time (UTC) | Event | stop_price |
|------------|-------|------------|
| 18:42:18 | Protective stop submitted | 49305 |
| 18:48:01 | ChangePending | 49604 |
| 18:48:01 | ChangeSubmitted | 49305 |
| 18:48:01 | Working | 49305 |
| 18:48:01 | ChangePending | 49604 |
| 18:48:01 | ChangeSubmitted | 49305 |
| … | … | … |

Pattern repeats every ~100–300ms for several seconds.

---

## Price Analysis

- **49604** = range_high from RANGE_LOCKED (YM1, MYM). Original protective stop for short.
- **49305** = BE stop (entry + tick for short). Correct BE target.
- **Ping-pong**: BE logic repeatedly Changes to 49305; something keeps reverting or NT shows stale state.

---

## Root Cause: IEA Worker Threading

| Operation | Before | NT API | Thread |
|-----------|--------|--------|--------|
| Entry submission | IEA worker | CreateOrder, Submit | **Fixed** (strategy thread) |
| **BE modification** | IEA worker | **order.Change()** | **Was worker** |

NinjaTrader requires `CreateOrder`, `Submit`, and **`Change()`** to run on the strategy thread (OnBarUpdate/OnMarketData context). IEA was running BE evaluation on the worker:

1. `EvaluateBreakEven()` → `Enqueue()` → worker runs `EvaluateBreakEvenCore`
2. `EvaluateBreakEvenCore` → `Executor.ModifyStopToBreakEven` → `ModifyStopToBreakEvenReal` → `account.Change(stopOrder)`

Calling `Change()` from the worker thread caused:
- Undefined behavior (NT may queue or process changes out of order)
- Race with OrderUpdate (strategy thread updates order state; worker reads stale)
- Repeated Change attempts because worker saw stale stop (49604) and kept retrying

---

## Fix Implemented

**Pattern**: Same as entry submission — run NT call on strategy thread.

1. **InstrumentExecutionAuthority.NT.cs**: `EvaluateBreakEvenDirect` now takes `hasEventTime` for correct dedupe.
2. **NinjaTraderSimAdapter.cs**: When IEA enabled, call `EvaluateBreakEvenDirect` under `EntrySubmissionLock` instead of `EvaluateBreakEven` (which enqueued).
3. **ReplayDriver.cs**: Updated call to pass `hasEventTime: true`.

**Result**: BE evaluation runs on the strategy thread (OnMarketData context). `ModifyStopToBreakEven` → `account.Change()` executes on the correct thread.

---

## Other IEA/NT Operations That May Need Same Fix

| Operation | Worker path? | NT APIs | Status |
|----------|--------------|---------|--------|
| Entry submission | Was worker | CreateOrder, Submit | **Fixed** |
| Protective orders | Worker | CreateOrder, Submit | Not fixed |
| BE modification | Was worker | Order.Change() | **Fixed** |
| Flatten | Worker | Submit | Not fixed |
| Aggregation | Worker | CancelOrders, Submit | Not fixed |

If protective orders, flatten, or aggregation show similar issues, apply the same pattern: run on strategy thread with lock.

---

## Files Changed

- `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` — `EvaluateBreakEvenDirect` signature
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` — BE path uses Direct + lock
- `RobotCore_For_NinjaTrader/Execution/ReplayDriver.cs` — Call site updated

---

## Verification

1. Rebuild and deploy to NinjaTrader bin.
2. Run MYM with BE trigger (e.g. 65%).
3. Confirm: STOP_MODIFY_REQUESTED → STOP_MODIFY_CONFIRMED.
4. Confirm: stop stays at BE (49305 for this intent); no repeated ChangePending 49604.
