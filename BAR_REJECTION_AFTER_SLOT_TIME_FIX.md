# Bar Rejection After Slot Time - Code Fix

**Date**: 2026-01-30  
**Issue**: Bars arriving at or after slot_time are rejected, preventing range lock

---

## Root Cause

### Bug Location

**File**: `modules/robot/core/StreamStateMachine.cs`  
**Method**: `AddBarToBuffer()`  
**Lines**: 2512-2533

### The Problem

```csharp
// Line 2504: Comment says "[range_start, slot_time)" - slot_time is EXCLUSIVE
// Line 2512: Check uses < (exclusive)
var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime;

// Line 2533: Only admits bars if < slot_time (exclusive)
if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
{
    // Bar is admitted and buffered
    AddBarToBuffer(...);
}
// Bars at or after slot_time are REJECTED - no handling
```

**Issue**: 
- Bars arriving **at** slot_time (e.g., 08:00:00) are **rejected** because `08:00:00 < 08:00:00` is false
- Bars arriving **after** slot_time are also rejected
- Range lock check runs in `Tick()` which is called from `OnBarUpdate()`
- If NinjaTrader stops calling `OnBarUpdate()` (because bars aren't closing or data feed issues), `Tick()` never runs
- Range lock check never runs → Range never locks → No orders

---

## Why This Breaks Range Locking

### Range Lock Check Location

**File**: `modules/robot/core/StreamStateMachine.cs`  
**Method**: `HandleArmedState()`  
**Lines**: 2089-2096

```csharp
// Request range lock when slot_time is reached
if (utcNow >= SlotTimeUtc && !_rangeLocked)
{
    if (!TryLockRange(utcNow))
    {
        // Locking failed - will retry on next tick
        return;
    }
}
```

**Problem**:
1. Range lock check runs in `Tick()` method
2. `Tick()` is called from `OnBarUpdate()` (line 1219 in RobotSimStrategy.cs)
3. If `OnBarUpdate()` isn't called (no bars closing), `Tick()` never runs
4. Range lock check never runs → Range never locks

### The Catch-22

- **Need**: Bars to trigger `OnBarUpdate()` → `Tick()` → Range lock check
- **Problem**: Bars after slot_time are rejected
- **Result**: If bars stop arriving/closing, `OnBarUpdate()` stops being called
- **Outcome**: Range lock check never runs

---

## The Fix

### Change Bar Admission to Include Slot Time

**Current Code** (line 2512, 2533):
```csharp
var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime;

if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
```

**Fixed Code**:
```csharp
var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime;

if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime)
```

**Change**: `< SlotTimeChicagoTime` → `<= SlotTimeChicagoTime` (make slot_time **inclusive**)

### Update Comment

**Current Comment** (line 2504):
```csharp
// Buffer bars that fall within [range_start, slot_time) using Chicago time comparison
```

**Fixed Comment**:
```csharp
// Buffer bars that fall within [range_start, slot_time] using Chicago time comparison
```

**Change**: `[range_start, slot_time)` → `[range_start, slot_time]` (bracket notation)

---

## Files to Modify

1. **`modules/robot/core/StreamStateMachine.cs`**:
   - Line 2504: Update comment
   - Line 2512: Change `<` to `<=`
   - Line 2533: Change `<` to `<=`

2. **`RobotCore_For_NinjaTrader/StreamStateMachine.cs`**:
   - Same changes as above

---

## Rationale

### Why Include Slot Time Bar?

1. **Semantic Correctness**: Slot_time bar should be included in range building
   - Range represents price action from range_start **through** slot_time
   - Slot_time bar is the last bar that should influence the range

2. **Range Lock Timing**: Range lock should happen when slot_time bar arrives
   - Slot_time bar triggers `OnBarUpdate()` → `Tick()` → Range lock check
   - Range lock check runs immediately when slot_time bar arrives

3. **Consistency**: Matches expected behavior
   - Users expect range to include slot_time bar
   - Matches Analyzer behavior (includes slot_time bar)

### Why Not Just Fix Tick() Timing?

- **Current Design**: Range lock check is bar-driven (runs on `Tick()`)
- **Problem**: If bars stop arriving, `Tick()` doesn't run
- **Better Fix**: Include slot_time bar so range lock check runs when it arrives
- **Alternative**: Could add time-based trigger, but bar-driven is simpler

---

## Testing

After fix, verify:

1. **Bar Admission**:
   - ✅ Bars at slot_time are admitted
   - ✅ Bars after slot_time are still rejected (correct)
   - ✅ Range includes slot_time bar

2. **Range Locking**:
   - ✅ Range lock check runs when slot_time bar arrives
   - ✅ Range locks correctly after slot_time
   - ✅ Breakout levels computed

3. **Order Placement**:
   - ✅ Orders can be placed after range locks
   - ✅ Execution gate evaluations run

---

## Impact

### Before Fix
- ❌ Bars at slot_time rejected
- ❌ Range lock check may not run
- ❌ Range doesn't lock
- ❌ Orders cannot be placed

### After Fix
- ✅ Bars at slot_time admitted
- ✅ Range lock check runs when slot_time bar arrives
- ✅ Range locks correctly
- ✅ Orders can be placed

---

## Implementation

### Step 1: Update StreamStateMachine.cs

**File**: `modules/robot/core/StreamStateMachine.cs`

**Line 2504**:
```csharp
// OLD:
// Buffer bars that fall within [range_start, slot_time) using Chicago time comparison

// NEW:
// Buffer bars that fall within [range_start, slot_time] using Chicago time comparison
```

**Line 2512**:
```csharp
// OLD:
var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime;

// NEW:
var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime;
```

**Line 2533**:
```csharp
// OLD:
if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)

// NEW:
if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime)
```

### Step 2: Sync to RobotCore_For_NinjaTrader

Apply same changes to:
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

---

## Conclusion

**Root Cause**: Bar admission window excludes slot_time (`<` instead of `<=`), causing bars at slot_time to be rejected.

**Fix**: Change bar admission check to include slot_time (`<=` instead of `<`).

**Priority**: 🔴 **CRITICAL** - Blocks trading for streams that restart near slot time or when bars arrive exactly at slot_time.

**Expected Result**: Bars at slot_time will be admitted, range lock check will run, range will lock, orders can be placed.
