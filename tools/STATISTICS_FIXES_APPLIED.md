# Statistics Fixes Applied

## ✅ Fixed Issues

### 1. **Profit per Week** - FIXED
- **Problem:** Was calculating average of "week of month" profits (Week 1-5 of each month)
- **Fix:** Changed to `profitPerDay * 5` (trading days per week)
- **File:** `matrix_timetable_app/frontend/src/matrixWorker.js` line 525

### 2. **Profit per Month** - FIXED
- **Problem:** Was averaging monthly profits, which can be skewed
- **Fix:** Changed to `profitPerDay * 21` (average trading days per month)
- **File:** `matrix_timetable_app/frontend/src/matrixWorker.js` line 527-548

### 3. **Profit per Year** - ALREADY CORRECT
- **Status:** Already using `profitPerDay * 252` (correct)

## ⚠️ Remaining Issues to Investigate

### 1. **Total Trades Mismatch**
- **Displayed:** 11,075
- **Calculated:** 24,374
- **Possible Cause:** Data filtering in dashboard vs. all data in diagnostic
- **Action Needed:** Check if filters are being applied when they shouldn't be

### 2. **Total Profit Mismatch**
- **Displayed:** $758,480
- **Calculated:** $651,852
- **Possible Cause:** Different dataset (filtered vs. all) or contract multiplier issue
- **Action Needed:** Verify contract multiplier and data filtering

### 3. **Median PnL per Trade**
- **Displayed:** $150
- **Calculated:** $-2
- **Possible Cause:** Calculation error or wrong data subset
- **Action Needed:** Verify median calculation is using correct data

### 4. **Max Drawdown**
- **Displayed:** $15,713
- **Calculated:** $34,808
- **Possible Cause:** Different calculation method or filtered data
- **Action Needed:** Compare drawdown calculation methods

### 5. **Sharpe Ratio**
- **Displayed:** 4.13
- **Calculated:** 2.29
- **Possible Cause:** Different volatility calculation or data subset
- **Action Needed:** Verify daily returns calculation

### 6. **Calmar Ratio**
- **Displayed:** 5.97
- **Calculated:** 2.31
- **Possible Cause:** Depends on max drawdown and annual return - will be wrong if drawdown is wrong
- **Action Needed:** Fix max drawdown first

## Next Steps

1. **Run diagnostic again** after fixes to see updated values
2. **Check data filtering** - verify if dashboard is using filtered data
3. **Compare median calculation** - ensure it's using all trades, not filtered
4. **Review drawdown calculation** - verify it matches diagnostic method
5. **Check contract multiplier** - ensure it's 1.0 for master stream

## Testing

Run the diagnostic script to verify fixes:
```bash
python tools\diagnose_statistics.py
```

Compare the new "Profit per Week" value - it should now be ~$1,594 instead of $151,696.



