# Phase C.1 Complete - Real NinjaTrader SIM Order Placement

**Status**: COMPLETE - Real NT API Integration Ready

**Date**: 2026-01-02

## Integration Surface

**SIM adapter is hosted in `RobotSimStrategy` (NinjaTrader Strategy) and receives callbacks via `Account.OrderUpdate` and `Account.ExecutionUpdate` events.**

The adapter runs inside NinjaTrader's Strategy context, which provides:
- `Account` object (for order submission)
- `Instrument` object (for order creation)  
- `OrderUpdate` and `ExecutionUpdate` events (for callbacks)

## Real NinjaTrader API Calls Used

### Entry Order Submission
```csharp
// Create order
var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;
var orderType = entryPrice.HasValue ? OrderType.Limit : OrderType.Market;
var order = account.CreateOrder(instrument, orderAction, orderType, quantity, entryPrice ?? 0);
order.Tag = intentId; // Namespace by intent_id
order.TimeInForce = TimeInForce.Day;

// Submit order
var result = account.Submit(new[] { order });
if (result[0].OrderState == OrderState.Rejected) { /* Handle rejection */ }
```

**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - `SubmitEntryOrderReal()`

### Stop Order Submission
```csharp
var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
var order = account.CreateOrder(instrument, orderAction, OrderType.StopMarket, quantity, stopPrice);
order.Tag = $"{intentId}_STOP";
var result = account.Submit(new[] { order });
```

**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - `SubmitProtectiveStopReal()`

### Target Order Submission
```csharp
var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
var order = account.CreateOrder(instrument, orderAction, OrderType.Limit, quantity, targetPrice);
order.Tag = $"{intentId}_TARGET";
var result = account.Submit(new[] { order });
```

**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - `SubmitTargetOrderReal()`

### Stop Modification (Break-Even)
```csharp
// Find existing stop order
var stopOrder = account.Orders.FirstOrDefault(o => 
    o.Tag == $"{intentId}_STOP" && 
    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));

// Modify stop price
stopOrder.StopPrice = beStopPrice;
var result = account.Change(new[] { stopOrder });
```

**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - `ModifyStopToBreakEvenReal()`

## Event Wiring

### OrderUpdate Event Handler
```csharp
private void OnOrderUpdate(object sender, OrderUpdateEventArgs e)
{
    var order = e.Order;
    var intentId = order.Tag as string;
    var orderState = order.OrderState;
    
    // Forward to adapter
    _adapter.HandleOrderUpdate(order, e.OrderUpdate);
}
```

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs` - `OnOrderUpdate()`

### ExecutionUpdate Event Handler
```csharp
private void OnExecutionUpdate(object sender, ExecutionUpdateEventArgs e)
{
    var execution = e.Execution;
    var order = e.Order;
    
    // Forward to adapter (triggers protective orders on fill)
    _adapter.HandleExecutionUpdate(execution, order);
}
```

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs` - `OnExecutionUpdate()`

## Files Modified

- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs` - Routes to real NT APIs when context is set
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - Real NT API implementations (NEW)
- `modules/robot/ninjatrader/RobotSimStrategy.cs` - NT Strategy host (NEW)
- `modules/robot/core/Execution/ExecutionJournal.cs` - BE modification tracking

## Files Created

- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - Real NT API implementations
- `modules/robot/ninjatrader/RobotSimStrategy.cs` - NT Strategy host
- `docs/robot/execution/NT_INTEGRATION.md` - Integration documentation
- `docs/robot/execution/PHASE_C1_COMPLETE.md` - This document

## Implementation Architecture

### Dual-Mode Operation

The adapter supports two modes:

1. **Harness Mode** (standalone .NET console):
   - Uses mock implementations
   - No NT API dependencies
   - For testing and development

2. **NT Strategy Mode** (inside NinjaTrader):
   - Uses real NT API calls
   - Requires NT context (Account, Instrument)
   - Event-driven via NT callbacks

### Context Injection

The Strategy host (`RobotSimStrategy`) injects NT context into the adapter:

```csharp
adapter.SetNTContext(Account, Instrument);
```

This enables the adapter to use real NT APIs.

### Event Forwarding

NT events are forwarded from Strategy to adapter:

```csharp
Account.OrderUpdate += OnOrderUpdate;      // Strategy handler
Account.ExecutionUpdate += OnExecutionUpdate; // Strategy handler

// Strategy forwards to adapter:
_adapter.HandleOrderUpdate(order, orderUpdate);
_adapter.HandleExecutionUpdate(execution, order);
```

## Expected Log Flow (Real NT Execution)

When running inside NinjaTrader with real orders:

```
EXECUTION_MODE_SET { mode: "SIM", adapter: "NinjaTraderSimAdapter" }
SIM_ACCOUNT_VERIFIED { account_name: "Sim101" }
ORDER_SUBMIT_ATTEMPT { order_type: "ENTRY", direction: "Long", ... }
ORDER_SUBMIT_SUCCESS { broker_order_id: "NT_ORDER_ID_12345", ... }
ORDER_ACKNOWLEDGED { broker_order_id: "NT_ORDER_ID_12345", ... }
EXECUTION_FILLED { fill_price: 5000.25, fill_quantity: 1, broker_order_id: "NT_ORDER_ID_12345" }
ORDER_SUBMIT_ATTEMPT { order_type: "PROTECTIVE_STOP", ... }
ORDER_SUBMIT_SUCCESS { broker_order_id: "NT_ORDER_ID_12346", ... }
ORDER_SUBMIT_ATTEMPT { order_type: "TARGET", ... }
ORDER_SUBMIT_SUCCESS { broker_order_id: "NT_ORDER_ID_12347", ... }
PROTECTIVE_ORDERS_SUBMITTED { stop_order_id: "NT_ORDER_ID_12346", target_order_id: "NT_ORDER_ID_12347", ... }
```

## Next Steps for Testing

1. **Copy Strategy to NT**: Copy `RobotSimStrategy.cs` into NinjaTrader 8 strategy project
2. **Wire References**: Add reference to `Robot.Core.dll`
3. **Run in SIM**: Enable strategy on SIM account with ES instrument
4. **Verify Logs**: Check for real NT order IDs and fill events
5. **Test Protective Orders**: Verify stop/target submitted after entry fill
6. **Test Idempotency**: Restart and verify no duplicate submissions

## Safety Guarantees Maintained

✅ **SIM-only enforcement**: Adapter fails closed if account is not Sim
✅ **No Analyzer changes**: Analyzer logic untouched
✅ **No intent schema changes**: Intent structure unchanged
✅ **No RobotEngine logic changes**: Only adapter callbacks added
✅ **No LIVE enablement**: LIVE mode still disabled
✅ **Idempotency**: ExecutionJournal prevents double-submission
✅ **DRYRUN parity**: DRYRUN logs identical to pre-Phase C.1

---

**Phase C.1 is complete.** Real NinjaTrader API integration is implemented and ready for testing inside NinjaTrader Strategy context.
