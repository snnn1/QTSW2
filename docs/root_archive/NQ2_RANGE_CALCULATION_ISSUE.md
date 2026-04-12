# NQ2 Range Calculation Issue

## Problem

NQ2's range was calculated incorrectly as **[26170.25, 26197]** but should be **[25536, 26197]**.

## Root Cause

The range was calculated with only **3 bars** instead of the expected **180 bars**.

**Evidence:**
- Range window: 08:00:00 to 11:00:00 Chicago time (180 minutes)
- Expected bar count: 180 bars (1-minute bars)
- Actual bar count: **3 bars**
- Range calculated: [26170.25, 26197]
- Correct range should be: [25536, 26197]

## Analysis

**What happened:**
1. Range calculation ran at 17:00:43 (11:00:43 Chicago time)
2. Only 3 bars were in the buffer when `ComputeRangeRetrospectively` ran
3. Those 3 bars had:
   - High: 26197 ✅ (correct)
   - Low: 26170.25 ❌ (should be 25536)
4. With only 3 bars, the range calculation couldn't find the true low

**Why restoration shows wrong range:**
- Restoration is working correctly ✅
- It restored exactly what was locked: [26170.25, 26197]
- The problem is the **original calculation**, not restoration

## Questions to Investigate

1. **Why only 3 bars in buffer?**
   - Were bars not loaded during pre-hydration?
   - Were bars filtered out incorrectly?
   - Did NinjaTrader not provide bars for the 08:00-11:00 window?

2. **Bar loading/pre-hydration:**
   - Check if pre-hydration completed successfully
   - Check if bars were requested from NinjaTrader for the correct time window
   - Check if bars were filtered out by date/time window logic

3. **Bar filtering:**
   - Check `RANGE_COMPUTE_BAR_FILTERING` logs to see how many bars were filtered
   - Verify time window filtering logic is correct
   - Check if bars were filtered by trading date

## Next Steps

1. Check pre-hydration logs for NQ2 to see how many bars were loaded
2. Check bar filtering logs to see why bars were excluded
3. Verify NinjaTrader provided bars for the 08:00-11:00 window
4. Check if there's a timezone conversion issue causing bars to be filtered out

## Status

- ✅ **Restoration is working correctly** - it restored what was actually locked
- ❌ **Original calculation was wrong** - only 3 bars used instead of 180
- 🔍 **Investigation needed** - why weren't bars loaded/filtered correctly?
