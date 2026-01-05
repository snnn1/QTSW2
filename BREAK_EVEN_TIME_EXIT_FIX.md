# Break-Even Stop Loss Time Exit Fix

## Issue Description

A YM trade on **02/01/2026 at 09:00** exited with **TIME** instead of **BE** even though:
1. T1 trigger was activated (65% of target reached)
2. Stop loss was adjusted to break-even (entry_price - tick_size)
3. Price reversed back to hit the break-even stop loss
4. Trade should have exited with BE but instead waited until time expiry

## Root Cause

The issue was in `modules/analyzer/logic/price_tracking_logic.py` in the trade execution loop:

1. **Execution Check**: For each bar, the code checks if target or stop loss is hit using `_simulate_intra_bar_execution()`
2. **Time Expiry Check**: After checking execution, if time expires, the code exits with TIME
3. **Problem**: If `_simulate_intra_bar_execution()` returned `None` (no execution detected in that bar), the code would continue to the time expiry check
4. **Missing Check**: The code didn't verify if the break-even stop loss was hit in ANY previous bar before checking time expiry

### Why Execution Check Might Return None

The execution check can return `None` in certain scenarios:
- When T1 just triggered in the current bar (line 66-68 in execution_logic.py)
- When neither target nor stop loss is clearly hit in a bar (edge cases)
- When the break-even stop loss is very close to entry price and bar granularity causes detection issues

## Fix Applied

Added checks **before time expiry** in BOTH expiry code paths to verify if the break-even stop loss was hit in any bar before expiry time:

**Location**: 
- `modules/analyzer/logic/price_tracking_logic.py` lines 482-558 (during loop expiry path)
- `modules/analyzer/logic/price_tracking_logic.py` lines 679-758 (after loop expiry path)

**Logic**:
1. When time expiry is detected, check if T1 was triggered and stop was adjusted to break-even
2. If so, scan all bars before expiry time to see if break-even stop loss was hit
3. If break-even stop loss was hit, exit with **BE** instead of **TIME**
4. Use the timestamp when break-even stop loss was hit as the exit time

**Key Changes**:
```python
# Before exiting with TIME, check if break-even stop loss was hit in any bar before expiry
if t1_triggered and stop_loss_adjusted:
    # Check all bars before expiry time to see if break-even stop loss was hit
    bars_before_expiry = after[after["timestamp"] < expiry_time]
    be_stop_hit = False
    be_stop_hit_time = None
    
    for _, prev_bar in bars_before_expiry.iterrows():
        # Check if break-even stop loss was hit
        if direction == "Long":
            if prev_low <= current_stop_loss:
                be_stop_hit = True
                be_stop_hit_time = prev_bar["timestamp"]
                break
        else:
            if prev_high >= current_stop_loss:
                be_stop_hit = True
                be_stop_hit_time = prev_bar["timestamp"]
                break
    
    if be_stop_hit:
        # Exit with BE instead of TIME
        exit_reason = "BE"
        # ... rest of BE exit logic
```

## Impact

This fix ensures that:
- ✅ Trades that hit break-even stop loss before time expiry will correctly exit with **BE**
- ✅ Trades that don't hit break-even stop loss will still exit with **TIME** as expected
- ✅ The exit time reflects when break-even stop loss was actually hit, not when time expired

## Testing

To verify the fix works:
1. Re-run the analyzer for the affected trade (YM 02/01/2026 09:00)
2. Check that the trade now exits with **BE** instead of **TIME**
3. Verify the exit time matches when break-even stop loss was hit

## Related Files

- `modules/analyzer/logic/price_tracking_logic.py` - Main fix location
- `modules/analyzer/logic/execution_logic.py` - Execution simulation logic
- `modules/analyzer/logic/break_even_logic.py` - Break-even trigger logic
