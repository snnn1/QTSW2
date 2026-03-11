# Restart Recovery Diagnosis and Fix

## Current Status (After Latest Restart ~16:54)

### What's Happening

1. **Restoration IS Running**: Diagnostic logs show `RANGE_LOCKED_RESTORE_ATTEMPT` and `RANGE_LOCKED_RESTORE_SCAN_COMPLETE`
2. **But Finding 0 Events**: All streams report `hydration_events_found: 0, range_events_found: 0`
3. **Result**: Streams proceed through normal flow and re-lock ranges (creating duplicates)

### Evidence from Health Logs

**GC2 at 16:54:10:**
- `RANGE_LOCKED_RESTORE_ATTEMPT` ✅ (file exists)
- `RANGE_LOCKED_RESTORE_SCAN_COMPLETE` ✅ (scanned 60 lines)
- `hydration_events_found: 0, range_events_found: 0` ❌ (found nothing!)
- `RANGE_LOCKED_RESTORE_NO_EVENTS` ❌ (no restoration)

**NG2 at 16:54:07:**
- Same pattern - scanned 55 lines, found 0 events

**RTY2 at 16:54:06:**
- Same pattern - scanned 48 lines, found 0 events

### Root Cause

**Deserialization is failing silently!**

The hydration log DOES contain RANGE_LOCKED events (we can see them in the file), but `JsonUtil.Deserialize<HydrationEvent>()` is failing silently and returning null.

**Why?**
- `HydrationEvent` class has constructor-only properties (no setters)
- JSON deserializers (`JavaScriptSerializer` or `System.Text.Json`) may not be able to deserialize into constructor-only classes
- The `catch` block swallows exceptions, so we never see why deserialization fails

### Fixes Applied

1. **Added `Arm()` Guard**: Prevents normal flow if `_rangeLocked == true` (already applied)
2. **Improved Deserialization Logic**:
   - Added quick string checks to filter lines before deserialization
   - Separated try-catch blocks for `HydrationEvent` vs `RangeLockedEvent`
   - Added format detection (`looksLikeHydrationFormat` vs `looksLikeRangesFormat`)
   - Better error handling

3. **Enhanced Diagnostic Logging**:
   - Logs which file is being checked
   - Logs how many lines scanned
   - Logs how many events found
   - Logs deserialization failures (for small files)

### Next Steps

The deserialization issue needs to be fixed. Options:

1. **Option A**: Make `HydrationEvent` deserializable by adding setters or using a different deserialization approach
2. **Option B**: Parse JSON manually to extract fields without using `JsonUtil.Deserialize`
3. **Option C**: Use a different event format that's easier to deserialize

### Current Behavior

- ✅ Restoration code is running
- ✅ Diagnostic logging is working
- ❌ Deserialization is failing silently
- ❌ No events found → streams re-lock ranges
- ❌ Duplicate RANGE_LOCKED events created

### Files Updated

- `modules/robot/core/StreamStateMachine.cs` - Improved deserialization logic
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs` - Synced changes

## Summary

**Status**: ⚠️ **Restoration code is running but deserialization is failing**

**Impact**: High - ranges are being recomputed unnecessarily, creating duplicate events

**Next Action**: Fix deserialization to properly read `HydrationEvent` from JSON
