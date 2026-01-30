# ES1 Bar Rejection After Slot Time - Bug Analysis

**Date**: 2026-01-30  
**Issue**: Bars stop being received after slot time, preventing range lock

---

## Root Cause

### The Bug: Bar Admission Window is Too Restrictive

**Location**: `modules/robot/core/StreamStateMachine.cs` line 2512-2533

**Code**:
```csharp
// Buffer bars that fall within [range_start, slot_time) using Chicago time comparison
var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime;

if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
{
    // Bar is admitted and buffered
    AddBarToBuffer(...);
}
// Bars at or after slot_time are REJECTED - no else clause to handle them
```

**Problem**: 
- Bars are only admitted if `barChicagoTime < SlotTimeChicagoTime` (slot_time is EXCLUSIVE)
- Bars arriving **at or after** slot_time are **silently rejected**
- No logging or handling for bars that arrive after slot_time

---

## Why This Breaks Range Locking

### Range Lock Check Location

**Location**: `modules/robot/core/StreamStateMachine.cs` line 2089-2096 (in ARMED state handler)

**Code**:
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
1. Range lock check happens in `Tick()` method
2. `Tick()` is called from `OnBarUpdate()` in NinjaTrader strategy
3. If bars are rejected, `OnBarUpdate()` may not process them properly
4. Range lock check **only runs when Tick() is called**
5. **Catch-22**: Need bars to trigger Tick(), but bars after slot_time are rejected

---

## Current Behavior

### What Happens

1. **Before Slot Time** (e.g., 07:59):
   - Bars arrive: `barChicagoTime = 07:59:00`
   - Check: `07:59:00 < 08:00:00` ✅ **PASS**
   - Bar is admitted and buffered
   - `Tick()` is called → Range lock check runs

2. **At Slot Time** (e.g., 08:00):
   - Bar arrives: `barChicagoTime = 08:00:00`
   - Check: `08:00:00 < 08:00:00` ❌ **FAIL**
   - Bar is **REJECTED** (silently)
   - `Tick()` may not be called → Range lock check **doesn't run**

3. **After Slot Time** (e.g., 08:01):
   - Bar arrives: `barChicagoTime = 08:01:00`
   - Check: `08:01:00 < 08:00:00` ❌ **FAIL**
   - Bar is **REJECTED** (silently)
   - `Tick()` may not be called → Range lock check **doesn't run**

---

## Why ES1 Stopped Receiving Bars

### Timeline

1. **07:59:18**: ES1 restarted, range building started
2. **08:00:00**: Slot time passed
3. **08:00:01+**: Bars arriving after slot_time are rejected
4. **Result**: No bars processed → No `Tick()` calls → Range lock check never runs

### Evidence

- Last bar event: 13:59:18 UTC (07:59:18 Chicago) - **1 minute before slot time**
- No `ONBARUPDATE_CALLED` events after slot time
- Range still in `RANGE_BUILDING` state
- No `RANGE_LOCKED` events

---

## The Fix

### Option 1: Allow Bars at Slot Time (Recommended)

**Change**: Modify bar admission to allow bars **at** slot_time (make it inclusive)

```csharp
// OLD: barChicagoTime < SlotTimeChicagoTime (exclusive)
// NEW: barChicagoTime <= SlotTimeChicagoTime (inclusive)
var comparisonResult = barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime;

if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime <= SlotTimeChicagoTime)
{
    // Admit bar
}
```

**Rationale**: 
- Slot_time bar should be included in range building
- Allows range lock check to run when slot_time bar arrives
- Matches semantic intent: "build range up to and including slot_time"

### Option 2: Always Call Tick() Even When Bar Rejected

**Change**: Ensure `Tick()` is called even when bars are rejected

**Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs` in `OnBarUpdate()`

**Rationale**:
- Range lock check runs independently of bar admission
- Allows range to lock even if bars are rejected

### Option 3: Time-Based Range Lock Trigger

**Change**: Add time-based trigger for range lock (not just bar-based)

**Location**: Add periodic check in state machine

**Rationale**:
- Range lock doesn't depend on bar arrival
- More robust - works even if bars stop arriving

---

## Recommended Fix: Option 1

**Why**: 
- Simplest and most correct
- Slot_time bar should be included in range
- Fixes the root cause (bar rejection)

**Implementation**:
1. Change line 2512: `barChicagoTime < SlotTimeChicagoTime` → `barChicagoTime <= SlotTimeChicagoTime`
2. Change line 2533: Same change
3. Update comment on line 2504 to reflect inclusive boundary

**Files to Modify**:
- `modules/robot/core/StreamStateMachine.cs` (line 2512, 2533)
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs` (same lines)

---

## Impact Assessment

### Current Impact
- **ES1**: Stuck in RANGE_BUILDING, cannot place orders
- **Other streams**: May have same issue if they restart near slot time
- **Trading**: Blocked until range locks

### After Fix
- Bars at slot_time will be admitted
- Range lock check will run when slot_time bar arrives
- Range will lock correctly
- Orders can be placed

---

## Testing

After fix, verify:
1. Bars at slot_time are admitted
2. Range lock check runs when slot_time bar arrives
3. Range locks correctly
4. Orders can be placed after range lock

---

## Conclusion

**Root Cause**: Bar admission window excludes slot_time (`<` instead of `<=`), causing bars after slot_time to be rejected and preventing range lock check from running.

**Fix**: Change bar admission check to include slot_time (`<=` instead of `<`).

**Priority**: 🔴 **CRITICAL** - Blocks trading for streams that restart near slot time.
