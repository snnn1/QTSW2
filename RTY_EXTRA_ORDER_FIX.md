# RTY Extra Order Fix - Duplicate Entry Order Prevention

**Date**: February 4, 2026
**Issue**: RTY2 submitted 3 orders when only 2 should have been submitted (Long + Short stop brackets)

## Root Cause

**Race Condition**: `CheckBreakoutEntry()` was submitting entry orders even when stop brackets were already submitted at lock.

### The Problem Flow

1. **Range locks** at 09:30
2. **Stop brackets submitted** (Long + Short stop orders) ✅
3. **Short stop bracket fills immediately** (price at breakout level)
4. **Protective orders submitted** for Short ✅
5. **Breakout detected** on next bar (09:31)
6. **`CheckBreakoutEntry()` runs** and detects breakout
7. **`RecordIntendedEntry()` called** → Submits ANOTHER entry order ❌
8. **Result**: 3 orders total (Long stop, Short stop, + duplicate Short entry)

### Why This Happened

`CheckBreakoutEntry()` only checked `_entryDetected` flag, but didn't check if stop brackets were already submitted. When a stop bracket fills immediately:
- Fill processing may not have set `_entryDetected = true` yet
- `CheckBreakoutEntry()` sees breakout and submits duplicate order
- Race condition between fill processing and breakout detection

## The Fix

**Added check in `CheckBreakoutEntry()`**:
- If `_stopBracketsSubmittedAtLock = true`, don't submit another entry order
- Stop brackets handle breakout entries automatically (they fill when price hits breakout level)
- Only log breakout detection for debugging, but don't submit duplicate order

### Code Change

**File**: `modules/robot/core/StreamStateMachine.cs` and `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

**Location**: `CheckBreakoutEntry()` method (line ~5256)

**Before**:
```csharp
private void CheckBreakoutEntry(...)
{
    // No check for stop brackets
    if (longTrigger) RecordIntendedEntry("Long", ...);
    if (shortTrigger) RecordIntendedEntry("Short", ...);
}
```

**After**:
```csharp
private void CheckBreakoutEntry(...)
{
    // CRITICAL FIX: If stop brackets already submitted, don't submit duplicate order
    if (_stopBracketsSubmittedAtLock)
    {
        // Stop brackets handle breakout entries - don't submit duplicate
        if ((longTrigger || shortTrigger) && !_entryDetected)
        {
            // Log for debugging but don't submit order
            _log.Write(..., "BREAKOUT_DETECTED_STOP_BRACKETS_EXIST", ...);
        }
        return; // Don't submit duplicate entry order
    }
    
    // Only submit entry orders if stop brackets weren't submitted
    if (longTrigger) RecordIntendedEntry("Long", ...);
    if (shortTrigger) RecordIntendedEntry("Short", ...);
}
```

## Impact

### Before Fix
- Stop brackets submitted → Breakout detected → Duplicate entry order submitted
- Result: 3 orders (Long stop, Short stop, + duplicate entry)

### After Fix
- Stop brackets submitted → Breakout detected → No duplicate order (stop bracket handles it)
- Result: 2 orders (Long stop, Short stop) ✅

## Status

✅ **FIXED** - Code updated in both `modules/` and `RobotCore_For_NinjaTrader/`
✅ **BUILT** - DLL rebuilt successfully
✅ **DEPLOYED** - DLL copied to NinjaTrader folders

## Testing

After restarting NinjaTrader:
1. Monitor for `BREAKOUT_DETECTED_STOP_BRACKETS_EXIST` events
2. Verify only 2 orders submitted at range lock (Long + Short stop brackets)
3. Verify no duplicate entry orders when breakouts occur after stop brackets submitted
