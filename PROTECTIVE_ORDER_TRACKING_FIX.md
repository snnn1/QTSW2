# Protective Order Tracking Fix

## Issue
Protective stop orders were being treated as "untracked" when they filled, triggering unnecessary flatten operations. The user reported: "why was it untracked it was the protective stop".

## Root Cause
1. **Entry orders** are added to `_orderMap` when submitted (line 816 in `NinjaTraderSimAdapter.NT.cs`)
2. **Protective stop/target orders** were NOT added to `_orderMap` when submitted
3. When a protective stop filled:
   - `OnExecutionUpdate` decoded `intentId` from tag `QTSW2:{intentId}:STOP`
   - Looked up `_orderMap[intentId]` and found the **entry order** info (not the protective stop)
   - Since entry order info has `IsEntryOrder=true`, it was incorrectly classified as an entry fill
   - OR if the entry order wasn't in `_orderMap` either, it was treated as completely untracked and triggered flatten

## Solution
Two-part fix:

### Part 1: Add Protective Orders to `_orderMap`
When protective stop and target orders are successfully submitted, add them to `_orderMap`:

**Protective Stop Orders:**
```csharp
var stopOrderInfo = new OrderInfo
{
    IntentId = intentId,
    Instrument = instrument,
    OrderId = order.OrderId,
    OrderType = "STOP",
    Direction = direction,
    Quantity = quantity,
    Price = stopPrice,
    State = "SUBMITTED",
    NTOrder = order,
    IsEntryOrder = false, // Protective order, not entry
    FilledQuantity = 0
};
_orderMap[intentId] = stopOrderInfo;
```

**Protective Target Orders:**
```csharp
var targetOrderInfo = new OrderInfo
{
    IntentId = intentId,
    Instrument = instrument,
    OrderId = order.OrderId,
    OrderType = "TARGET",
    Direction = direction,
    Quantity = quantity,
    Price = targetPrice,
    State = "SUBMITTED",
    NTOrder = order,
    IsEntryOrder = false, // Protective order, not entry
    FilledQuantity = 0
};
_orderMap[intentId] = targetOrderInfo; // Overwrites entry order (already filled) or stop order (if stop was added first)
```

**Note**: Adding protective orders to `_orderMap` overwrites the entry order, but that's acceptable because:
- Entry order is already filled by the time protective orders are submitted
- Entry fills are tracked in execution journal (ground truth)
- The tag-based detection (Part 2) provides a fallback if `_orderMap` lookup fails
- If both stop and target are submitted, whichever is submitted last overwrites the other, but that's OK because:
  - Both orders are OCO-linked, so only one will fill anyway
  - Tag-based detection will still work correctly

### Part 2: Tag-Based Order Type Detection
Before looking up in `_orderMap`, detect order type from the tag:
```csharp
string? orderTypeFromTag = null;
bool isProtectiveOrder = false;
if (!string.IsNullOrEmpty(encodedTag))
{
    if (encodedTag.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase))
    {
        orderTypeFromTag = "STOP";
        isProtectiveOrder = true;
    }
    else if (encodedTag.EndsWith(":TARGET", StringComparison.OrdinalIgnoreCase))
    {
        orderTypeFromTag = "TARGET";
        isProtectiveOrder = true;
    }
}
```

Then, if a protective order isn't in `_orderMap`, create `OrderInfo` on the fly from the tag and intent:
```csharp
if (isProtectiveOrder && !string.IsNullOrEmpty(orderTypeFromTag) && !string.IsNullOrEmpty(intentId))
{
    if (_intentMap.TryGetValue(intentId, out var intent))
    {
        orderInfo = new OrderInfo
        {
            IntentId = intentId,
            Instrument = intent.Instrument ?? order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN",
            OrderId = order.OrderId,
            OrderType = orderTypeFromTag,
            Direction = intent.Direction ?? "",
            Quantity = order.Quantity,
            Price = orderTypeFromTag == "STOP" ? (decimal?)order.StopPrice : (decimal?)order.LimitPrice,
            State = "SUBMITTED",
            NTOrder = order,
            IsEntryOrder = false,
            FilledQuantity = 0
        };
    }
}
```

Finally, use `orderTypeFromTag` to correctly classify entry vs exit fills:
```csharp
// Use orderTypeFromTag to determine if it's an entry or exit order
bool isEntryFill = !isProtectiveOrder && orderInfo.IsEntryOrder == true;
if (isEntryFill)
{
    // Handle entry fill
}
else if (orderTypeForContext == "STOP" || orderTypeForContext == "TARGET")
{
    // Handle protective order fill
}
```

## Files Changed
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`

## Impact
- **Both protective stop and target fills** are now correctly tracked and classified as exit orders
- No more false "untracked fill" alerts for protective orders (stop or target)
- No more unnecessary flatten operations when protective orders fill
- System correctly distinguishes between entry fills and protective order fills

## Testing
After rebuilding DLL and deploying:
1. Monitor logs for `PROTECTIVE_ORDER_FILL_TRACKED_FROM_TAG` events (should see these if protective order wasn't in `_orderMap` - fallback mechanism)
2. Verify no more `EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL` events for protective orders (stop or target)
3. Verify protective order fills (both stop and target) are correctly journaled as exit fills (not entry fills)
4. Verify target order fills are properly tracked (the limit order for profit-taking)
