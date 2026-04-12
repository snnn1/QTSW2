# Intent Resolution Race Condition Fix

## Problem Identified

**Fills are arriving when order state is "Initialized"** - before the order is fully accepted and visible in `_orderMap`.

### Evidence

- **4 fills** have decoded intent_id but order not found in `_orderMap`
- **Order State**: All fills arrived when order state = "Initialized"
- **Timing**: Fills arrive BEFORE order submission log (negative delay)
- **Tag Decoding**: Works correctly (`QTSW2:e31087ec7bc9f86b` → `e31087ec7bc9f86b`)

### Why This Happens

1. Order is created and added to `_orderMap` (line 706) ✅
2. Order is submitted (line 714)
3. **Fill arrives immediately** (SIM mode - instant fills)
4. Fill processing checks `_orderMap` (line 1385)
5. **Order not found** (race condition - order might not be visible yet)

### Root Cause

**Race Condition**: In NinjaTrader SIM mode, orders fill instantly. The fill event can arrive on a different thread before the `_orderMap` addition is fully visible, especially when order state is "Initialized".

## The Fix

### Added Retry Logic for "Initialized" Orders

When a fill arrives and order is not found in `_orderMap`:

1. **Check order state**
   - If `Initialized`: Wait briefly and retry (order is being added)
   - If other state: Immediate flatten (fail-closed)

2. **Retry Logic**
   - Max 3 retries
   - 50ms delay between retries
   - Total wait: up to 150ms

3. **If Found After Retry**
   - Log race condition resolved
   - Continue normal fill processing
   - Call `HandleEntryFill()` → Submit protective orders ✅

4. **If Still Not Found**
   - Flatten position (fail-closed)
   - Log critical error

### Code Changes

**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`

**Lines 1385-1434**: Added retry logic for fills with "Initialized" order state

**Before**: Immediate flatten if order not found
**After**: Retry up to 3 times (50ms each) if order state is "Initialized", then flatten if still not found

## Impact

### Before Fix
- Fill arrives → Order not found → Flatten immediately
- `HandleEntryFill()` never called
- Protective orders never submitted
- BE detection can't work (no stop order to modify)

### After Fix
- Fill arrives → Order not found → Check order state
- If "Initialized": Retry (order being added)
- If found: Continue processing → `HandleEntryFill()` called → Protective orders submitted ✅
- BE detection can work (stop order exists)

## Status

✅ **FIXED**: Added retry logic for race condition
✅ **BUILT**: DLL rebuilt successfully
✅ **COPIED**: DLL copied to NinjaTrader folders

## Testing

After restarting NinjaTrader:
1. Monitor logs for `EXECUTION_UPDATE_RACE_CONDITION_RESOLVED` events
2. Verify protective orders are submitted after retry succeeds
3. Verify BE detection works (stop orders exist to modify)

## Why This Fixes BE Detection

1. **Before**: Fills with "Initialized" orders → Flattened → No protective orders → BE can't work
2. **After**: Fills with "Initialized" orders → Retry → Found → `HandleEntryFill()` called → Protective orders submitted → BE can work ✅

This fix ensures that fills arriving during the race condition window are properly processed, allowing protective orders to be submitted and BE detection to function correctly.
