# ES2 Dec 26, 2025 11:00 Update Issue - FIXED

## Problem Identified

The ES2 trade on **December 26, 2025 at 11:00** was not showing the correct exit time in the master matrix.

### What Was Wrong

1. **Analyzer Data (Correct):**
   - EntryTime: `26/12/25 11:08`
   - ExitTime: `28/12/25 17:59` ✅

2. **Master Matrix (Incorrect):**
   - entry_time: `11:00` ✅ (correct - this is the Time slot)
   - exit_time: `11:00` ❌ (WRONG - should be `28/12/25 17:59`)

### Root Cause

The issue was in `modules/matrix/schema_normalizer.py` at lines 109-111:

**Before (BROKEN):**
```python
if 'exit_time' not in df.columns:
    # For now, set exit_time same as entry_time (would need actual exit logic)
    df['exit_time'] = df['entry_time'] if 'entry_time' in df.columns else ''
```

The schema normalizer was **ignoring** the `ExitTime` column from the analyzer data and just copying `entry_time` instead.

### Fix Applied

**After (FIXED):**
```python
if 'exit_time' not in df.columns:
    # Use ExitTime from analyzer if available, otherwise fallback to entry_time
    if 'ExitTime' in df.columns:
        df['exit_time'] = df['ExitTime']
    else:
        df['exit_time'] = df['entry_time'] if 'entry_time' in df.columns else ''
```

Now the schema normalizer:
1. ✅ Checks if `ExitTime` exists in analyzer data
2. ✅ Uses `ExitTime` if available
3. ✅ Falls back to `entry_time` only if `ExitTime` is missing

## What You Need To Do

1. **Rebuild or Update the Matrix:**
   - Click "Rebuild Matrix (Full)" OR
   - Click "Update Matrix (Rolling 35-Day Window)"
   
2. **Verify the Fix:**
   - After rebuild/update, check ES2 Dec 26, 2025 11:00 row
   - `exit_time` should now show: `28/12/25 17:59` (or formatted as `15:58` if time-only)

## Impact

This fix affects **all trades** that have `ExitTime` in the analyzer data. Previously, all exit times were being set to the entry time slot (e.g., `11:00`) instead of the actual exit time from the analyzer.

## Files Changed

- `modules/matrix/schema_normalizer.py` - Fixed `exit_time` mapping to use `ExitTime` from analyzer

## Testing

After rebuilding the matrix, verify:
- ES2 Dec 26, 2025 11:00 shows correct exit time
- All other trades show correct exit times (not just the entry time slot)

