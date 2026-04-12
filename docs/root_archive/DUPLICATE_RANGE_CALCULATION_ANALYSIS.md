# Duplicate Range Calculation Analysis

## Problem Statement

Ranges are being calculated more than once per stream per trading day, which should not happen. The `_rangeComputed` flag exists to prevent this, but evidence suggests it's not working correctly.

## Code Analysis

### Range Calculation Entry Points

After analyzing `StreamStateMachine.cs`, there are **multiple code paths** that can trigger range calculations:

#### 1. **Initial Range Computation in `HandleArmedState`** (Line ~1978-1991)
```csharp
if (!_rangeComputed && utcNow < SlotTimeUtc)
{
    var initialRangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: utcNow);
    if (initialRangeResult.Success)
    {
        RangeHigh = initialRangeResult.RangeHigh;
        RangeLow = initialRangeResult.RangeLow;
        FreezeClose = initialRangeResult.FreezeClose;
        FreezeCloseSource = initialRangeResult.FreezeCloseSource;
        _rangeComputed = true; // ✅ Flag set here
    }
}
```

**Issue**: This computation happens when entering `RANGE_BUILDING` state, but the flag check `!_rangeComputed` might be bypassed if:
- The state transition happens multiple times
- The flag is reset elsewhere
- Race conditions occur

#### 2. **Late Range Computation in `HandleArmedState`** (Line ~2026-2057)
```csharp
else if (utcNow >= SlotTimeUtc && !_rangeComputed)
{
    // CRITICAL: We're past slot time but range was never computed
    var lateRangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
    if (lateRangeResult.Success)
    {
        RangeHigh = lateRangeResult.RangeHigh;
        RangeLow = lateRangeResult.RangeLow;
        FreezeClose = lateRangeResult.FreezeClose;
        FreezeCloseSource = lateRangeResult.FreezeCloseSource;
        _rangeComputed = true; // ✅ Flag set here
    }
}
```

**Issue**: This is a recovery path, but if the initial computation failed silently, this could run multiple times.

#### 3. **Main Range Computation in `HandleRangeBuildingState`** (Line ~2178-2318)
```csharp
if (!_rangeComputed)
{
    // Log RANGE_COMPUTE_START only once per stream per slot (prevents spam)
    if (!_rangeComputeStartLogged)
    {
        _rangeComputeStartLogged = true;
        // ... log RANGE_COMPUTE_START ...
    }

    var rangeResult = ComputeRangeRetrospectively(utcNow);
    
    if (!rangeResult.Success)
    {
        // ... handle failure ...
        return; // ⚠️ Returns WITHOUT setting _rangeComputed
    }
    
    // Range computed successfully - set values atomically
    RangeHigh = rangeResult.RangeHigh;
    RangeLow = rangeResult.RangeLow;
    FreezeClose = rangeResult.FreezeClose;
    FreezeCloseSource = rangeResult.FreezeCloseSource;
    _rangeComputed = true; // ✅ Flag set here
}
```

**CRITICAL ISSUE**: If `ComputeRangeRetrospectively` fails, the method returns early **without setting `_rangeComputed = true`**. This means:
- The next `Tick()` call will try again
- If it fails again, it will keep retrying
- Once it succeeds, it sets the flag
- **BUT**: If there are multiple code paths that can reach this point, they could all run before the flag is set

#### 4. **Missed Breakout Detection in Pre-Hydration** (Lines ~1433, 1687)
```csharp
var rangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
```
**Issue**: These computations are for **reconstruction purposes only** (missed breakout detection) and **do NOT set `_rangeComputed`**. However, they still call `ComputeRangeRetrospectively`, which is expensive.

### Flag Reset Points

The `_rangeComputed` flag is reset in:

1. **`EnterRecoveryManage`** (Line ~5167)
   ```csharp
   _rangeComputed = false;
   ```
   This resets the flag when entering recovery mode, which is correct.

2. **Constructor** - Flag is initialized to `false` (Line ~115)
   ```csharp
   private bool _rangeComputed = false;
   ```

### Potential Issues

#### Issue 1: Race Condition Between State Transitions
- `HandleArmedState` can compute range when transitioning to `RANGE_BUILDING`
- `HandleRangeBuildingState` can also compute range
- If both run before `_rangeComputed` is set, both will compute

**Evidence**: The flag is checked (`!_rangeComputed`) but not set atomically with the state transition.

