# YM Trade 02/01/2026 Fix Verification

## Issue
YM trade on **02/01/2026 at 09:00** is still showing **TIME** exit instead of **BE**, even though break-even stop loss was hit.

## Fixes Applied

### Fix #1: Break-Even Stop Loss Detection (Previously Applied)
- **Location**: `modules/analyzer/logic/price_tracking_logic.py` lines 482-558
- **Change**: Added check before time expiry to verify if break-even stop loss was hit
- **Improvement**: Now checks bars **at or before** expiry time (includes current bar)

### Fix #2: Target Exit Price (Just Applied)
- **Location**: `modules/analyzer/logic/price_tracking_logic.py` lines 371-417
- **Change**: Fixed exit price and profit calculation for target hits

## Why Trade Still Shows TIME

The fixes are in the code, but **the analyzer needs to be re-run** to pick up the fixes. The matrix is showing old results from before the fixes were applied.

## Steps to Fix

### Step 1: Re-run Analyzer for YM
The analyzer needs to process YM data again with the fixed code:

```bash
# Option A: Run analyzer for YM only
python modules/analyzer/scripts/run_data_processed.py \
    --folder data/data_processed \
    --instrument YM \
    --sessions S1 S2 \
    --slots S1:07:30 S1:08:00 S1:09:00 S2:09:30 S2:10:00 S2:10:30 S2:11:00 \
    --days Mon Tue Wed Thu Fri

# Option B: Use the parallel analyzer script
python ops/maintenance/run_analyzer_parallel.py --instruments YM
```

### Step 2: Update/Rebuild Matrix
After analyzer completes, update the matrix to pick up the corrected results:

**Via UI:**
- Click "Update Matrix (Rolling 35-Day Window)" button

**Via API:**
```bash
# Update matrix (rolling window)
curl -X POST http://localhost:8000/api/matrix/update \
  -H "Content-Type: application/json" \
  -d '{"analyzer_runs_dir": "data/analyzed"}'
```

**Via Python:**
```python
from modules.matrix.master_matrix import MasterMatrix
matrix = MasterMatrix(analyzer_runs_dir="data/analyzed")
matrix.update_master_matrix(output_dir="data/master_matrix")
```

### Step 3: Verify Fix
After updating the matrix, check the YM trade:
- Date: 02/01/2026
- Time: 09:00
- Stream: YM1
- Result should now be **BE** instead of **TIME**

## Diagnostic Script

Run the diagnostic script to verify the fix is working:

```bash
python diagnose_ym_trade_020126_verification.py
```

This will:
1. Load the YM data
2. Find the trade in analyzer output
3. Check if break-even stop was hit
4. Re-run trade execution with the fix
5. Show if the result changes from TIME to BE

## Expected Behavior After Fix

If break-even stop loss was hit before time expiry:
- **Exit Reason**: BE (not TIME)
- **Exit Time**: When break-even stop was actually hit (not expiry time)
- **Exit Price**: Break-even stop loss level (entry_price - tick_size)
- **Profit**: Should be 0.0 (or very close to 0 for BE)

## If Trade Still Shows TIME After Re-run

If the trade still shows TIME after re-running the analyzer, possible reasons:

1. **Break-even stop was NOT actually hit**: Price may have gotten close but didn't actually hit the break-even stop level
2. **T1 was NOT triggered**: If T1 threshold wasn't reached, break-even stop wouldn't be set
3. **Data issue**: Missing bars or incorrect price data

Run the diagnostic script to investigate further.

## Code Changes Summary

### Change 1: Break-Even Stop Detection (Line 488)
```python
# OLD: Only checked bars before expiry
bars_before_expiry = after[after["timestamp"] < expiry_time]

# NEW: Checks bars at or before expiry (includes current bar)
bars_at_or_before_expiry = after[after["timestamp"] <= expiry_time]
```

### Change 2: Target Exit Price (Lines 371-417)
```python
# OLD: Used current_stop_loss for target exits
profit = self.calculate_profit(entry_price, current_stop_loss, ...)
return self._create_trade_execution(current_stop_loss, ...)

# NEW: Uses target_exit_price (from execution_result or target_level)
target_exit_price = execution_result["exit_price"] if execution_result else target_level
profit = self.calculate_profit(entry_price, target_exit_price, ...)
return self._create_trade_execution(target_exit_price, ...)
```

## Next Steps

1. ✅ Code fixes are in place
2. ⏳ **Re-run analyzer for YM** (required)
3. ⏳ **Update matrix** (required)
4. ⏳ **Verify trade shows BE** (verification)
