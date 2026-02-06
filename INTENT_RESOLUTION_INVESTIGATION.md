# Intent Resolution Failure Investigation

## Root Cause Found

### The Problem

**Fills are arriving when order state is "Initialized"** - this is BEFORE the order transitions to "Accepted" or "Working" state.

### Evidence

1. **4 fills have decoded intent_id but order not in _orderMap**
   - Tag: `QTSW2:e31087ec7bc9f86b` → Decodes to `e31087ec7bc9f86b` ✅
   - Order State: `Initialized` (not Accepted/Working)
   - Fill arrives BEFORE order is fully accepted

2. **Timing Analysis**
   - Fill arrives: 14:00:00.841466
   - Order submitted: 14:00:00.944381
   - Delay: -0.103 seconds (fill BEFORE submission log)
   - But order IS added to _orderMap at line 706 (BEFORE submission)

3. **Order State When Fill Arrives**
   - All 4 fills: Order state = "Initialized"
   - This is the state BEFORE "Accepted" or "Working"

## Why This Happens

### Code Flow

1. **Order Created** (line ~470)
2. **Order Added to _orderMap** (line 706) ✅ BEFORE submission
3. **Order Submitted** (line 714)
4. **Order State Check** (line 732)
   - If Rejected → return early (order stays in map)
   - If Accepted → continue
5. **Fill Arrives** → `HandleExecutionUpdateReal()` called
6. **Check _orderMap** (line 1385) → Order not found!

### The Race Condition

**Hypothesis**: The fill is arriving from NinjaTrader's event system BEFORE the order addition to `_orderMap` is visible to the thread processing the fill.

**Possible Causes**:
1. **Threading Issue**: `_orderMap` is `ConcurrentDictionary` (thread-safe), but there might be a visibility issue
2. **Order State**: Fill arrives when order is "Initialized" - maybe the order lookup fails because state check happens first?
3. **Different Order**: Fill might be from a different order (previous order with same tag?)

### Why Order Not Found

Looking at line 1385:
```csharp
if (!_orderMap.TryGetValue(intentId, out var orderInfo))
```

The order SHOULD be in the map (added at line 706), but it's not found. This suggests:

1. **Order was never added** (unlikely - code path is clear)
2. **Order was removed** (no code removes orders from map)
3. **Threading visibility issue** (possible with ConcurrentDictionary)
4. **Different intent_id** (tag decodes correctly, but intent_id doesn't match)

## The Real Issue

Looking at the timing more carefully:
- Fill arrives at 14:00:00.841466
- Order submitted log at 14:00:00.944381
- But order is added to map BEFORE submission (line 706)

**The fill is arriving BEFORE the submission completes**, but the order SHOULD be in the map.

**However**: If the order submission FAILS (rejected at line 732), the order stays in the map but the submission log isn't written. But the fill still arrives!

## Solution Needed

### Option 1: Handle Fills for Initialized Orders

When a fill arrives for an order in "Initialized" state:
1. Check if order is in _orderMap (it should be)
2. If not found, wait briefly and retry (order might be added soon)
3. If still not found, flatten position (fail-closed)

### Option 2: Add Order to Map Earlier

Add order to _orderMap IMMEDIATELY after order creation, before any submission logic.

### Option 3: Use Broker Order ID for Lookup

Instead of relying on intent_id from tag, use broker_order_id to find the order in _orderMap.

## Current Code Issue

The code at line 1385 checks `_orderMap.TryGetValue(intentId, ...)` but:
- Order is added to map with `intentId` as key
- Fill arrives with decoded `intentId` from tag
- But order might not be visible yet (threading) or order state is wrong

## Immediate Fix

Add retry logic for fills with "Initialized" order state:

```csharp
if (!_orderMap.TryGetValue(intentId, out var orderInfo))
{
    // If order state is Initialized, wait briefly and retry (order might be added soon)
    if (order.OrderState == OrderState.Initialized)
    {
        System.Threading.Thread.Sleep(50); // Brief wait
        if (_orderMap.TryGetValue(intentId, out orderInfo))
        {
            // Found it - continue processing
        }
    }
    
    // Still not found - flatten (fail-closed)
    Flatten(...);
}
```
