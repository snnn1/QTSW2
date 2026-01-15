# Root Cause Analysis - Why Streams Didn't Transition to RANGE_BUILDING

## The Problem

Streams for the 09:00 slot remained stuck in `ARMED` state and never transitioned to `RANGE_BUILDING`.

## Root Cause

Looking at the code flow:

### 1. Trading Day Rollover (UpdateTradingDate)

When `UpdateTradingDate()` is called (line 218-348), it:
- Resets `_preHydrationComplete = false` (line 299)
- Preserves state as `ARMED` if already in ARMED (line 325-336)

**Result**: Stream is in `ARMED` state but `_preHydrationComplete` is `false`.

### 2. ARMED State Logic (Tick method)

In the `ARMED` state handler (line 374-516):
```csharp
case StreamState.ARMED:
    // Require pre-hydration completion before entering RANGE_BUILDING
    if (!_preHydrationComplete)
    {
        // Should not happen - pre-hydration should complete before ARMED
        LogHealth("ERROR", "INVARIANT_VIOLATION", "ARMED state reached without pre-hydration completion",
            new { instrument = Instrument, slot = Stream });
        break;  // ⚠️ EXITS EARLY - NEVER CHECKS RangeStartUtc!
    }
    
    // ... diagnostic logging ...
    
    if (utcNow >= RangeStartUtc)  // ⚠️ THIS CODE NEVER REACHED!
    {
        Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
    }
```

**The Bug**: When `_preHydrationComplete` is `false`, the code logs an error and `break`s early, **never checking** if `utcNow >= RangeStartUtc`.

### 3. Why This Happens

After `UpdateTradingDate()`:
- State is preserved as `ARMED` (correct)
- But `_preHydrationComplete` is reset to `false` (incorrect for preserved state)
- Pre-hydration is not re-run because state is already `ARMED`
- Every `Tick()` call hits the early `break` and never checks `RangeStartUtc`

## The Fix

The issue is in `UpdateTradingDate()` - when state is preserved as `ARMED`, we should **NOT** reset `_preHydrationComplete` to `false` if pre-hydration was already complete.

**Solution**: Only reset `_preHydrationComplete` if we're resetting to `PRE_HYDRATION` state, not when preserving `ARMED` state.

## The Fix (IMPLEMENTED)

**Solution**: When `UpdateTradingDate()` clears the bar buffer and resets `_preHydrationComplete`, we must reset state to `PRE_HYDRATION` (not preserve `ARMED`) so that pre-hydration can re-run for the new trading day.

**Code Change**: Modified `UpdateTradingDate()` to always reset to `PRE_HYDRATION` when the journal is not committed, instead of preserving `ARMED` state. This ensures:
1. Pre-hydration re-runs for the new trading day
2. `_preHydrationComplete` flag is properly set
3. Stream can transition from PRE_HYDRATION → ARMED → RANGE_BUILDING correctly

**Files Modified**:
- `modules/robot/core/StreamStateMachine.cs` (line 298-347)
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs` (line 298-347)
