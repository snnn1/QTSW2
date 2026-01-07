# Range Building Timing Issue

## Problem Summary

**Symptom:** `RangeHigh` and `RangeLow` are `null` when streams transition to `RANGE_LOCKED`, causing `MISSING_RANGE_VALUES` errors.

**Root Cause:** Streams are being created AFTER the slot time has already passed. When `Tick()` runs, it immediately locks the range because `utcNow >= SlotTimeUtc`, but `OnBar()` hasn't processed any bars yet to build the range.

## Timeline Analysis

From logs:
- **15:29:01.1918973** - `RANGE_BUILD_START` (stream starts building range)
- **15:29:01.1949900** - `RANGE_LOCKED` (only 3ms later!)
- **Result:** `RangeHigh: null`, `RangeLow: null`

**Slot Time:** 09:00 Chicago = 15:00 UTC
**Stream Created:** ~15:29 UTC (29 minutes AFTER slot time)

## The Problem

### Code Flow

1. **Stream Creation** (`RobotEngine.ApplyTimetable()`):
   - Stream is created with `SlotTimeUtc = 15:00 UTC` (09:00 Chicago)
   - `RangeStartUtc` is set (e.g., 08:00 Chicago = 14:00 UTC)

2. **Tick() Called** (`StreamStateMachine.Tick()`):
   ```csharp
   case StreamState.RANGE_BUILDING:
       if (utcNow >= SlotTimeUtc)  // 15:32 >= 15:00 → TRUE
       {
           // Immediately locks without checking if bars were processed!
           Transition(utcNow, StreamState.RANGE_LOCKED, ...);
       }
   ```

3. **OnBar() Called** (`StreamStateMachine.OnBar()`):
   ```csharp
   if (State == StreamState.RANGE_BUILDING)
   {
       if (barUtc < RangeStartUtc || barUtc >= SlotTimeUtc) return;  // barUtc >= SlotTimeUtc → SKIPPED!
       // Range building code never executes
   }
   ```

### Why Range Isn't Built

- `OnBar()` only builds range when `State == RANGE_BUILDING` AND `barUtc < SlotTimeUtc`
- If stream is created after slot time, `Tick()` immediately transitions to `RANGE_LOCKED`
- When `OnBar()` is called with bars at/after slot time, the state is already `RANGE_LOCKED`, so range building is skipped
- Result: `RangeHigh` and `RangeLow` remain `null`

## Solutions

### Option 1: Prevent Locking If Range Is Empty (Recommended)

Modify `Tick()` to check if range values exist before locking:

```csharp
case StreamState.RANGE_BUILDING:
    if (utcNow >= SlotTimeUtc)
    {
        // Only lock if we have range values OR if we're past the range building window
        if (RangeHigh.HasValue && RangeLow.HasValue)
        {
            // Normal lock with range values
            Transition(utcNow, StreamState.RANGE_LOCKED, ...);
        }
        else if (utcNow > SlotTimeUtc.AddMinutes(5))  // Allow 5 min grace period
        {
            // Lock anyway but log warning (range building window has passed)
            _log.Write(..., "RANGE_LOCKED_WITHOUT_VALUES", ...);
            Transition(utcNow, StreamState.RANGE_LOCKED, ...);
        }
        // Otherwise, stay in RANGE_BUILDING and wait for bars
    }
    break;
```

### Option 2: Allow Range Building After Slot Time

Modify `OnBar()` to allow range building even after slot time if range is empty:

```csharp
public void OnBar(DateTimeOffset barUtc, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
{
    if (State == StreamState.RANGE_BUILDING)
    {
        // Allow range building if we don't have values yet, even if past slot time
        if (barUtc < RangeStartUtc) return;
        if (barUtc >= SlotTimeUtc && RangeHigh.HasValue && RangeLow.HasValue) return;
        
        RangeHigh = RangeHigh is null ? high : Math.Max(RangeHigh.Value, high);
        RangeLow = RangeLow is null ? low : Math.Min(RangeLow.Value, low);
        _lastCloseBeforeLock = close;
    }
    // ... rest of method
}
```

### Option 3: Create Streams Before Slot Time (System-Level Fix)

Ensure streams are created BEFORE the slot time:
- Timetable should be loaded/validated before slot time
- Streams should be armed before `RangeStartUtc`
- This is the ideal solution but requires system-level changes

## Current Status

**Range Building:** ❌ **NOT WORKING** - Streams lock before bars are processed

**Impact:**
- `RangeHigh` and `RangeLow` are `null`
- `ComputeBreakoutLevelsAndLog()` fails with `MISSING_RANGE_VALUES`
- `breakout_levels_computed` is `false`
- Execution is blocked

## Recommendation

Implement **Option 1** (prevent locking if range is empty) as a defensive fix, while working on **Option 3** (system-level timing) as the long-term solution.
