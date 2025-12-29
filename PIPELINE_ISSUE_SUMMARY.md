# Pipeline Issue: ES2 Dec 26 Not Updating

## Key Finding

The merger logs show:
```
Found 76 true duplicate rows (same dedup key AND same profit/ExitTime/ExitPrice)
```

This means: **The analyzer IS running and producing data, but it's producing the SAME data** (same profit/ExitTime/ExitPrice) as what's already in the monthly file.

## Root Cause

The analyzer processes ALL dates in the translated data folder, but:
1. **It writes output to `analyzer_temp/{today}`** (today's date folder)
2. **It processes ALL dates** in the translated data (including Dec 26)
3. **But it's producing the SAME ExitTime/profit** as what's already in the monthly file

## Why No Update?

The merger correctly:
- Loads existing monthly file (76 rows)
- Loads new analyzer data (76 rows)
- Checks if profit/ExitTime/ExitPrice differ
- Finds they're the SAME → logs as "true duplicates"
- Replaces old with new (but new = old, so no change)

## The Real Question

**Why is the analyzer producing the same ExitTime/profit for Dec 26?**

Possible reasons:
1. **Analyzer is using old/cached data** - not reading latest translated data
2. **Translated data hasn't been updated** - Dec 26 data in translated folder is still old
3. **Analyzer logic** - ExitTime calculation hasn't changed because trade hasn't progressed
4. **Data source** - The raw/translated data for Dec 26 hasn't been updated

## Next Steps to Investigate

1. **Check translated data for Dec 26:**
   - Is `data/translated/ES/1m/` updated with latest Dec 26 data?
   - Does it include data up to the latest ExitTime?

2. **Check analyzer input:**
   - What date range does analyzer see when it runs?
   - Does it include Dec 26 with updated data?

3. **Check if ExitTime should have changed:**
   - What should the ExitTime be? (You mentioned it should be `26/12/25 15:58` not `28/12/25 17:59`)
   - Is the translated data correct for that time?

## Solution

The merger is working correctly. The issue is that **the analyzer is producing the same data** because:
- Either the translated data hasn't been updated
- Or the analyzer isn't seeing the updated translated data
- Or the ExitTime hasn't actually changed (trade closed at same time)

