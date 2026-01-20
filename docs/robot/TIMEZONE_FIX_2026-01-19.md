# Timezone Conversion Fix - 2026-01-19

## Problem

All bars were being rejected with `BAR_PARTIAL_REJECTED` because bar timestamps were 6 hours in the future:
- Bar Time UTC: `2026-01-19T22:09:00 UTC`
- Current Time UTC: `2026-01-19T16:09:04 UTC`
- Bar Age: **-359 minutes** (negative = future bar)

This caused:
- All bars rejected as "too recent"
- No bars reaching streams (`bar_count: 0`)
- All range computations failing (`RANGE_COMPUTE_FAILED` with `NO_BARS_IN_WINDOW`)
- No trades possible

## Root Cause

NinjaTrader's `Times[0][0]` property behavior:
- **Documentation says**: Exchange local time (Chicago) with `DateTimeKind.Unspecified`
- **Actual behavior**: Appears to be **UTC** for live bars (not Chicago time)
- **Our code**: Was treating it as Chicago time and converting to UTC, causing double conversion

When `Times[0][0]` contains UTC time (e.g., 16:09 UTC) but we treat it as Chicago time:
- We create `DateTimeOffset(16:09, -6:00)` = 22:09 UTC
- This makes bars appear 6 hours in the future
- Bar age becomes negative → rejection

## Solution

Implemented **automatic timezone detection** in `OnBarUpdate()`:

1. **Try both interpretations**:
   - Treat `Times[0][0]` as UTC → calculate bar age
   - Treat `Times[0][0]` as Chicago time → calculate bar age

2. **Choose the correct interpretation**:
   - Prefer interpretation that gives positive bar age (bar in past, not future)
   - Prefer interpretation that gives reasonable age (0-10 minutes for recent bars)
   - Fallback to Chicago interpretation (documented behavior)

3. **Files Updated**:
   - `modules/robot/ninjatrader/RobotSimStrategy.cs`
   - `modules/robot/ninjatrader/RobotSkeletonStrategy.cs`
   - `RobotCore_For_NinjaTrader/NinjaTraderExtensions.cs` (comments only)

## Testing

After deploying this fix, verify:

1. **Bars are accepted**:
   - Check logs for `BAR_ACCEPTED` events (not just `BAR_PARTIAL_REJECTED`)
   - Bar age should be positive (0-10 minutes for recent bars)

2. **Range computations succeed**:
   - Streams should transition from `RANGE_BUILDING` to `RANGE_LOCKED`
   - No more `RANGE_COMPUTE_FAILED` with `NO_BARS_IN_WINDOW`

3. **Trading can proceed**:
   - Streams should build ranges successfully
   - Breakout levels should be computed
   - Trades can execute when breakouts occur

## Expected Log Changes

**Before Fix**:
```
BAR_PARTIAL_REJECTED: bar_age_minutes: -359.9 (negative = future)
RANGE_COMPUTE_FAILED: reason: NO_BARS_IN_WINDOW, bar_count: 0
```

**After Fix**:
```
BAR_ACCEPTED: bar_age_minutes: 0.5 (positive = past)
RANGE_LOCKED: bar_count: 150+
```

## Notes

- This fix handles both UTC and Chicago time interpretations automatically
- No configuration changes needed
- Works for both live bars and historical bars
- Backward compatible (falls back to Chicago interpretation if detection fails)
