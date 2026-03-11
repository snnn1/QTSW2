# M2K Protective Order Bug Fix

## Issue Summary

When M2K filled its entry order, the robot attempted to submit protective orders (stop loss and target), but the target order submission failed with:

```
"PROTECTIVE_ORDER_FAILURE: Protective orders failed after 3 retries: TARGET: Target order submission failed: No overload for method 'CreateOrder' takes '4' arguments"
```

This caused:
1. **Protective orders failed** - Target order couldn't be created
2. **Position was flattened** - Emergency safety measure activated
3. **Stream was stood down** - RTY1 stream disabled
4. **Robot shutdown** - Engine stopped

## Root Cause

The bug was in `SubmitTargetOrderReal()` method in `NinjaTraderSimAdapter.NT.cs`:

1. **Wrong CreateOrder signature**: Line 1782 was calling `CreateOrder` with 5 arguments:
   ```csharp
   order = dynAccountTarget.CreateOrder(ntInstrument, orderAction, OrderType.Limit, quantity, (double)targetPrice);
   ```
   But NinjaTrader's `CreateOrder` for Limit orders doesn't accept the price as the 5th argument.

2. **Variable name bug**: Line 1731 had a typo - `dynAccountChange` should have been `dynAccountChangeTarget` in the fallback error handling.

3. **Inconsistent with Stop order**: The stop order submission (line 1518) correctly uses the full 11-argument `CreateOrder` signature, but the target order was using an incorrect 5-argument version.

## Fix Applied

### 1. Fixed CreateOrder Signature
Updated `SubmitTargetOrderReal()` to use the same 11-argument `CreateOrder` signature as the stop order:

```csharp
order = account.CreateOrder(
    ntInstrument,                           // Instrument
    orderAction,                            // OrderAction
    OrderType.Limit,                        // OrderType
    OrderEntry.Manual,                      // OrderEntry
    TimeInForce.Day,                        // TimeInForce
    quantity,                               // Quantity
    targetPriceD,                           // LimitPrice
    0.0,                                    // StopPrice (0 for Limit orders)
    null,                                   // Oco (will be set after creation if needed)
    $"{intentId}_TARGET",                   // OrderName
    DateTime.MinValue,                      // Gtd
    null                                    // CustomOrder
);
```

### 2. Fixed Variable Name Bug
Changed line 1731 from:
```csharp
dynAccountChange.Change(new[] { existingTarget });
```
to:
```csharp
dynAccountChangeTarget.Change(new[] { existingTarget });
```

### 3. Added Safety Checks
Added the same runtime safety checks that exist for stop orders:
- NT context validation
- Account null check
- Instrument null check
- Quantity validation (> 0)
- Price validation (> 0)

### 4. Removed Duplicate Code
Removed duplicate `SetOrderTag` call that was outside the try block.

## Impact

- **Before**: Target orders failed to submit → position flattened → stream disabled → robot shutdown
- **After**: Target orders will submit correctly using the proper CreateOrder signature

## Testing Recommendations

1. **Monitor next M2K fill**: Watch for successful protective order submission
2. **Check logs**: Verify `PROTECTIVE_ORDERS_SUBMITTED` event appears (not `PROTECTIVE_ORDERS_FAILED_FLATTENED`)
3. **Verify OCO pairing**: Ensure stop and target orders are properly OCO paired (may need additional fix)

## Additional Fix: OCO Pairing

**Critical Safety Issue**: Stop and target orders were not OCO paired, which could allow both to fill simultaneously.

**Fix Applied**:
- Added `ocoGroup` parameter to `SubmitProtectiveStop()` and `SubmitTargetOrder()` methods
- Generate OCO group string in `HandleEntryFill()`: `$"QTSW2:{intentId}_PROTECTIVE"`
- Pass OCO group to both stop and target order creation
- Updated interface `IExecutionAdapter` to include `ocoGroup` parameter
- Updated all adapter implementations (Sim, Live, Null)

## Files Modified

- `modules/robot/core/Execution/IExecutionAdapter.cs`
  - Added `ocoGroup` parameter to `SubmitProtectiveStop()` and `SubmitTargetOrder()`

- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs`
  - Updated `HandleEntryFill()` to generate and pass OCO group
  - Updated `SubmitProtectiveStop()` and `SubmitTargetOrder()` signatures

- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
  - Fixed `SubmitTargetOrderReal()` method (lines ~1776-1914)
  - Fixed variable name bug in fallback error handling (line 1731)
  - Updated `SubmitProtectiveStopReal()` and `SubmitTargetOrderReal()` to accept and use `ocoGroup`
  - Set OCO group in CreateOrder calls for both stop and target orders

- `modules/robot/core/Execution/NullExecutionAdapter.cs`
  - Updated method signatures to match interface

- `modules/robot/core/Execution/NinjaTraderLiveAdapter.cs`
  - Updated method signatures to match interface

## Related Documentation

- `ORDER_FILL_WORKFLOW.md` - Documents the expected workflow when orders fill
- `ORDER_EXECUTION_ERRORS_ANALYSIS.md` - Historical error analysis

## Notes

- The OCO pairing between stop and target orders may need additional implementation if not handled automatically by NinjaTrader
- The fix ensures consistency between stop and target order creation patterns
