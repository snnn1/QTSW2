# Zero Quantity / Unknown State Orders Assessment

## Issue Summary

Orders are appearing in NinjaTrader UI with:
- **Quantity: 0**
- **State: Unknown**
- **No filled quantity**
- **No average price**

However, internal robot logs show orders are being submitted with **correct quantities (1 or 2)**.

## Root Cause Analysis

### What's Happening

1. **Orders ARE being created correctly** ✅
   - Logs show `ORDER_CREATED_STOPMARKET` with `quantity: 2`
   - `ORDER_CREATED_VERIFICATION` confirms `requested_quantity: 2` matches `order_quantity: 2`

2. **Orders ARE being submitted** ✅
   - `ORDER_SUBMIT_SUCCESS` shows orders submitted with correct quantities
   - Orders receive broker order IDs (e.g., `2218ab775f16485c9956afd047d4b0db`)

3. **Order tracking is failing** ❌
   - Multiple `EXECUTION_UPDATE_UNKNOWN_ORDER` events appear
   - Error: "Order not found in tracking map"
   - Order state: `Initialized`
   - This happens **immediately after submission**

### The Problem

When NinjaTrader sends `OrderUpdate` callbacks with state `Initialized`, the robot cannot find the order in its `_orderMap` tracking dictionary. 

**ROOT CAUSE: Multiple Strategy Instances**

Each `NinjaTraderSimAdapter` instance has its own `_orderMap` (a `ConcurrentDictionary<string, OrderInfo>`). If:
- Order is submitted from Strategy Instance A → stored in Instance A's `_orderMap`
- OrderUpdate callback arrives in Strategy Instance B → Instance B's `_orderMap` doesn't have the order

This explains why:
- Orders ARE being submitted correctly (Instance A)
- OrderUpdate arrives but order is NOT in map (Instance B)
- UI shows "Unknown" state (order stuck in `Initialized` because tracking failed)

**Evidence:**
- Multiple `EXECUTION_UPDATE_UNKNOWN_ORDER` events with same order ID
- Orders have correct tags (`QTSW2:5a70807af02cce8e`) but aren't found in map
- This happens consistently for all orders, suggesting systematic multi-instance issue

### Why UI Shows "Unknown" / 0 Quantity

NinjaTrader UI displays orders in `Initialized` state as:
- **State: Unknown** (because `Initialized` is an intermediate state)
- **Quantity: 0** (because the order hasn't been properly acknowledged yet)

This is a **display artifact** - the orders were submitted correctly, but NinjaTrader hasn't transitioned them to `Accepted` or `Working` state yet, possibly because:
- The order is being rejected (but rejection callback hasn't arrived yet)
- The order is stuck in an intermediate state
- Multiple strategy instances are interfering with each other

## Evidence from Logs

### Example: MNG Order (2026-01-30 13:59:23)

```
ORDER_CREATED_STOPMARKET: quantity=2, stop_price=3.914 ✅
ORDER_CREATED_VERIFICATION: requested_quantity=2, order_quantity=2 ✅
ORDER_SUBMIT_SUCCESS: quantity=2, order_state=Initialized ✅
EXECUTION_UPDATE_UNKNOWN_ORDER: Order not found in tracking map ❌
```

**Timeline:**
- 13:59:23.367 - Order created (quantity=2)
- 13:59:23.367 - Order verified (quantity=2)
- 13:59:24.051 - Order submitted successfully
- 13:59:23.928 - **OrderUpdate arrives with state "Initialized" but order NOT in tracking map**

**Critical Issue**: OrderUpdate arrives **BEFORE** the order is fully tracked, or the tracking is being lost.

## Code Analysis

### Order Submission Flow (`NinjaTraderSimAdapter.NT.cs`)

```csharp
// Line 656-692: Order info stored in _orderMap
var orderInfo = new OrderInfo { ... };
_orderMap[intentId] = orderInfo;  // ← Should store order here

// Line 694-715: Order submitted
account.Submit(new[] { order });

// Line 810-829: OrderUpdate handler
if (!_orderMap.TryGetValue(intentId, out var orderInfo))
{
    // Order not found! ← This is happening
    _log.Write(..., "EXECUTION_UPDATE_UNKNOWN_ORDER", ...);
    return;
}
```

### Potential Issues

1. **Timing**: OrderUpdate callback may arrive synchronously during `Submit()`, before `_orderMap` assignment completes
2. **Multiple Instances**: If multiple strategy instances exist, each has its own `_orderMap`, causing cross-instance tracking failures
3. **Order Rejection**: If order is rejected immediately, it may be removed from tracking before OrderUpdate arrives

## Impact

### Functional Impact
- ✅ Orders ARE being submitted to NinjaTrader
- ✅ Quantities ARE correct internally
- ❌ Order state tracking is broken
- ❌ UI shows confusing "Unknown" / 0 quantity display
- ❌ Robot cannot track order lifecycle properly

### Risk Assessment
- **Low Risk**: Orders are still being submitted correctly
- **Medium Risk**: Order state tracking failures prevent proper lifecycle management
- **High Risk**: If orders are rejected but not tracked, robot may retry incorrectly

## Recommendations

### Immediate Fixes

1. **Fix Multi-Instance Order Tracking** ⚠️ **CRITICAL**
   - **Problem**: Each strategy instance has its own `_orderMap`, causing cross-instance tracking failures
   - **Solution**: Use a shared order tracking mechanism (e.g., static dictionary keyed by order ID + instance ID)
   - **Alternative**: Ensure OrderUpdate callbacks route to the correct instance that submitted the order
   - **Quick Fix**: Add instance ID to order tracking and verify OrderUpdate arrives in correct instance

2. **Improve OrderUpdate Handling**
   - Handle `Initialized` state explicitly (don't treat as error)
   - When order not found, check if it's from another instance
   - Add logging to identify which instance submitted vs received OrderUpdate
   - Consider using order ID (not just intent ID) for cross-instance lookup

3. **Add Order State Validation**
   - Verify order is in `_orderMap` immediately after `Submit()`
   - Log warning if order not found within expected timeframe
   - Add instance ID to all order tracking logs for debugging

### Long-term Improvements

1. **Order Lifecycle Tracking**
   - Implement proper state machine for order tracking
   - Add timeout handling for orders stuck in `Initialized`
   - Track order rejections more reliably

2. **Multi-Instance Handling**
   - Ensure each strategy instance tracks only its own orders
   - Add instance ID to order tracking
   - Prevent cross-instance interference

3. **UI State Synchronization**
   - Add logging to track when orders transition from `Initialized` to `Accepted`/`Working`
   - Monitor for orders stuck in `Initialized` state
   - Add alerts for orders that never transition properly

## Next Steps

1. ✅ **Assessment Complete** - Root cause identified: **Multiple Strategy Instances**
2. ✅ **Investigation Complete** - Architecture analyzed, fix identified
3. ✅ **Fix Implemented** - Added instrument filtering to `OnOrderUpdate` and `OnExecutionUpdate`
4. ⏳ **Sync to RobotCore_For_NinjaTrader** - Copy fixes to deployment folder
5. ⏳ **Test Fixes** - Verify orders are tracked correctly, no more `EXECUTION_UPDATE_UNKNOWN_ORDER` events
6. ⏳ **Monitor** - Watch for zero quantity / Unknown state orders to disappear

## Related Issues

- Orders may be rejected but not properly tracked (see `ORDER_REJECTED` logs)
- Multiple strategy instances may be causing tracking conflicts
- Order state transitions may be failing silently
