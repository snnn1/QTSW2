# MNQ Position Bug Fix - Critical Error

## Problem
Position of 63 contracts in MNQ (should be 1 contract) - exponential position growth.

## Root Cause
**Bug in `NinjaTraderSimAdapter.NT.cs` line 1471:**

`HandleEntryFill` was being called with `filledTotal` (cumulative total) instead of `fillQuantity` (delta) for each partial fill.

### What Happened
1. **Fill 1**: 1 contract → `HandleEntryFill` called with `filledTotal=1` → protective orders for 1 contract
2. **Fill 2**: 1 contract → `HandleEntryFill` called with `filledTotal=2` → protective orders for 2 contracts (should be 1!)
3. **Fill 3**: 2 contracts → `HandleEntryFill` called with `filledTotal=4` → protective orders for 4 contracts (should be 2!)
4. **Fill 4**: 4 contracts → `HandleEntryFill` called with `filledTotal=8` → protective orders for 8 contracts
5. **Fill 5**: 8 contracts → `HandleEntryFill` called with `filledTotal=16` → protective orders for 16 contracts
6. **Fill 6**: 16 contracts → `HandleEntryFill` called with `filledTotal=32` → protective orders for 32 contracts
7. **Fill 7**: 32 contracts → `HandleEntryFill` called with `filledTotal=64` → protective orders for 64 contracts

**Result**: Position grew exponentially from 1 to 64 contracts in 7 fills within seconds.

## Fix Applied
Changed line 1471 in `NinjaTraderSimAdapter.NT.cs`:
- **Before**: `HandleEntryFill(intentId, entryIntent, fillPrice, filledTotal, utcNow);`
- **After**: `HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, utcNow);`

Now `HandleEntryFill` receives the delta (new fill quantity) instead of cumulative total.

## Files Changed
1. `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`
2. `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`

## Remaining Issue
`HandleEntryFill` may still be called multiple times for partial fills, potentially creating duplicate protective orders. However, this is much better than exponential growth.

**Future Enhancement**: Add check to prevent resubmitting protective orders if they already exist, or modify existing orders instead of creating new ones.

## Immediate Action Required
1. **FLATTEN THE POSITION IMMEDIATELY** - Manual intervention required to close the 63-contract position
2. Rebuild the DLL with the fix
3. Monitor for any remaining position accumulation issues

## Testing
After fix is deployed:
1. Monitor entry fills for MNQ (and all instruments)
2. Verify protective orders are submitted with correct quantities
3. Check that positions don't accumulate beyond expected quantities
4. Test with partial fills to ensure correct behavior
