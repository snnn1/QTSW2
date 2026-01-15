# Rollover Spam - Root Cause Found

## Problem
Seeing 728 rollover events cycling through historical dates:
- 2026-01-14 -> 2026-01-01 (56 times)
- 2026-01-01 -> 2026-01-02 (56 times)
- 2026-01-02 -> 2026-01-04 (56 times)
- ...continuing through multiple dates

## Root Cause
**Bars are arriving with incorrect/inconsistent dates**, causing `UpdateTradingDate()` to be called repeatedly as `_activeTradingDate` changes with each bar.

Each time a bar arrives with a different date:
1. `_activeTradingDate != barChicagoDate` evaluates to true
2. `UpdateTradingDate()` is called for all streams
3. Streams reset to PRE_HYDRATION
4. Process repeats with next bar

## Why This Happens
Possible causes:
1. **Replay mode** - Historical bars with different dates
2. **Date conversion bug** - `GetChicagoDateToday()` returning wrong dates
3. **Bar timestamp issues** - Bars arriving with incorrect timestamps

## Current Status
- ✅ Fix prevents initialization spam (when `_activeTradingDate` is null)
- ❌ Still seeing rollovers because bars have different dates
- ❌ Streams keep resetting because each date change triggers rollover

## Next Steps
1. Check if replay mode is enabled
2. Verify bar timestamps are correct
3. Consider adding date validation/throttling to prevent rapid rollovers
4. Or: Accept rollovers in replay mode but prevent state reset if date is in the past
