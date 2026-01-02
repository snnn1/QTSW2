# NinjaTrader Integration Surface

## Hosting Context

**SIM adapter is hosted in `RobotSimStrategy` (NinjaTrader Strategy) and receives callbacks via `Account.OrderUpdate` and `Account.ExecutionUpdate` events.**

The adapter runs inside NinjaTrader's Strategy context, which provides:
- `Account` object (for order submission)
- `Instrument` object (for order creation)
- `OrderUpdate` and `ExecutionUpdate` events (for callbacks)

## Integration Flow

1. **Strategy Initialization**: `RobotSimStrategy` creates `RobotEngine` in SIM mode
2. **Context Injection**: Strategy provides `Account` and `Instrument` to adapter via `SetNTContext()`
3. **Event Wiring**: Strategy subscribes to NT events and forwards them to adapter
4. **Order Submission**: Adapter uses NT `Account.CreateOrder()` and `Account.Submit()`
5. **Fill Handling**: NT events trigger adapter callbacks, which submit protective orders

## NinjaTrader API Calls Used

### Entry Order Submission
```csharp
// Create order
var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;
var orderType = entryPrice.HasValue ? OrderType.Limit : OrderType.Market;
var order = account.CreateOrder(instrument, orderAction, orderType, quantity, entryPrice ?? 0);
order.Tag = intentId; // Namespace by intent_id
order.TimeInForce = TimeInForce.Day;

// Submit order
var result = account.Submit(order);
if (result == null || result.OrderState == OrderState.Rejected)
{
    // Handle rejection
}
```

### Stop Order Submission
```csharp
var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
var order = account.CreateOrder(instrument, orderAction, OrderType.StopMarket, quantity, stopPrice);
order.Tag = $"{intentId}_STOP";
var result = account.Submit(order);
```

### Target Order Submission
```csharp
var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
var order = account.CreateOrder(instrument, orderAction, OrderType.Limit, quantity, targetPrice);
order.Tag = $"{intentId}_TARGET";
var result = account.Submit(order);
```

### Stop Modification (Break-Even)
```csharp
// Find existing stop order
var stopOrder = account.Orders.FirstOrDefault(o => o.Tag == $"{intentId}_STOP" && o.OrderState == OrderState.Working);
if (stopOrder != null)
{
    stopOrder.StopPrice = beStopPrice;
    var result = account.Change(stopOrder);
}
```

## Event Handling

### OrderUpdate Event
```csharp
private void OnOrderUpdate(object sender, OrderUpdateEventArgs e)
{
    var order = e.Order;
    var intentId = order.Tag as string;
    var orderState = order.OrderState;
    
    // Update journal based on order state
    if (orderState == OrderState.Accepted) { /* ACKNOWLEDGED */ }
    if (orderState == OrderState.Rejected) { /* REJECTED */ }
    if (orderState == OrderState.Cancelled) { /* CANCELLED */ }
}
```

### ExecutionUpdate Event
```csharp
private void OnExecutionUpdate(object sender, ExecutionUpdateEventArgs e)
{
    var execution = e.Execution;
    var order = e.Order;
    var intentId = order.Tag as string;
    var fillPrice = execution.Price;
    var fillQuantity = execution.Quantity;
    
    // Update journal: PARTIAL_FILL or FILLED
    // On full fill, trigger protective order submission
    if (execution.Quantity == order.Quantity && order.OrderType == OrderType.Market)
    {
        HandleEntryFill(intentId, fillPrice, fillQuantity);
    }
}
```
