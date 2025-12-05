# Statistics Comparison Report

## Major Discrepancies Found

### Core Performance Metrics

| Metric | Displayed | Calculated | Difference | Status |
|--------|-----------|------------|------------|--------|
| **Total Profit ($)** | $758,480 | $651,852 | -$106,628 | ❌ **WRONG** |
| **Total Trades** | 11,075 | 24,374 | +13,299 | ❌ **WRONG** |
| **Total Days** | 2,037 | 2,045 | +8 | ⚠️ Minor |
| **Avg Trades/Day** | 5.44 | 11.92 | +6.48 | ❌ **WRONG** |
| **Profit per Day** | $372 | $319 | -$53 | ⚠️ Different |
| **Profit per Week** | $151,696 | $1,594 | -$150,102 | ❌ **WRONG** |
| **Profit per Month** | $7,901 | $6,694 | -$1,207 | ⚠️ Different |
| **Profit per Year** | $94,810 | $80,326 | -$14,484 | ⚠️ Different |
| **Profit per Trade** | $68 | $29 | -$39 | ❌ **WRONG** |

### Win/Loss Statistics

| Metric | Displayed | Calculated | Difference | Status |
|--------|-----------|------------|------------|--------|
| **Win Rate** | 71.6% | 68.3% | -3.3% | ⚠️ Different |
| **Wins** | 5,323 | 10,280 | +4,957 | ❌ **WRONG** |
| **Losses** | 2,107 | 4,777 | +2,670 | ❌ **WRONG** |
| **Break-Even** | 2,783 | 5,624 | +2,841 | ❌ **WRONG** |

### Risk-Adjusted Performance

| Metric | Displayed | Calculated | Difference | Status |
|--------|-----------|------------|------------|--------|
| **Sharpe Ratio** | 4.13 | 2.29 | -1.84 | ❌ **WRONG** |
| **Sortino Ratio** | 4.23 | 3.72 | -0.51 | ⚠️ Different |
| **Calmar Ratio** | 5.97 | 2.31 | -3.66 | ❌ **WRONG** |
| **Profit Factor** | 1.38 | 1.22 | -0.16 | ⚠️ Different |
| **Risk-Reward** | 0.55 | 0.56 | +0.01 | ✅ OK |

### Drawdowns & Stability

| Metric | Displayed | Calculated | Difference | Status |
|--------|-----------|------------|------------|--------|
| **Max Drawdown ($)** | $15,713 | $34,808 | +$19,095 | ❌ **WRONG** |
| **Time-to-Recovery** | 102 days | 196 days | +94 days | ❌ **WRONG** |
| **Max Consecutive Losses** | 16 | 23 | +7 | ❌ **WRONG** |
| **Monthly Return Std Dev** | $6,535 | $9,401 | +$2,866 | ⚠️ Different |

### PnL Distribution

| Metric | Displayed | Calculated | Difference | Status |
|--------|-----------|------------|------------|--------|
| **Median PnL per Trade** | $150 | $-2 | -$152 | ❌ **WRONG** |
| **Std Dev of PnL** | $544 | $572 | +$28 | ✅ OK |
| **95% VaR** | -$1,188 | -$1,310 | -$122 | ⚠️ Different |
| **Expected Shortfall** | -$1,441 | -$1,479 | -$38 | ✅ OK |
| **Skewness** | -1.319 | -1.197 | +0.122 | ✅ OK |
| **Kurtosis** | 1.048 | 0.576 | -0.472 | ⚠️ Different |

## Critical Issues Identified

### 1. **Profit per Week Calculation is WRONG**
- **Displayed:** $151,696 (this is clearly wrong - should be ~$1,594)
- **Calculated:** $1,594
- **Issue:** Likely multiplying by wrong factor (maybe 95 instead of 5)

### 2. **Total Trades Mismatch**
- **Displayed:** 11,075 trades
- **Calculated:** 24,374 trades
- **Issue:** Dashboard is likely filtering data incorrectly or using a subset

### 3. **Total Profit Mismatch**
- **Displayed:** $758,480
- **Calculated:** $651,852
- **Issue:** Different data set or calculation method

### 4. **Median PnL is WRONG**
- **Displayed:** $150
- **Calculated:** $-2
- **Issue:** Calculation error in median calculation

### 5. **Max Drawdown is WRONG**
- **Displayed:** $15,713
- **Calculated:** $34,808
- **Issue:** Different calculation method or data filtering

## Recommendations

1. **Check data filtering** - Dashboard may be filtering trades incorrectly
2. **Verify Profit per Week calculation** - Check if it's multiplying by wrong factor
3. **Review median calculation** - Median should be negative, not positive
4. **Check contract multiplier** - Ensure it's being applied correctly
5. **Verify date parsing** - Ensure all dates are being parsed correctly for day calculations

## Next Steps

1. Compare the data being used in the dashboard vs. the diagnostic
2. Check if filters are being applied in the dashboard
3. Review the calculation formulas in `statsCalculations.js`
4. Verify contract multiplier settings



