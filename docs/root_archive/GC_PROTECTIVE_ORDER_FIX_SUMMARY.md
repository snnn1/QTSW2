# GC Protective Order Quantity Mismatch Fix

## Problem
When entry orders filled in multiple partial fills, protective orders were initially submitted for the first partial fill quantity (2 contracts) instead of the cumulative filled quantity (4 contracts). When the second partial fill arrived, attempts to update the existing protective orders failed, leaving the position partially unprotected.

## Root Cause
1. **Multiple Partial Fills**: Entry order filled in two partial fills (2 contracts each)
2. **First Fill**: `HandleEntryFill` called with `fillQuantity = 2` → Protective orders submitted for 2 contracts ✅
3. **Second Fill**: `HandleEntryFill` called again with `fillQuantity = 4` → Attempted to update existing orders from 2 to 4 ❌
4. **Update Failure**: NinjaTrader may not allow quantity changes on working orders, causing update attempts to fail
5. **Retry Logic**: Retry logic tried to create new orders for 4 contracts, but they were rejected (duplicate/OCO conflict)

## Solution Implemented

### 1. Cancel and Recreate Strategy
When quantity changes are detected on existing protective orders:
- **Cancel** existing protective orders (both stop and target since they're OCO paired)
- **Recreate** them with the correct cumulative quantity

### 2. Helper Method: `CancelProtectiveOrdersForIntent`
Added a new helper method that:
- Finds protective orders (stop and target) for a specific intent
- Cancels them using NinjaTrader's `Account.Cancel()` API
- Logs the cancellation for audit purposes

### 3. Enhanced Update Logic
Modified `SubmitProtectiveStopReal` and `SubmitTargetOrderReal` to:
- **Detect quantity changes** separately from price changes
- **Cancel and recreate** when quantity changes (instead of trying to update)
- **Update in place** only when price changes (quantity unchanged)
- **Handle update failures** by canceling and recreating

### 4. Better Error Handling
- When order change fails, cancel existing orders and recreate
- When order change is rejected, cancel existing orders and recreate
- Added comprehensive logging for all cancellation/recreation scenarios

## Code Changes

### Files Modified
1. `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
2. `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` (synced)

### Key Changes

#### 1. New Helper Method
```csharp
private void CancelProtectiveOrdersForIntent(string intentId, DateTimeOffset utcNow)
```
- Cancels both stop and target orders for an intent
- Used when quantity changes require cancel/recreate

#### 2. Quantity Change Detection
```csharp
var quantityChanged = existingStop.Quantity != quantity;
var priceChanged = Math.Abs(existingStop.StopPrice - stopPriceD) > 1e-10;

if (quantityChanged)
{
    // Cancel and recreate
    CancelProtectiveOrdersForIntent(intentId, utcNow);
    existingStop = null; // Fall through to create new order
}
else if (priceChanged)
{
    // Try to update in place
    // ... update logic
}
```

#### 3. Update Failure Handling
When order change fails or is rejected:
- Cancel existing orders
- Clear `existingStop`/`existingTarget` to fall through to create new orders

## Expected Behavior After Fix

### Scenario: Multiple Partial Fills
1. **First Fill (2 contracts)**:
   - `HandleEntryFill` called with `fillQuantity = 2`
   - No existing protective orders → Create new orders for 2 contracts ✅

2. **Second Fill (2 more contracts, total 4)**:
   - `HandleEntryFill` called with `fillQuantity = 4`
   - Existing protective orders found with quantity 2
   - **Quantity changed detected** → Cancel existing orders
   - Create new orders for 4 contracts ✅
   - Position fully protected ✅

### Scenario: Price-Only Changes
- If only price changes (quantity unchanged), update in place
- No cancellation needed

## Testing Recommendations

1. **Test Multiple Partial Fills**:
   - Submit entry order for 4 contracts
   - Simulate partial fill of 2 contracts
   - Verify protective orders created for 2
   - Simulate second partial fill of 2 contracts
   - Verify existing orders canceled and recreated for 4

2. **Test Price Updates**:
   - Verify price-only changes update in place (no cancellation)

3. **Test Update Failures**:
   - Simulate order change failure
   - Verify orders are canceled and recreated

## Related Issues Fixed
- ✅ GC quantity mismatch (4 contracts filled, 2 contracts protected)
- ✅ Protective order retry failures
- ✅ Unprotected positions after partial fills
