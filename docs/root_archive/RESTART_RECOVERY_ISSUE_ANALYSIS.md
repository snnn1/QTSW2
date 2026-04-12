# Restart Recovery Issue Analysis

## Critical Problem Found

**Restart recovery is NOT working** - streams are re-locking ranges instead of restoring them.

## Evidence from Logs

### GC2 Stream (Example)

**First Restart (16:31:02):**
- Line 7: `STREAM_INITIALIZED` with `previous_state: RANGE_LOCKED` ✅ (detected correctly)
- Line 16: `RANGE_BUILDING_START` ❌ (should NOT happen - should restore directly)
- Line 17: `RANGE_LOCKED` at 16:31:06 ❌ (NEW lock created, not restored)

**Second Restart (16:44:28):**
- Line 25: `STREAM_INITIALIZED` with `previous_state: RANGE_LOCKED` ✅ (detected correctly)
- Line 35: `RANGE_BUILDING_START` ❌ (should NOT happen)
- Line 37: `RANGE_LOCKED` at 16:44:31 ❌ (ANOTHER new lock created)

### Pattern Across All Streams

All streams (GC2, RTY2, NG2) show the same pattern:
1. ✅ Restart detected correctly (`previous_state: RANGE_LOCKED`)
2. ❌ Goes through PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED
3. ❌ Creates NEW RANGE_LOCKED events instead of restoring
4. ❌ Results in duplicate RANGE_LOCKED events per stream

## Root Cause

`RestoreRangeLockedFromHydrationLog()` is being called but:
- Either not finding events in hydration/ranges log
- Or finding events but not restoring correctly
- Or restoration happens but then `TryLockRange()` is called anyway

## Expected vs Actual Behavior

**Expected:**
1. `RestoreRangeLockedFromHydrationLog()` finds RANGE_LOCKED event
2. Restores `_rangeLocked = true`, range values, breakout levels
3. Transitions to `RANGE_LOCKED` state
4. Logs `RANGE_LOCKED_RESTORED_FROM_HYDRATION`
5. Stream stays in `RANGE_LOCKED` state (no re-locking)

**Actual:**
1. `RestoreRangeLockedFromHydrationLog()` called but no restoration log found
2. Stream goes through PRE_HYDRATION → ARMED → RANGE_BUILDING
3. `TryLockRange()` called and creates NEW lock
4. New `RANGE_LOCKED` event written (duplicate)

## Missing Logs

**No `RANGE_LOCKED_RESTORED_FROM_HYDRATION` or `RANGE_LOCKED_RESTORED_FROM_RANGES` events found** in robot logs.

This suggests:
- `RestoreRangeLockedFromHydrationLog()` is not finding events
- Or finding events but failing silently
- Or restoration code path is not being executed

## Impact

1. **Duplicate RANGE_LOCKED events**: Each restart creates new events instead of restoring
2. **Range recomputation**: Ranges are recomputed unnecessarily (wasteful)
3. **Potential range differences**: If bars differ, ranges might be different (incorrect)
4. **Breakout detection disruption**: Stream goes through full lifecycle unnecessarily

## Next Steps to Investigate

1. Check if `RestoreRangeLockedFromHydrationLog()` is actually being called
2. Check if hydration/ranges log files exist and contain events
3. Check if deserialization is failing silently
4. Check if restoration happens but then `TryLockRange()` is called anyway
5. Add more logging to restoration method to trace execution

## Current Status

❌ **Restart recovery is broken** - needs immediate investigation and fix.
