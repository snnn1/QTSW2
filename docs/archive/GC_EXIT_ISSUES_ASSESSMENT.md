# GC Exit Issues Assessment - 2026-02-02

## Summary
GC2 stream had critical exit issues: protective orders failed during retry, and flatten operation failed with null reference exception.

## Timeline

### GC2 Stream (IntentId: 790e76c11892a9a8)

1. **15:30:00 CT**: Intent registered
   - Direction: Short
   - Entry price: 4695.0
   - Stop price: 4710.0
   - Target price: 4690.0

2. **15:30:00 CT**: Stop brackets submitted (entry orders)

3. **15:30:11 CT**: Entry filled
   - Fill price: 4690
   - Fill quantity: **4 contracts** (not 2!)
   - Broker order ID: a6e384c6a3c943508a313acfc6f42643

4. **15:30:11 CT**: Protective orders submitted (FIRST ATTEMPT)
   - Stop order: Quantity **2**, Stop price: 4710.0 ✅ SUCCESS
   - Target order: Quantity **2**, Target price: 4690.0 ✅ SUCCESS
   - ⚠️ **QUANTITY MISMATCH**: Entry filled for 4 contracts, but protective orders only for 2!

5. **15:30:16 CT**: Protective orders failed (RETRY ATTEMPTS)
   - Stop order: Quantity **4**, Stop price: 4710.0 ❌ REJECTED ("Stop order rejected")
   - Target order: Quantity **4** ❌ FAILED ("Target order submission failed: 'NinjaTrader.Cbi.Order' does not contain a definition for 'ErrorMessage'")
   - Retry count: 3 attempts, all failed

6. **15:30:16 CT**: Flatten attempted
   - ❌ FAILED: "Object reference not set to an instance of an object"
   - Retry count: 3 attempts, all failed

7. **15:30:16 CT**: Stream stood down
   - Terminal state: FAILED_RUNTIME
   - Commit reason: STREAM_STAND_DOWN

## Root Cause Analysis

### Issue 1: Quantity Mismatch
- **Entry filled**: 4 contracts
- **Protective orders (first attempt)**: 2 contracts
- **Problem**: Protective orders were sized to expected quantity (2) instead of actual fill quantity (4)
- **Impact**: Position was only partially protected (2 contracts protected, 2 contracts unprotected)

### Issue 2: Protective Order Retry Failure
- **Stop order rejection**: "Stop order rejected" (reason not detailed)
- **Target order failure**: Code tried to access `Order.ErrorMessage` property which doesn't exist in NinjaTrader API
- **Impact**: Retry attempts failed, leaving position unprotected

### Issue 3: Flatten Null Reference Exception
- **Error**: "Object reference not set to an instance of an object"
- **Location**: `FlattenIntentReal` method
- **Impact**: Position could not be flattened, leaving it open

## Execution Journal Status

```json
{
  "IntentId": "790e76c11892a9a8",
  "EntryFilled": true,
  "FillQuantity": 4,
  "EntryFilledQuantityTotal": 4,
  "ExitFilledQuantityTotal": 0,  // ⚠️ NO EXIT FILLS
  "TradeCompleted": false
}
```

## Critical Issues

1. **Quantity Mismatch**: Entry filled for 4 contracts but protective orders initially submitted for 2
2. **Protective Order Retry Failure**: Stop rejected, target failed due to ErrorMessage property access
3. **Flatten Failure**: Null reference exception prevented position closure
4. **Unprotected Position**: Position remained open with no protective orders

## Questions to Investigate

1. **Why did entry fill for 4 contracts instead of 2?**
   - Was this an overfill?
   - Were there multiple partial fills?
   - Check execution logs for fill details

2. **Why did protective orders fail during retry?**
   - Why was stop order rejected?
   - Why does code try to access `Order.ErrorMessage`?
   - Check NinjaTrader API for correct error property

3. **What causes the null reference in FlattenIntentReal?**
   - Check `GetPosition` call
   - Check account/instrument null checks
   - Check position access

4. **Why wasn't position closed?**
   - Flatten failed, so position remained open
   - Need to verify if position was manually closed

## Next Steps

1. ✅ Fix `Order.ErrorMessage` property access (use correct NinjaTrader API property)
2. ✅ Fix null reference in `FlattenIntentReal` method
3. ✅ Investigate quantity mismatch (why 4 instead of 2) - **ROOT CAUSE IDENTIFIED**
4. Add better error handling for flatten failures
5. Add monitoring for unprotected positions

## Quantity Mismatch Root Cause

### Finding: Multiple Partial Fills
The execution journal shows:
- **EntryAvgFillPrice**: 4692.35 (not 4690)
- **EntryFillNotional**: 18769.4
- **FillQuantity**: 4 contracts total

This indicates **multiple partial fills** at different prices that averaged to 4692.35.

### Root Cause Analysis
1. **First Fill**: 2 contracts → `filledTotal = 2` → `HandleEntryFill` called with `fillQuantity = 2` → Protective orders submitted for **2 contracts** ✅
2. **Second Fill**: 2 more contracts → `filledTotal = 4` → `HandleEntryFill` called again with `fillQuantity = 4` → Protective order retry logic attempted to submit for **4 contracts** ❌

### Problem: HandleEntryFill Not Fully Idempotent
- `HandleEntryFill` is called **every time** there's a fill update
- It doesn't check if protective orders already exist and need updating
- Instead, it always attempts to submit new protective orders
- When retry logic kicks in (because protective orders already exist), it tries to submit for the new cumulative quantity (4) instead of updating existing orders (2) to match

### Impact
- Position was only **partially protected** (2 contracts protected, 2 contracts unprotected)
- Retry attempts failed because they tried to submit duplicate orders
- Position remained open and unprotected

### Solution Needed
`HandleEntryFill` should:
1. Check if protective orders already exist for this intent
2. If they exist, **update** their quantity to match `filledTotal` instead of submitting new orders
3. Only submit new protective orders if none exist

## Fixes Applied

### Fix 1: Null Reference in FlattenIntentReal
- **Issue**: Line 2680 accessed `position.Quantity` without null check
- **Fix**: Added null check before accessing `position.Quantity`
- **Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:2675-2682`

### Fix 2: ErrorMessage Property Access
- **Issue**: Code tried to access `Order.ErrorMessage` which doesn't exist in NinjaTrader API
- **Fix**: Removed `ErrorMessage` access, use only `Error` property with proper exception handling
- **Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:2258-2266`
