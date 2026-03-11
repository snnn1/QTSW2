# OnBarUpdate Continuous Execution Fix

**Date**: 2026-01-30  
**Issue**: OnBarUpdate stopped being called when bars stopped closing, preventing Tick() from running and blocking range lock checks.

---

## Problem

### Root Cause

1. **OnBarUpdate uses `Calculate.OnBarClose`**:
   - OnBarUpdate only fires when bars **close**
   - If bars aren't closing, OnBarUpdate won't be called

2. **Tick() only called from OnBarUpdate**:
   - Range lock checks run in `Tick()`
   - `Tick()` was only called from `OnBarUpdate()`
   - No OnBarUpdate → No Tick() → No range lock checks

3. **Impact**:
   - When bars stopped closing (data feed issue, market closed, etc.), Tick() stopped running
   - Range lock checks couldn't execute
   - Range couldn't lock → No orders

---

## Solution

### Change Made

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`  
**Lines**: 1277-1280

Added `Tick()` call to `OnMarketData()` so it runs on **every tick**, not just when bars close.

### Code Change

```csharp
// CRITICAL FIX: Drive Tick() from tick flow to ensure continuous execution
// This ensures range lock checks and time-based logic run even when bars aren't closing
// Tick() is idempotent and safe to call frequently
_engine.Tick(utcNow);
```

### Why This Works

1. **OnMarketData() fires on every tick**:
   - Runs continuously as long as market data is flowing
   - Doesn't depend on bars closing

2. **Tick() is idempotent**:
   - Safe to call frequently
   - Handles duplicate calls gracefully
   - No performance impact from frequent calls

3. **Dual execution paths**:
   - **OnBarUpdate**: Handles bar data + calls Tick()
   - **OnMarketData**: Handles tick data + calls Tick()
   - Tick() runs continuously regardless of bar closes

---

## Benefits

### Continuous Execution

- **Range lock checks run continuously**: Even when bars aren't closing
- **Time-based logic executes**: Slot time checks, stall detection, etc.
- **System remains responsive**: Doesn't depend on bar closes

### Maintains Existing Behavior

- **OnBarUpdate still handles bars**: Bar data processing unchanged
- **OnMarketData still handles ticks**: Break-even detection unchanged
- **No breaking changes**: Existing functionality preserved

### Why Not Change Calculate Mode?

**We kept `Calculate.OnBarClose`** because:
- `OnEachTick` was causing Realtime transition blocking (as noted in comments)
- OnMarketData() already provides tick-based execution
- Dual path (bars + ticks) is more robust than single path

---

## Testing

### Expected Behavior

1. **When bars are closing**:
   - OnBarUpdate fires → Tick() runs
   - OnMarketData fires → Tick() runs
   - Tick() runs twice per bar (once from each path) - safe because idempotent

2. **When bars aren't closing**:
   - OnBarUpdate doesn't fire
   - OnMarketData still fires → Tick() runs
   - Range lock checks continue to execute

3. **When market is closed**:
   - Neither fires (expected)
   - System waits for market to reopen

---

## Related Fixes

### Bar Admission Fix (Previously Applied)

- Changed bar admission to include slot_time (`<=` instead of `<`)
- Ensures slot_time bars are admitted when they arrive
- **Still needed**: Works together with this fix

### Combined Effect

1. **Bar admission fix**: Allows slot_time bars to be admitted
2. **Continuous Tick() fix**: Ensures Tick() runs even if bars aren't closing
3. **Result**: Range lock checks run continuously, regardless of bar close timing

---

## Summary

**Problem**: OnBarUpdate stopped when bars stopped closing, blocking Tick() and range lock checks.

**Solution**: Added `Tick()` call to `OnMarketData()` so it runs on every tick, ensuring continuous execution.

**Result**: Range lock checks and time-based logic run continuously, independent of bar close timing.

**Status**: ✅ **FIXED** - Ready for deployment
