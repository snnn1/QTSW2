# Pipeline Skip Issue - ES2 Dec 26 Not Updating

## Problem
ES2 trade on **December 26, 2025 at 11:00** is not updating to the latest ExitTime. The pipeline appears to be skipping updates.

## Root Cause Analysis

### Current State
- **Monthly file shows:** ExitTime = `28/12/25 17:59`
- **Dec 26 folder:** NOT in `analyzer_temp` (was deleted after processing)
- **Processed log:** Dec 26 is NOT in processed log (but Dec 27-29 ARE)

### How Deduplication Works
The merger deduplicates by: `[Date, Time, Session, Instrument, Stream]`

**Key point:** `ExitTime` is NOT in the deduplication key!

This means:
- If ExitTime changes but Date/Time/Session/Instrument/Stream stay the same → Still treated as "duplicate"
- The merger uses `keep='last'`, so NEW data should replace OLD data
- BUT: Only if the analyzer actually produces NEW data with different ExitTime

### What's Happening

1. **Analyzer produces data for Dec 26**
2. **Merger processes the folder:**
   - Loads existing monthly file (76 rows)
   - Loads new data from analyzer_temp (76 rows)
   - Removes all 76 rows as "duplicates" (same Date/Time/Session/Instrument/Stream)
   - Keeps the LAST occurrence (new data replaces old)
3. **Problem:** If analyzer produces SAME ExitTime → No actual update

### Possible Issues

1. **Analyzer not producing updated ExitTime**
   - Analyzer might be producing the same ExitTime (`28/12/25 17:59`)
   - No change = No update

2. **Folder being skipped**
   - Dec 26 folder was deleted after processing
   - If analyzer doesn't create a new folder, nothing to process

3. **Deduplication working correctly but no new data**
   - Merger correctly replaces old with new
   - But if new = old, no visible change

## Investigation Needed

Check if analyzer is producing **NEW** ExitTime for Dec 26:

1. **Check analyzer output:**
   - When analyzer runs for Dec 26, does it produce ExitTime = `29/12/25 XX:XX` (newer)?
   - Or does it still produce ExitTime = `28/12/25 17:59` (same)?

2. **Check if folder is being created:**
   - Does `data/analyzer_temp/2025-12-26/` get created when analyzer runs?
   - Or is it being skipped?

3. **Check processed log:**
   - Why is Dec 26 NOT in processed log?
   - Was the log reset/cleared?

## Solution Options

### Option 1: Include ExitTime in deduplication key (NOT RECOMMENDED)
- Would treat same Date/Time with different ExitTime as different trades
- But this might create duplicates if ExitTime changes multiple times

### Option 2: Check if analyzer is producing updated data
- Verify analyzer is actually producing NEW ExitTime
- If analyzer produces same data, that's the real issue

### Option 3: Force reprocess Dec 26
- Manually create `data/analyzer_temp/2025-12-26/` folder
- Run analyzer for Dec 26
- Let merger process it

## Next Steps

1. **Check analyzer logs** for Dec 26 runs
2. **Verify analyzer is producing updated ExitTime**
3. **Check if folder is being created** in analyzer_temp
4. **Investigate why Dec 26 is not in processed log**

