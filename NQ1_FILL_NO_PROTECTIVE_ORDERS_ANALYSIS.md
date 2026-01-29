# NQ1 Fill Without Protective Orders - Analysis

## Issue
NQ1 entry order was filled, but no protective stop or target orders were placed.

## Expected Flow

When an entry order fills:
1. NinjaTrader fires `ExecutionUpdate` event
2. `RobotSimStrategy.OnExecutionUpdate()` forwards to `_adapter.HandleExecutionUpdate()`
3. `HandleExecutionUpdateReal()` detects fill and calls `HandleEntryFill()`
4. `HandleEntryFill()` submits protective stop and target orders

## Diagnostic Findings

### No Fill Events in Logs
- **No `EXECUTION_FILLED` events** found in `robot_NQ.jsonl`
- **No `EXECUTION_PARTIAL_FILL` events** found
- **No `EXECUTION_ERROR` events** found

This suggests the fill was **not detected** by the execution adapter.

## Possible Root Causes

### 1. Order Tag Missing or Invalid
**Location**: `NinjaTraderSimAdapter.NT.cs:888-890`
```csharp
var encodedTag = GetOrderTag(order);
var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
if (string.IsNullOrEmpty(intentId)) return; // strict: non-robot orders ignored
```

**Issue**: If the order tag is missing or doesn't decode to a valid intentId, the execution is silently ignored.

**Check**: Verify the entry order has a valid tag set:
- Entry orders should have tag: `RobotOrderIds.EncodeTag(intentId)`
- Check `ORDER_SUBMIT_SUCCESS` events for the entry order - does it show a tag?

### 2. Order Not Tracked in _orderMap
**Location**: `NinjaTraderSimAdapter.NT.cs:896-901`
```csharp
if (!_orderMap.TryGetValue(intentId, out var orderInfo))
{
    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", "EXECUTION_UPDATE_UNKNOWN_ORDER",
        new { error = "Order not found in tracking map", broker_order_id = order.OrderId, tag = encodedTag }));
    return;
}
```

**Issue**: If the order wasn't registered in `_orderMap` when submitted, the fill won't be processed.

**Check**: Look for `EXECUTION_UPDATE_UNKNOWN_ORDER` events in logs.

### 3. Intent Not Registered in _intentMap
**Location**: `NinjaTraderSimAdapter.NT.cs:981-987`
```csharp
if (orderInfo.IsEntryOrder && _intentMap.TryGetValue(intentId, out var entryIntent))
{
    // Register exposure with coordinator
    _coordinator?.OnEntryFill(intentId, filledTotal, entryIntent.Stream, entryIntent.Instrument, entryIntent.Direction ?? "", utcNow);
    
    // Ensure we protect the currently filled quantity (no market-close gating)
    HandleEntryFill(intentId, entryIntent, fillPrice, filledTotal, utcNow);
}
```

**Issue**: If the intent wasn't registered via `RegisterIntent()`, `HandleEntryFill()` won't be called.

**Check**: Verify `RegisterIntent()` was called when the entry order was submitted.

### 4. Intent Incomplete (Missing Direction/StopPrice/TargetPrice)
**Location**: `NinjaTraderSimAdapter.cs:332-337`
```csharp
if (intent.Direction == null || intent.StopPrice == null || intent.TargetPrice == null)
{
    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "EXECUTION_ERROR",
        new { error = "Intent incomplete - cannot submit protective orders", intent_id = intentId }));
    return;
}
```

**Issue**: If the intent is missing Direction, StopPrice, or TargetPrice, protective orders won't be placed.

**Check**: Look for `EXECUTION_ERROR` events with message "Intent incomplete - cannot submit protective orders".

### 5. Protective Order Submission Failed
**Location**: `NinjaTraderSimAdapter.cs:421-467`

If protective order submission fails after retries, the position is flattened and the stream is stood down.

**Check**: Look for `PROTECTIVE_ORDERS_FAILED_FLATTENED` events.

## Diagnostic Steps

1. **Check NinjaTrader Orders Tab**:
   - Verify the entry order exists and shows as filled
   - Check the order's Tag field - does it contain an encoded intentId?
   - Are there any protective orders (stop/target) at all?

2. **Check Logs for Entry Order Submission**:
   ```bash
   grep "ORDER_SUBMIT_SUCCESS" logs/robot/robot_NQ.jsonl | grep "NQ1"
   ```
   - Verify entry order was submitted successfully
   - Check if tag/intentId is present

3. **Check for Unknown Order Events**:
   ```bash
   grep "EXECUTION_UPDATE_UNKNOWN_ORDER" logs/robot/robot_NQ.jsonl
   ```

4. **Check for Execution Errors**:
   ```bash
   grep "EXECUTION_ERROR" logs/robot/robot_NQ.jsonl
   ```

5. **Check for Protective Order Failures**:
   ```bash
   grep "PROTECTIVE_ORDERS_FAILED" logs/robot/robot_NQ.jsonl
   ```

6. **Check Intent Registration**:
   - Verify `RegisterIntent()` was called when entry order was submitted
   - Check if intent has Direction, StopPrice, and TargetPrice set

## Most Likely Cause

Based on the absence of any execution events in the logs, the most likely cause is:

**The order tag is missing or invalid**, causing `HandleExecutionUpdateReal()` to return early at line 890 without logging anything.

## Next Steps

1. **Verify Order Tag**: Check if the entry order in NinjaTrader has a tag set
2. **Add Logging**: Add logging before the early return at line 890 to capture untagged orders
3. **Check Order Submission**: Verify `SetOrderTag()` is being called when orders are created
4. **Manual Test**: Manually check NinjaTrader's Orders tab to see if protective orders exist but weren't logged

## Code Locations

- Order tag encoding: `RobotOrderIds.EncodeTag(intentId)`
- Order tag decoding: `RobotOrderIds.DecodeIntentId(encodedTag)`
- Fill detection: `NinjaTraderSimAdapter.NT.cs:881-994`
- Protective order submission: `NinjaTraderSimAdapter.cs:330-497`
