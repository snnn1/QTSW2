# Phase C.1 Artifacts - Real NT API Integration

## Integration Surface

**SIM adapter is hosted in `RobotSimStrategy` (NinjaTrader Strategy) and receives callbacks via `Account.OrderUpdate` and `Account.ExecutionUpdate` events.**

## Real NinjaTrader API Calls Used

### 1. Entry Order Submission
**Location**: `NinjaTraderSimAdapter.NT.cs` - `SubmitEntryOrderReal()`

```csharp
var orderAction = direction == "Long" ? OrderAction.Buy : OrderAction.SellShort;
var orderType = entryPrice.HasValue ? OrderType.Limit : OrderType.Market;
var order = account.CreateOrder(instrument, orderAction, orderType, quantity, entryPrice ?? 0);
order.Tag = intentId;
order.TimeInForce = TimeInForce.Day;
var result = account.Submit(new[] { order });
```

**NT API**: `Account.CreateOrder()`, `Account.Submit()`

### 2. Stop Order Submission
**Location**: `NinjaTraderSimAdapter.NT.cs` - `SubmitProtectiveStopReal()`

```csharp
var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
var order = account.CreateOrder(instrument, orderAction, OrderType.StopMarket, quantity, stopPrice);
order.Tag = $"{intentId}_STOP";
var result = account.Submit(new[] { order });
```

**NT API**: `Account.CreateOrder()`, `Account.Submit()`

### 3. Target Order Submission
**Location**: `NinjaTraderSimAdapter.NT.cs` - `SubmitTargetOrderReal()`

```csharp
var orderAction = direction == "Long" ? OrderAction.Sell : OrderAction.BuyToCover;
var order = account.CreateOrder(instrument, orderAction, OrderType.Limit, quantity, targetPrice);
order.Tag = $"{intentId}_TARGET";
var result = account.Submit(new[] { order });
```

**NT API**: `Account.CreateOrder()`, `Account.Submit()`

### 4. Stop Modification (Break-Even)
**Location**: `NinjaTraderSimAdapter.NT.cs` - `ModifyStopToBreakEvenReal()`

```csharp
var stopOrder = account.Orders.FirstOrDefault(o => 
    o.Tag == $"{intentId}_STOP" && 
    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));
stopOrder.StopPrice = beStopPrice;
var result = account.Change(new[] { stopOrder });
```

**NT API**: `Account.Orders.FirstOrDefault()`, `Account.Change()`

## Expected Log Flow (Real NT Execution)

When running inside NinjaTrader with real orders, logs should show:

```
EXECUTION_MODE_SET { mode: "SIM", adapter: "NinjaTraderSimAdapter" }
SIM_ACCOUNT_VERIFIED { account_name: "Sim101" }
ORDER_SUBMIT_ATTEMPT { order_type: "ENTRY", direction: "Long", entry_price: 5000.25, quantity: 1 }
ORDER_SUBMIT_SUCCESS { broker_order_id: "NT_ORDER_ID_12345", order_type: "ENTRY", ... }
ORDER_ACKNOWLEDGED { broker_order_id: "NT_ORDER_ID_12345", order_type: "ENTRY" }
EXECUTION_FILLED { fill_price: 5000.25, fill_quantity: 1, broker_order_id: "NT_ORDER_ID_12345", order_type: "ENTRY" }
ORDER_SUBMIT_ATTEMPT { order_type: "PROTECTIVE_STOP", direction: "Long", stop_price: 4990.00, quantity: 1 }
ORDER_SUBMIT_SUCCESS { broker_order_id: "NT_ORDER_ID_12346", order_type: "PROTECTIVE_STOP", ... }
ORDER_SUBMIT_ATTEMPT { order_type: "TARGET", direction: "Long", target_price: 5010.00, quantity: 1 }
ORDER_SUBMIT_SUCCESS { broker_order_id: "NT_ORDER_ID_12347", order_type: "TARGET", ... }
PROTECTIVE_ORDERS_SUBMITTED { stop_order_id: "NT_ORDER_ID_12346", target_order_id: "NT_ORDER_ID_12347", ... }
```

## Execution Summary JSON Sample

```json
{
  "intents_seen": 1,
  "intents_executed": 1,
  "orders_submitted": 3,
  "orders_rejected": 0,
  "orders_filled": 1,
  "orders_blocked": 0,
  "blocked_by_reason": {},
  "duplicates_skipped": 0,
  "intent_details": [
    {
      "intent_id": "abc123def456",
      "trading_date": "2025-12-01",
      "stream": "ES1",
      "instrument": "ES",
      "executed": true,
      "orders_submitted": 3,
      "orders_rejected": 0,
      "orders_filled": 1,
      "order_types": ["ENTRY", "STOP", "TARGET"],
      "rejection_reasons": [],
      "blocked": false,
      "duplicate_skipped": false
    }
  ]
}
```

## SIM Account Verification

The adapter verifies SIM account using:

```csharp
var account = _ntAccount as Account;
if (!account.IsSimAccount)
{
    throw new InvalidOperationException("Account is not Sim account");
}
```

**NT API**: `Account.IsSimAccount` property

## Idempotency

ExecutionJournal stores real NT order IDs:

```json
{
  "intent_id": "abc123def456",
  "broker_order_id": "NT_ORDER_ID_12345",
  "entry_submitted": true,
  "entry_filled": true,
  "fill_price": 5000.25,
  "fill_quantity": 1
}
```

On restart, the journal prevents duplicate submissions by checking `IsIntentSubmitted(intentId, tradingDate, stream)`.

## Kill Switch Test

When kill switch is enabled (`configs/robot/kill_switch.json`):

```json
{
  "message": "SIM smoke test - kill switch enabled",
  "enabled": true
}
```

Expected logs:
```
EXECUTION_BLOCKED { reason: "KILL_SWITCH_ACTIVE", intent_id: "...", ... }
```

No orders should be submitted.

---

**Phase C.1 implementation is complete.** Real NT API integration is ready for testing inside NinjaTrader Strategy context.
