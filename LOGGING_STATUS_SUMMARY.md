# Logging Status Summary - January 29, 2026

## Critical Issue: Restart Recovery Not Working

### Problem
**Restart recovery is failing** - streams are re-locking ranges instead of restoring them from logs.

### Evidence
1. **No restoration logs found**: No `RANGE_LOCKED_RESTORED_FROM_HYDRATION` or `RANGE_LOCKED_RESTORED_FROM_RANGES` events
2. **Duplicate RANGE_LOCKED events**: Each restart creates new events instead of restoring
   - GC2: 2 events (16:31:06, 16:44:31)
   - RTY2: 2 events (16:31:06, 16:44:31)
   - NG2: 2 events (16:31:08, 16:44:34)
3. **Full lifecycle executed**: Streams go through PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED instead of restoring directly

### Root Cause Analysis

**Timing Issue:**
- Restoration is called in constructor at 16:31:02
- At that time, hydration log might not have events from BEFORE restart
- Ranges log DOES have events (from 15:30, 15:44), but restoration might not be finding them

**Possible Causes:**
1. `RestoreRangeLockedFromHydrationLog()` not finding events (deserialization failing?)
2. Restoration happening but then normal flow continues anyway
3. Events exist but restoration logic has a bug

### What's Working

✅ **Range lock implementation**: New code prevents duplicates going forward (once locked, stays locked)
✅ **No critical errors**: No RANGE_LOCK_TRANSITION_FAILED, DUPLICATE_RANGE_LOCKED errors
✅ **Breakout computation**: All events have `breakout_levels_missing: false`
✅ **Idempotency**: RangeLockedEventPersister prevents duplicate writes (but restoration bypasses this)

### What's Broken

❌ **Restart recovery**: Not restoring from logs
❌ **Duplicate prevention on restart**: Creating new locks instead of restoring
❌ **Efficiency**: Unnecessary range recomputation on restart

### Next Steps

1. **Add diagnostic logging** to `RestoreRangeLockedFromHydrationLog()`:
   - Log when method is called
   - Log if files exist
   - Log how many events found
   - Log if restoration succeeded or failed

2. **Check deserialization**: Verify HydrationEvent and RangeLockedEvent deserialize correctly

3. **Add guard**: Prevent normal flow from continuing if `_rangeLocked == true` after restoration

4. **Test restoration**: Manually verify restoration works with existing log files

## Summary

**Status**: ⚠️ **Restart recovery is broken** - needs immediate fix

**Impact**: Medium - ranges are being recomputed unnecessarily, but values appear correct

**Priority**: High - should be fixed before next restart to prevent duplicate events
