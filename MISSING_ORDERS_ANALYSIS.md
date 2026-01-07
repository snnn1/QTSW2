# Missing Orders at 15:00 UTC - Complete Analysis

## Summary

**Root Cause:** Timetable file is being updated with invalid `trading_date` (empty string) during the 15:00 UTC window, causing all engines to call `StandDown()` and clear streams.

## Timeline from Logs

```
14:30:05 UTC - TIMETABLE_UPDATED (valid)
14:33:34 UTC - EXECUTION_SUMMARY_WRITTEN (engines stopping)
14:37:04 UTC - EXECUTION_MODE_SET (new engines starting)
15:00:05 UTC - TRADING_DAY_ROLLOVER (multiple engines)
15:01:06 UTC - TIMETABLE_UPDATED → TIMETABLE_INVALID: BAD_TRADING_DATE (trading_date="")
15:02:05 UTC - TIMETABLE_UPDATED → TIMETABLE_INVALID: BAD_TRADING_DATE (repeated)
15:06:06 UTC - TIMETABLE_UPDATED → TIMETABLE_INVALID: BAD_TRADING_DATE
15:07:09 UTC - TIMETABLE_UPDATED → TIMETABLE_INVALID: BAD_TRADING_DATE
15:08:05 UTC - TIMETABLE_UPDATED → TIMETABLE_INVALID: BAD_TRADING_DATE
15:10:05 UTC - TIMETABLE_UPDATED → TIMETABLE_INVALID: BAD_TRADING_DATE
```

## What's Happening

1. **Multiple engines running** - One per instrument (ES, NG, CL, etc.)
2. **Timetable file being updated** - `TIMETABLE_UPDATED` events show file changes
3. **Invalid trading_date** - Some updates have empty `trading_date=""`
4. **Engines call StandDown()** - All streams cleared when timetable invalid
5. **No streams exist** - No orders can be submitted

## Current Timetable State

**File:** `data/timetable/timetable_current.json`
- ✅ `trading_date: "2026-01-05"` (valid)
- ✅ `timezone: "America/Chicago"` (correct)
- ✅ `streams: 14` (has streams)

**BUT:** The file is being updated during runtime, and some updates have invalid `trading_date`.

## Possible Causes

### 1. Race Condition During File Write
- Timetable file is being written while engines are reading it
- Partial writes result in empty `trading_date`
- Multiple engines read the file simultaneously

### 2. Timetable Generation Bug
- The timetable generator is sometimes producing files with empty `trading_date`
- This happens intermittently during updates

### 3. File Lock/Contention
- Multiple processes writing to the same file
- File corruption or partial writes

## Solution

### Immediate Fix

1. **Check timetable generation code** - Ensure `trading_date` is always set
2. **Add file locking** - Prevent concurrent writes
3. **Validate before write** - Ensure `trading_date` is valid before writing file
4. **Add retry logic** - If timetable invalid, retry reading after delay

### Long-term Fix

1. **Atomic file writes** - Write to temp file, then rename
2. **Timetable validation** - Validate timetable before writing
3. **Better error handling** - Don't call `StandDown()` on transient errors
4. **Timetable versioning** - Track timetable versions to detect corruption

## Why Orders Didn't Submit

1. ✅ Timetable loads initially (valid)
2. ❌ Timetable gets updated with invalid `trading_date`
3. ❌ Engine calls `StandDown()` → clears all streams
4. ❌ No streams exist → no entry detection → no orders

## Next Steps

1. **Check timetable generator** - Verify it always sets `trading_date`
2. **Check file write process** - Ensure atomic writes
3. **Add validation** - Validate timetable before writing
4. **Monitor logs** - Watch for `TIMETABLE_INVALID` events

## Files to Check

- Timetable generation code (Python)
- File write process
- `data/timetable/timetable_current.json` (check for empty `trading_date`)
