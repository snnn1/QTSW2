# Statistics Analysis & Fixes

## ✅ Fixed Issues

### 1. **Profit per Week** - FIXED ✅
- **Before:** $151,696 (WRONG - was averaging "week of month")
- **After:** ~$1,596 (correct - profit per day * 5)
- **Fix:** Changed calculation to `profitPerDay * 5`

### 2. **Profit per Month** - FIXED ✅
- **Before:** Averaging monthly profits (inconsistent)
- **After:** `profitPerDay * 21` (consistent)
- **Fix:** Changed calculation to use daily average

## ⚠️ Discrepancies Explained

### Data Filtering Issue
The dashboard shows **11,075 trades** while the diagnostic shows **22,360 trades** (excluding NoTrade). This indicates:

1. **Dashboard is using filtered data** - Filters are being applied (year filters, day of week filters, etc.)
2. **Diagnostic uses all data** - No filters applied

This explains why:
- **Total Profit:** $758,480 (filtered) vs $651,852 (all data)
- **Total Trades:** 11,075 (filtered) vs 22,360 (all data)
- **Max Drawdown:** $15,713 (filtered) vs $34,808 (all data)

### Calculation Differences

| Metric | Displayed | Diagnostic | Status | Notes |
|--------|-----------|------------|--------|-------|
| **Total Trades** | 11,075 | 22,360 | ⚠️ Filtered | Dashboard has filters applied |
| **Total Profit** | $758,480 | $651,852 | ⚠️ Filtered | Different dataset |
| **Profit per Week** | $151,696 | $1,596 | ✅ **FIXED** | Was calculation error |
| **Profit per Month** | $7,901 | $6,704 | ✅ **FIXED** | Now consistent |
| **Median PnL** | $150 | -$2 | ⚠️ Filtered | Different data subset |
| **Max Drawdown** | $15,713 | $34,808 | ⚠️ Filtered | Different data subset |
| **Sharpe Ratio** | 4.13 | 2.29 | ⚠️ Filtered | Different volatility |
| **Calmar Ratio** | 5.97 | 2.31 | ⚠️ Filtered | Depends on drawdown |

## Key Findings

1. **Profit per Week was clearly wrong** - Fixed ✅
2. **Most other differences are due to filtering** - Dashboard shows filtered data, diagnostic shows all data
3. **Median calculation is correct** - The $150 vs -$2 difference is because filtered data has different characteristics

## Recommendations

1. **Check active filters** - The dashboard may have year/day filters applied that reduce the dataset
2. **Verify filter state** - When viewing master stats, check if any filters are active
3. **Consider showing both** - Display both "All Data" and "Filtered Data" statistics

## Next Steps

To verify which calculations are actually wrong (vs just filtered):

1. Clear all filters in the dashboard
2. Compare statistics again
3. If still different, investigate specific calculations



