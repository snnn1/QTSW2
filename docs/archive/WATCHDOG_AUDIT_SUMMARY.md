# Watchdog System Audit Summary

## Issues Identified

### 1. **Event Reading Logic Broken** (CRITICAL)
**Problem:** `_read_feed_events_since()` used byte-position tracking that started reading from old positions, causing it to:
- Miss new run_ids that weren't in the cursor
- Process events for old run_ids while skipping new ones
- Fail to catch recent events when cursor was ahead

**Fix:** Changed to read last 5000 lines from end of file instead of using byte-position tracking. This ensures:
- All recent events are processed regardless of cursor position
- New run_ids are automatically detected and processed
- Events are sorted chronologically before processing

### 2. **State Not Initializing on Startup** (CRITICAL)
**Problem:** State manager started with all fields as None/Unknown, and only connection status was being rebuilt on startup.

**Fix:** Enhanced startup initialization to:
- Initialize engine tick from recent ticks
- Initialize bar tracking from recent bars
- Initialize connection status (already existed)
- All initialization happens synchronously before processing begins

### 3. **End-of-File Reads May Fail**
**Problem:** End-of-file reads for ticks/bars might not be finding events correctly.

**Status:** Code looks correct, but needs verification after restart. The end-of-file reads are still used as a backup, but the main fix (reading last 5000 lines) should handle most cases.

## Files Modified

1. **`modules/watchdog/aggregator.py`**
   - Fixed `_read_feed_events_since()` to read from end of file (last 5000 lines)
   - Enhanced startup initialization to rebuild all state from recent events
   - Added event sorting to ensure chronological processing

## Root Cause

The watchdog was using byte-position tracking that caused it to read from old file positions. When new run_ids appeared, they weren't being processed because:
1. The cursor didn't have them yet
2. The byte-position tracking started from old positions
3. Events for new run_ids were skipped

## Solution

Changed to read the last 5000 lines from the end of the file on each processing cycle. This ensures:
- Recent events are always processed
- New run_ids are automatically detected
- State stays current regardless of cursor position

## Additional Fix Applied

**Enhanced Startup Initialization:** Changed initialization to always run on startup, not just when fields are None. This ensures state is always rebuilt from recent events when the backend starts.

## Next Steps

**RESTART THE WATCHDOG BACKEND** to apply fixes:

1. Stop the current watchdog backend process
2. Restart it to load the new code
3. Verify status shows:
   - `engine_alive: True` (if ticks are recent)
   - `connection_status: Connected` (or appropriate status)
   - `worst_last_bar_age_seconds: < 120` (if bars are recent)
   - `last_identity_invariants_pass: True/False` (if identity events exist)

## Verification

After restart, run:
```bash
python verify_watchdog_fixes.py
```

This will check if all status fields are initialized correctly.

## Expected Behavior After Fix

- Events for new run_ids are processed immediately
- State initializes correctly on startup
- Engine liveness updates from recent ticks
- Bar tracking updates from recent bars
- Connection status initializes from recent connection events
- All status badges show correct state
