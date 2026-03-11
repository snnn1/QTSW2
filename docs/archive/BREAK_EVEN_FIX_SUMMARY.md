# Break-Even Detection and Stop Modification Fixes

## Issues Identified

1. **BE Trigger Not Set in Intents**: Intents were being registered without `BeTrigger` price, causing break-even detection to fail.
2. **BE Stop Price Calculation**: BE stop price was using intended entry price instead of actual fill price, which could be inaccurate due to slippage.

## Root Causes

### Issue 1: BE Trigger Not Set
- `ComputeAndLogProtectiveOrders()` required `RangeHigh` and `RangeLow` to be set before computing BE trigger
- If range wasn't available when entry was detected, `_intendedBeTrigger` remained null
- Intent was created with `beTrigger: null`, causing `GetActiveIntentsForBEMonitoring()` to filter it out

### Issue 2: BE Stop Price Calculation
- BE stop price calculation used `intent.EntryPrice` (intended entry/breakout level)
- Should use actual fill price from execution journal for more accurate BE stop placement
- "1 tick before breakout point" should use actual fill price, not intended entry price

## Fixes Applied

### Fix 1: Always Compute BE Trigger
**File**: `RobotCore_For_NinjaTrader/StreamStateMachine.cs` and `modules/robot/core/StreamStateMachine.cs`

- Modified `ComputeAndLogProtectiveOrders()` to always compute BE trigger, even if range isn't available
- BE trigger only requires entry price and base target (65% of target), not range
- Stop and target prices still require range, but BE trigger is set immediately
- Added logging when range isn't available but BE trigger is computed

**Key Change**:
```csharp
// Before: Required RangeHigh and RangeLow
if (_intendedDirection == null || !_intendedEntryPrice.HasValue || !RangeHigh.HasValue || !RangeLow.HasValue)
    return;

// After: Only requires direction and entry price for BE trigger
if (_intendedDirection == null || !_intendedEntryPrice.HasValue)
    return;

// BE trigger computed immediately (doesn't need range)
var beTriggerPts = _baseTarget * 0.65m;
var beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;
_intendedBeTrigger = beTriggerPrice; // Set immediately

// Stop/target only computed if range is available
if (!RangeHigh.HasValue || !RangeLow.HasValue)
{
    // Log warning but BE trigger is already set
    return;
}
```

### Fix 2: Use Actual Fill Price for BE Stop
**Files**: 
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs`
- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs`
- `modules/robot/ninjatrader/RobotSimStrategy.cs`

- Modified `GetActiveIntentsForBEMonitoring()` to return actual fill price from execution journal
- Updated `CheckBreakEvenTriggersTickBased()` to use actual fill price when available
- Falls back to intended entry price if actual fill price isn't available

**Key Changes**:
1. `GetActiveIntentsForBEMonitoring()` now returns `(intentId, intent, beTriggerPrice, entryPrice, actualFillPrice, direction)`
2. BE stop calculation uses `actualFillPrice ?? entryPrice` instead of just `entryPrice`
3. Added logging to show which price is used for BE stop calculation

### Fix 3: Enhanced Logging
**Files**: `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` and `modules/robot/core/Execution/NinjaTraderSimAdapter.cs`

- Added `be_trigger` and `has_be_trigger` fields to `INTENT_REGISTERED` event logging
- This allows verification that BE trigger is set when intents are registered

## Testing Checklist

- [ ] Verify intents are registered with BE trigger price set
- [ ] Verify BE trigger detection works when price reaches 65% of target
- [ ] Verify BE stop is modified to 1 tick before actual fill price (not intended entry)
- [ ] Verify BE stop modification works for both long and short positions
- [ ] Verify BE detection works even if range isn't available when entry is detected
- [ ] Check logs for `INTENT_REGISTERED` events showing `has_be_trigger: true`
- [ ] Check logs for `BE_TRIGGER_REACHED` events showing correct BE stop price

## Expected Behavior After Fix

1. **BE Trigger Always Set**: All intents will have `BeTrigger` price set, even if range isn't available
2. **BE Detection Works**: Break-even triggers will be detected when price reaches 65% of target
3. **Accurate BE Stop**: BE stop will be placed 1 tick before actual fill price (accounting for slippage)
4. **Better Logging**: Logs will show whether BE trigger is set and which price is used for BE stop

## Files Modified

1. `RobotCore_For_NinjaTrader/StreamStateMachine.cs` - Always compute BE trigger
2. `modules/robot/core/StreamStateMachine.cs` - Always compute BE trigger
3. `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` - Return actual fill price, enhanced logging
4. `modules/robot/core/Execution/NinjaTraderSimAdapter.cs` - Return actual fill price, enhanced logging
5. `modules/robot/ninjatrader/RobotSimStrategy.cs` - Use actual fill price for BE stop calculation
