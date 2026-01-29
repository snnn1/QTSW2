# NG1 Intent Registration Fix

## Problem
NG1 entry orders were filled, but protective orders were not placed because intents were not registered before order submission.

## Root Cause
In `SubmitEntryOrder()` method, `RegisterIntent()` was being called AFTER order submission (only if successful). This created a race condition:
- If order fills immediately, the intent might not be registered yet
- If order submission fails, intent is never registered

## Fix Applied

### 1. SubmitStopBracketsAtLock() (Range Lock Path)
- **Location**: `StreamStateMachine.cs` line ~3345
- **Change**: Added error logging if execution adapter is not `NinjaTraderSimAdapter`
- **Status**: Intent registration was already BEFORE order submission ✓

### 2. SubmitEntryOrder() (Breakout Entry Path)
- **Location**: `StreamStateMachine.cs` line ~4577
- **Change**: Moved `RegisterIntent()` call BEFORE `SubmitEntryOrder()` call
- **Before**: Intent registered AFTER order submission (inside `if (entryResult.Success)`)
- **After**: Intent registered BEFORE order submission
- **Status**: Fixed ✓

## Files Modified
1. `modules/robot/core/StreamStateMachine.cs`
2. `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

## Testing
After rebuild and restart:
1. Verify `INTENT_REGISTERED` events appear BEFORE `ORDER_SUBMITTED` events
2. Verify protective orders are placed when entry orders fill
3. Check logs for `EXECUTION_ERROR` events if adapter type check fails

## Related Issues
- Same fix applies to NQ1 and all other streams
- Ensures protective orders are always placed on entry fill
