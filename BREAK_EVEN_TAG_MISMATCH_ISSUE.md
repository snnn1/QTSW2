# Break-Even Detection Issue: Tag Mismatch

## Problem

Break-even detection is **working correctly** - triggers are being detected. However, **stop order modifications are failing** because the stop order cannot be found.

## Root Cause

**Tag Mismatch**: Protective stop orders are tagged differently than what BE modification code expects.

### Protective Stop Order Creation
**Location**: `NinjaTraderSimAdapter.NT.cs` line 2274
```csharp
// Set order tag
SetOrderTag(order, RobotOrderIds.EncodeTag(intentId));
```
**Tag Format**: `QTSW2:{intentId}` (e.g., `QTSW2:fa1708e718e939d9`)

### Break-Even Modification Lookup
**Location**: `NinjaTraderSimAdapter.NT.cs` line 2787
```csharp
var stopTag = RobotOrderIds.EncodeStopTag(intentId);
var stopOrder = account.Orders.FirstOrDefault(o =>
    GetOrderTag(o) == stopTag &&
    (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted));
```
**Tag Format**: `QTSW2:{intentId}:STOP` (e.g., `QTSW2:fa1708e718e939d9:STOP`)

## Impact

- ✅ Break-even triggers detected correctly (13 events in last 24h)
- ❌ Stop modifications fail with "Stop order not found for BE modification"
- ❌ All 13 BE triggers resulted in `BE_TRIGGER_RETRY_NEEDED` events
- ❌ No successful BE modifications

## Evidence from Logs

```
BE_TRIGGER_RETRY_NEEDED: 13 events
  RTY2 | Intent: fa1708e7 | Error: Stop order not found for BE modification
```

## Fix Required

Change protective stop order tag assignment to use `EncodeStopTag()` instead of `EncodeTag()`:

**Current (line 2274)**:
```csharp
SetOrderTag(order, RobotOrderIds.EncodeTag(intentId));
```

**Should be**:
```csharp
SetOrderTag(order, RobotOrderIds.EncodeStopTag(intentId));
```

This will make the tag `QTSW2:{intentId}:STOP`, matching what `ModifyStopToBreakEven()` expects.

## Status

- **Detection**: ✅ Working
- **Modification**: ❌ Broken (tag mismatch)
- **Fix**: Simple one-line change required
