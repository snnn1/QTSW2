# Update Strategy Analysis: Always Update vs. Check for Changes

## Current Behavior

The merger **already always updates** when the deduplication key matches:
- Deduplication key: `[Date, Time, Session, Instrument, Stream]`
- Uses `keep='last'` → **Always keeps the new data, replaces old data**
- The recent change just adds **logging** to distinguish updates vs true duplicates

## Option 1: Always Update (Current Approach)

**What it does:**
- When dedup key matches → Replace old with new (regardless of whether data changed)
- No checking if profit/ExitTime/ExitPrice are different

**Pros:**
- ✅ Simple logic - no complex comparisons
- ✅ Always gets latest data from analyzer
- ✅ Handles edge cases (e.g., analyzer produces same data but with different precision)
- ✅ Ensures data freshness
- ✅ Fast - no field-by-field comparison needed
- ✅ Works correctly for your use case (overlapping pipeline exports)

**Cons:**
- ❌ Can't detect if analyzer is producing stale/unchanged data
- ❌ Unnecessary writes if data hasn't changed (minor performance cost)
- ❌ Harder to debug - can't tell if data actually changed
- ❌ Loses ability to detect true duplicates vs updates

## Option 2: Check for Changes Before Updating

**What it would do:**
- When dedup key matches → Check if profit/ExitTime/ExitPrice differ
- Only update if fields differ
- Keep old data if everything is the same

**Pros:**
- ✅ Can detect if analyzer is producing stale data
- ✅ Avoids unnecessary writes
- ✅ Better debugging - know when data actually changed
- ✅ Can detect true duplicates

**Cons:**
- ❌ More complex logic
- ❌ Slower - needs to compare fields
- ❌ What if other fields change? (EntryPrice, Peak, Result, etc.)
- ❌ Risk: If analyzer produces updated data but profit/ExitTime/ExitPrice happen to match, update is skipped
- ❌ Doesn't solve your problem - if profit/ExitTime/ExitPrice are the same, update is skipped even if other fields changed

## Option 3: Include More Fields in Deduplication Key

**What it would do:**
- Add profit/ExitTime/ExitPrice to deduplication key
- Only treat as duplicate if ALL fields match

**Pros:**
- ✅ True duplicates only when everything matches
- ✅ Updates when any field changes
- ✅ Clear semantics

**Cons:**
- ❌ Creates multiple rows for same trade if profit/ExitTime/ExitPrice change
- ❌ Breaks the "one row per Date/Time/Session/Instrument/Stream" invariant
- ❌ Could create duplicates in matrix if ExitTime changes multiple times
- ❌ Not what you want - you want ONE row per trade slot, updated over time

## Recommendation: Keep Current Approach (Always Update)

**Why:**
1. **Your use case:** Overlapping pipeline exports every 15 minutes
   - Analyzer might produce updated ExitTime/profit as trade progresses
   - You want the latest data, not to keep old data

2. **Current behavior is correct:**
   - `keep='last'` already replaces old with new
   - This is what you want for overlapping exports

3. **The logging addition helps:**
   - Distinguishes updates vs true duplicates for debugging
   - Doesn't change behavior, just adds visibility

4. **Performance:**
   - Field-by-field comparison is slower
   - Unnecessary writes are minor cost compared to I/O

## What You Should Do

**Keep the current approach** (always update when dedup key matches) because:
- ✅ It's already working correctly
- ✅ Handles overlapping exports properly
- ✅ Simple and fast
- ✅ The logging addition helps you see when updates happen

**If you want to detect stale data:**
- Use the logging to see if updates are happening
- Check if profit/ExitTime/ExitPrice are changing
- Don't prevent updates - just monitor them

## The Real Issue

The problem isn't the update strategy - it's that:
1. **Manual analyzer runs** go to `data/manual_analyzer_runs/` (not processed by merger)
2. **Pipeline analyzer runs** go to `data/analyzer_temp/` (processed by merger)
3. Your updated data is in manual runs, not pipeline runs

**Solution:** Make sure analyzer runs through the pipeline (not manual) so data goes to `analyzer_temp` and gets merged.

