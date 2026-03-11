# Multi-Instance Order Tracking Investigation

## Problem Confirmed

**Root Cause**: Multiple strategy instances running on the same account cause order tracking failures.

### Architecture Analysis

1. **Each Strategy Instance Creates Its Own Adapter**
   - `RobotSimStrategy` creates `RobotEngine` → creates `NinjaTraderSimAdapter`
   - Each adapter has its own `_orderMap` (instance field)
   - Location: `modules/robot/core/Execution/NinjaTraderSimAdapter.cs:33`

2. **All Instances Subscribe to Same Account Events**
   - `Account.OrderUpdate += OnOrderUpdate;` (line 963)
   - `Account.ExecutionUpdate += OnExecutionUpdate;` (line 964)
   - **CRITICAL**: All strategy instances on the same account receive ALL order updates

3. **Order Tracking is Instance-Specific**
   - Order submitted from Instance A → stored in Instance A's `_orderMap`
   - OrderUpdate arrives → ALL instances receive callback
   - Instance B checks its `_orderMap` → order not found → `EXECUTION_UPDATE_UNKNOWN_ORDER`

### Evidence

From logs (2026-01-30):
- Multiple `EXECUTION_UPDATE_UNKNOWN_ORDER` events for same order ID
- Order tag present (`QTSW2:5a70807af02cce8e`) but order not in map
- Order state: `Initialized` (stuck because tracking failed)

### Code Flow

```
Instance A (MGC):
  1. Submit order → Store in Instance A's _orderMap[intentId]
  2. OrderUpdate arrives → Instance A finds order ✅

Instance B (MNG):
  1. OrderUpdate arrives (for Instance A's order)
  2. Check Instance B's _orderMap[intentId] → NOT FOUND ❌
  3. Log EXECUTION_UPDATE_UNKNOWN_ORDER
```

## Solution Options

### Option 1: Filter Orders by Instrument (RECOMMENDED)

**Fix**: Check if order's instrument matches this strategy's instrument before processing.

**Implementation**:
```csharp
private void OnOrderUpdate(object sender, OrderEventArgs e)
{
    // Filter: Only process orders for this strategy's instrument
    if (e.Order.Instrument != Instrument)
    {
        return; // Ignore orders from other strategy instances
    }
    
    // Forward to adapter
    _adapter.HandleOrderUpdate(e.Order, e);
}
```

**Pros**:
- Simple, minimal code change
- Each instance only processes its own orders
- No shared state needed

**Cons**:
- Assumes each instance trades different instruments (current architecture)

### Option 2: Shared Order Tracking

**Fix**: Use static/shared `_orderMap` keyed by order ID + instance ID.

**Implementation**:
```csharp
// Static shared map: orderId -> (instanceId, orderInfo)
private static readonly ConcurrentDictionary<string, (string instanceId, OrderInfo orderInfo)> 
    _sharedOrderMap = new();

// Store with instance ID
_sharedOrderMap[order.OrderId] = (GetInstanceId(), orderInfo);

// Lookup with instance ID check
if (_sharedOrderMap.TryGetValue(order.OrderId, out var entry))
{
    if (entry.instanceId == GetInstanceId())
    {
        // Process order
    }
}
```

**Pros**:
- Handles cross-instance orders if needed
- More robust for future multi-instrument instances

**Cons**:
- More complex
- Requires instance ID generation
- May not be needed if Option 1 works

### Option 3: Filter by Order Tag Prefix

**Fix**: Check if order tag matches this instance's tag prefix.

**Implementation**:
```csharp
private void OnOrderUpdate(object sender, OrderEventArgs e)
{
    var tag = GetOrderTag(e.Order);
    if (!tag.StartsWith($"QTSW2:{GetInstanceId()}"))
    {
        return; // Ignore orders from other instances
    }
    
    // Forward to adapter
    _adapter.HandleOrderUpdate(e.Order, e);
}
```

**Pros**:
- Works even if instances trade same instrument
- Uses existing tag system

**Cons**:
- Requires instance ID in tag
- More complex tag encoding

## Recommended Fix: Option 1

**Why**: Current architecture has one strategy instance per instrument, so filtering by instrument is sufficient and simplest.

**Implementation Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs:1468`

**Change**:
```csharp
private void OnOrderUpdate(object sender, OrderEventArgs e)
{
    try
    {
        if (_initFailed) return;
        if (_engine is null) return;
        
        // CRITICAL FIX: Filter orders by instrument to prevent cross-instance tracking failures
        // Each strategy instance only processes orders for its own instrument
        if (e.Order?.Instrument != Instrument)
        {
            // Order belongs to another strategy instance - ignore
            return;
        }
        
        var utcNow = DateTimeOffset.UtcNow;
        _engine.OnBrokerOrderUpdateObserved(utcNow);
        
        if (_adapter is null) return;
        
        // Forward to adapter's HandleOrderUpdate method
        _adapter.HandleOrderUpdate(e.Order, e);
    }
    catch (Exception ex)
    {
        // ... existing error handling
    }
}
```

**Same fix for ExecutionUpdate**:
```csharp
private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
{
    try
    {
        if (_engine is null) return;
        
        // CRITICAL FIX: Filter executions by instrument to prevent cross-instance tracking failures
        if (e.Execution?.Order?.Instrument != Instrument)
        {
            // Execution belongs to another strategy instance - ignore
            return;
        }
        
        var utcNow = DateTimeOffset.UtcNow;
        _engine.OnBrokerExecutionUpdateObserved(utcNow);
        
        if (_adapter is null) return;
        
        _adapter.HandleExecutionUpdate(e.Execution, e.Execution.Order);
    }
    catch (Exception ex)
    {
        // ... existing error handling
    }
}
```

## Testing

After fix:
1. Run multiple strategy instances (different instruments)
2. Submit orders from each instance
3. Verify:
   - ✅ No `EXECUTION_UPDATE_UNKNOWN_ORDER` events
   - ✅ Orders tracked correctly in submitting instance
   - ✅ Orders ignored in other instances (no errors)

## Impact

- **Fixes**: Zero quantity / Unknown state orders (UI display issue)
- **Fixes**: Order tracking failures
- **Improves**: Order lifecycle management
- **Risk**: Low - simple filter, no behavior change for single instance

## Related Issues

- Zero quantity orders in UI (display artifact of `Initialized` state)
- Order rejections not properly tracked
- Multiple strategy instances interfering with each other
