# ES Re-Entry After Stop Loss Fix

## Problem

When ES hit a protective stop loss, a new long order was placed immediately after. This happened because:

1. **Stop brackets submitted**: Both long and short entry stop orders were placed at RANGE_LOCKED
2. **One entry filled**: Short entry filled (for example)
3. **OCO should cancel**: Long entry stop should be cancelled by OCO group
4. **OCO failed/race condition**: Long entry stop remained active
5. **Protective stop filled**: Stop loss filled, closing the position
6. **Opposite entry still active**: Long entry stop was still active
7. **Re-entry occurred**: When price moved back up, long entry stop filled, creating unwanted new position

## Root Cause

When a protective stop loss fills, the code only:
- Records the exit fill
- Updates the coordinator
- **Does NOT cancel the opposite entry stop order**

The code relies on OCO groups to cancel the opposite entry order, but if OCO fails or there's a race condition, the opposite entry stop can remain active.

## Fix

**Location**: `NinjaTraderSimAdapter.NT.cs` lines 1958-2002

**Change**: When a protective stop loss fills, explicitly cancel the opposite entry stop order.

**Logic**:
1. When protective stop fills, get the filled intent from `_intentMap`
2. Determine opposite direction (Long ↔ Short)
3. Search `_intentMap` for opposite entry intent (same stream, opposite direction, entry stop bracket trigger)
4. Cancel the opposite entry order using `CancelIntentOrders()`
5. Log the cancellation

## Code Added

```csharp
// CRITICAL FIX: When protective stop fills, cancel opposite entry stop order to prevent re-entry
if (orderInfo.OrderType == "STOP" && _intentMap.TryGetValue(intentId, out var filledIntent))
{
    var oppositeDirection = filledIntent.Direction == "Long" ? "Short" : "Long";
    
    // Find opposite intent by searching _intentMap
    string? oppositeIntentId = null;
    foreach (var kvp in _intentMap)
    {
        var otherIntent = kvp.Value;
        if (otherIntent.Stream == filledIntent.Stream &&
            otherIntent.TradingDate == filledIntent.TradingDate &&
            otherIntent.Direction == oppositeDirection &&
            otherIntent.TriggerReason != null &&
            (otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") || 
             otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
        {
            oppositeIntentId = kvp.Key;
            break;
        }
    }
    
    // Cancel opposite entry order if found
    if (oppositeIntentId != null)
    {
        CancelIntentOrders(oppositeIntentId, utcNow);
        // Log cancellation
    }
}
```

## Expected Behavior After Fix

1. **Protective stop fills** → Position closed
2. **Opposite entry order cancelled** → No re-entry possible
3. **Stream can commit** → Trade complete

## Status

✅ **Fix applied** to both `modules/robot/core` and `RobotCore_For_NinjaTrader`
✅ **DLL rebuilt** and copied to NinjaTrader
⏳ **Requires restart** of NinjaTrader to load new DLL

## Next Steps

1. Restart NinjaTrader to load new DLL
2. Monitor logs for `OPPOSITE_ENTRY_CANCELLED_ON_STOP_FILL` events
3. Verify no re-entry occurs after stop loss fills
