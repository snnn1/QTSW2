# GC Quantity Mismatch Investigation

## Problem
GC2 entry filled for **4 contracts**, but protective orders were initially submitted for **2 contracts**.

## Timeline Analysis

### Entry Fill
- **Time**: 15:30:11 CT
- **Fill Quantity**: 4 contracts (from execution journal)
- **Fill Price**: 4690
- **Broker Order ID**: a6e384c6a3c943508a313acfc6f42643

### Protective Orders (First Attempt)
- **Time**: 15:30:11 CT (same timestamp as entry fill)
- **Stop Order Quantity**: 2 contracts
- **Target Order Quantity**: 2 contracts
- **Status**: ✅ Both submitted successfully

### Protective Orders (Retry Attempts)
- **Time**: 15:30:16 CT (5 seconds later)
- **Stop Order Quantity**: 4 contracts ❌ REJECTED
- **Target Order Quantity**: 4 contracts ❌ FAILED
- **Retry Count**: 3 attempts, all failed

## Code Flow Analysis

### HandleExecutionUpdateReal Flow
1. **Line 1374**: `orderInfo.FilledQuantity += fillQuantity;`
   - Tracks cumulative fills
2. **Line 1375**: `var filledTotal = orderInfo.FilledQuantity;`
   - Gets cumulative total
3. **Line 1472**: `HandleEntryFill(intentId, entryIntent, fillPrice, filledTotal, utcNow);`
   - **CRITICAL**: Passes `filledTotal` (cumulative) to `HandleEntryFill`

### HandleEntryFill Flow
1. **Line 534**: `fillQuantity` parameter used for protective orders
2. **Line 529-536**: `SubmitProtectiveStop(..., fillQuantity, ...)`
3. **Line 541-548**: `SubmitTargetOrder(..., fillQuantity, ...)`

## Hypothesis

### Hypothesis 1: Multiple Partial Fills
The entry order may have filled in multiple partial fills:
- **First fill**: 2 contracts → `filledTotal = 2` → Protective orders submitted for 2
- **Second fill**: 2 contracts → `filledTotal = 4` → But protective orders already submitted

**Problem**: If there are multiple execution updates, `HandleEntryFill` is called multiple times, but protective orders are only placed once (or should be idempotent).

### Hypothesis 2: Single 4-Contract Fill
The entry order filled for 4 contracts in one execution update:
- **Fill**: 4 contracts → `filledTotal = 4` → But protective orders submitted for 2

**Problem**: This suggests `filledTotal` was incorrectly calculated as 2 when it should have been 4.

### Hypothesis 3: Expected Quantity Mismatch
The `orderInfo.ExpectedQuantity` or `orderInfo.Quantity` may have been 2, causing protective orders to be sized incorrectly.

**Problem**: Protective orders should be sized to actual fill quantity, not expected quantity.

## Key Questions

1. **Were there multiple execution updates?**
   - Check logs for multiple `EXECUTION_UPDATE` or `INTENT_FILL_UPDATE` events
   - Check if `orderInfo.FilledQuantity` was incremented multiple times

2. **What was the order's expected quantity?**
   - Check `orderInfo.Quantity` and `orderInfo.ExpectedQuantity`
   - Check if order was submitted for 2 contracts but filled for 4

3. **Is HandleEntryFill idempotent?**
   - Does it check if protective orders already exist before submitting?
   - Or does it always submit new orders?

4. **Why did retry use quantity 4?**
   - Retry attempts used quantity 4, suggesting `filledTotal` was correctly 4 at retry time
   - But initial submission used quantity 2

## Next Steps

1. Check execution logs for multiple fill events
2. Check order submission logs for expected quantity
3. Review HandleEntryFill idempotency logic
4. Check if there's a race condition between fills and protective order submission
