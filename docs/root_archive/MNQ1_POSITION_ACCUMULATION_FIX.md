# MNQ1 Position Accumulation Bug - CRITICAL FIX

## CRITICAL ISSUE

**Position**: MNQ1 accumulated to **270 contracts**
**Root Cause**: Protective orders were using `fillQuantity` (delta) instead of `totalFilledQuantity` (cumulative)

## The Bug

### Problem
When incremental fills occur (multiple partial fills for the same intent):
- Fill 1: `fillQuantity=1`, `filledTotal=1` → HandleEntryFill submits protective orders for quantity=1 ✓
- Fill 2: `fillQuantity=1`, `filledTotal=2` → HandleEntryFill submits protective orders for quantity=1 ✗
- Fill 3: `fillQuantity=1`, `filledTotal=3` → HandleEntryFill submits protective orders for quantity=1 ✗
- ...
- Fill 270: `fillQuantity=1`, `filledTotal=270` → HandleEntryFill submits protective orders for quantity=1 ✗

**Result**: Protective orders only cover 1 contract, but position is 270 contracts → **UNPROTECTED POSITION**

### Why This Happened
The code comment said "HandleEntryFill submits protective orders for the NEW fill quantity only", but this is **WRONG** for incremental fills. Protective orders must cover the **ENTIRE position**, not just the delta.

## The Fix

### Changes Made

1. **Updated HandleEntryFill Signature**:
   ```csharp
   // Before:
   private void HandleEntryFill(string intentId, Intent intent, decimal fillPrice, int fillQuantity, DateTimeOffset utcNow)
   
   // After:
   private void HandleEntryFill(string intentId, Intent intent, decimal fillPrice, int fillQuantity, int totalFilledQuantity, DateTimeOffset utcNow)
   ```

2. **Updated Call Site**:
   ```csharp
   // Before:
   HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, utcNow);
   
   // After:
   HandleEntryFill(intentId, entryIntent, fillPrice, fillQuantity, filledTotal, utcNow);
   ```

3. **Updated Protective Order Submission**:
   ```csharp
   // Before:
   SubmitProtectiveStop(..., fillQuantity, ...);  // Only covers delta
   SubmitTargetOrder(..., fillQuantity, ...);     // Only covers delta
   
   // After:
   SubmitProtectiveStop(..., totalFilledQuantity, ...);  // Covers entire position
   SubmitTargetOrder(..., totalFilledQuantity, ...);    // Covers entire position
   ```

### Files Modified
- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs`
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs`
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`

## How It Works Now

### Correct Behavior
- Fill 1: `fillQuantity=1`, `filledTotal=1` → HandleEntryFill submits protective orders for quantity=1 ✓
- Fill 2: `fillQuantity=1`, `filledTotal=2` → HandleEntryFill submits protective orders for quantity=2 ✓ (updated to cover total)
- Fill 3: `fillQuantity=1`, `filledTotal=3` → HandleEntryFill submits protective orders for quantity=3 ✓ (updated to cover total)

**Result**: Protective orders always cover the ENTIRE position, preventing accumulation.

### Protective Order Update Logic
When `SubmitProtectiveStop` is called with a new quantity:
- If existing stop has `quantity=1` and new quantity is `quantity=2`:
  - `quantityChanged = true` → Cancel and recreate with quantity=2 ✓
- This ensures protective orders always match the total position

## Immediate Actions Required

1. **Rebuild DLL**: Fix is in code, needs to be built
2. **Copy DLL**: Deploy to NinjaTrader
3. **Restart NinjaTrader**: Load new DLL
4. **Check MNQ1 Position**: Verify current position and flatten if needed
5. **Monitor**: Watch for any further accumulation

## Status

✅ **FIXED**: Code updated to use `totalFilledQuantity` for protective orders
⏳ **PENDING**: DLL rebuild and deployment
⚠️ **ACTION REQUIRED**: Check and flatten MNQ1 position if needed