#### Issue 2: Multiple Calls to `HandleRangeBuildingState`
- `HandleRangeBuildingState` is called on every `Tick()` while in `RANGE_BUILDING` state
- If the first computation fails, subsequent ticks will retry
- However, if the computation succeeds but the flag isn't set immediately (due to exception or early return), subsequent ticks will recompute

#### Issue 3: Flag Not Set on Failure Paths
- When `ComputeRangeRetrospectively` fails, the method returns early
- `_rangeComputed` remains `false`
- Next tick will try again (correct behavior)
- **BUT**: If multiple code paths can trigger computation, they might all run before any succeed

#### Issue 4: Initialization vs. Main Computation Overlap
- `HandleArmedState` computes initial range when entering `RANGE_BUILDING` (line 1982)
- `HandleRangeBuildingState` also computes range if `!_rangeComputed` (line 2211)
- If state transitions happen quickly, both could run

### Evidence from Logs

From the analysis script:
- **0 RANGE_COMPUTE_START events found** in logs
- **5 RANGE_INITIALIZED_FROM_HISTORY events** found
- This suggests ranges are being initialized via the `HandleArmedState` path, but the main computation path (`HandleRangeBuildingState`) might not be logging `RANGE_COMPUTE_START`

**Possible explanations**:
1. `_rangeComputeStartLogged` flag prevents logging after first attempt
2. Ranges are computed successfully on first try in `HandleArmedState`, so `HandleRangeBuildingState` never runs the computation
3. Logging is happening but events aren't being written correctly

## Recommendations

### Fix 1: Atomic Flag Setting
Set `_rangeComputed = true` **immediately** when starting computation, not after success:

```csharp
if (!_rangeComputed)
{
    _rangeComputed = true; // Set flag FIRST to prevent concurrent computations
    try
    {
        var rangeResult = ComputeRangeRetrospectively(utcNow);
        // ... handle result ...
    }
    catch
    {
        _rangeComputed = false; // Reset on exception
        throw;
    }
}
```

**Trade-off**: If computation fails, we won't retry. Need to handle failures explicitly.

### Fix 2: Single Computation Entry Point
Consolidate all range computation into a single method that checks and sets the flag atomically:

```csharp
private bool TryComputeRangeOnce(DateTimeOffset utcNow)
{
    lock (_rangeComputeLock)
    {
        if (_rangeComputed)
            return true; // Already computed
        
        _rangeComputed = true; // Set flag before computation
        try
        {
            var result = ComputeRangeRetrospectively(utcNow);
            if (result.Success)
            {
                RangeHigh = result.RangeHigh;
                RangeLow = result.RangeLow;
                FreezeClose = result.FreezeClose;
                FreezeCloseSource = result.FreezeCloseSource;
                return true;
            }
            else
            {
                _rangeComputed = false; // Reset on failure
                return false;
            }
        }
        catch
        {
            _rangeComputed = false; // Reset on exception
            throw;
        }
    }
}
```

### Fix 3: Remove Duplicate Computation Paths
- Remove initial computation from `HandleArmedState` (line 1978-1991)
- Let `HandleRangeBuildingState` handle all computations
- Keep late computation path as recovery only

### Fix 4: Add Diagnostic Logging
Add logging when `_rangeComputed` flag is checked and set:

```csharp
if (!_rangeComputed)
{
    _log.Write(..., "RANGE_COMPUTE_ATTEMPT", ..., new {
        current_state = State.ToString(),
        flag_value_before = _rangeComputed,
        call_stack = Environment.StackTrace // For debugging
    });
    
    _rangeComputed = true;
    // ... compute ...
}
```

## Next Steps

1. **Add diagnostic logging** to track when and where `_rangeComputed` is checked/set
2. **Review state transition timing** - ensure `HandleArmedState` and `HandleRangeBuildingState` don't both compute
3. **Implement atomic flag setting** with proper failure handling
4. **Consolidate computation paths** to single entry point
5. **Test with multiple streams** to verify fix

## Related Code Locations

- `StreamStateMachine.cs` Line ~115: `_rangeComputed` flag declaration
- `StreamStateMachine.cs` Line ~1978-1991: Initial range computation in `HandleArmedState`
- `StreamStateMachine.cs` Line ~2026-2057: Late range computation in `HandleArmedState`
- `StreamStateMachine.cs` Line ~2178-2318: Main range computation in `HandleRangeBuildingState`
- `StreamStateMachine.cs` Line ~5167: Flag reset in `EnterRecoveryManage`
- `StreamStateMachine.cs` Line ~4197: `ComputeRangeRetrospectively` method definition
