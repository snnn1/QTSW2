# TRADING_DAY_ROLLOVER Spam Fix

## Problem

After restarting the robot, streams were experiencing:
1. **728 TRADING_DAY_ROLLOVER events in 5 minutes** - causing excessive logging
2. **Streams resetting repeatedly** - preventing transition from ARMED to RANGE_BUILDING
3. **Empty date fields** - rollover events showing `previous_trading_date: NONE, new_trading_date: NONE`

## Root Cause

When `UpdateTradingDate()` was called:
- If `_journal.TradingDate` was empty/null (initialization), it was treated as a rollover
- This caused streams to reset to `PRE_HYDRATION` state repeatedly
- Each reset cleared the bar buffer and reset `_preHydrationComplete = false`
- Streams couldn't stay in ARMED long enough to transition to RANGE_BUILDING

## Solution

Added a guard in `UpdateTradingDate()` to distinguish between:
1. **Initialization** - when `previousTradingDateStr` is empty/null
   - Just updates journal and times
   - Does NOT reset state or clear buffers
   - Logs `TRADING_DATE_INITIALIZED` instead of `TRADING_DAY_ROLLOVER`

2. **Actual Rollover** - when both dates are valid and different
   - Performs full reset (state, buffers, flags)
   - Logs `TRADING_DAY_ROLLOVER` as before

## Code Changes

**File:** `modules/robot/core/StreamStateMachine.cs`

**Change:** Added `isInitialization` check before reset logic:
```csharp
// GUARD: If previous trading date is empty/null, this is initialization, not a rollover
var isInitialization = string.IsNullOrWhiteSpace(previousTradingDateStr);

// Only reset state and clear buffers if this is an actual rollover (not initialization)
if (!isInitialization)
{
    // ... full reset logic ...
}
else
{
    // Initialization: Just update journal and times, don't reset state
    LogHealth("INFO", "TRADING_DATE_INITIALIZED", ...);
}
```

## Expected Impact

1. **Reduced log spam** - No more 728 rollover events on startup
2. **Streams can transition** - Streams stay in ARMED state and can transition to RANGE_BUILDING
3. **Proper initialization** - First-time setup doesn't trigger rollover logic

## Next Steps

1. Recompile in NinjaTrader
2. Restart the robot
3. Monitor logs to confirm:
   - `TRADING_DATE_INITIALIZED` events instead of rollover spam
   - Streams transitioning ARMED → RANGE_BUILDING
   - `RANGE_WINDOW_STARTED` events appearing

## Status

✅ Fix implemented
✅ Synced to RobotCore_For_NinjaTrader
✅ Ready for compilation and testing
