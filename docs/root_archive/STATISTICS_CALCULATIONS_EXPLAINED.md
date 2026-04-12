# Statistics Calculations: Sharpe Ratio, Sortino Ratio, and Time to Recovery

## Overview

These three metrics are calculated on the **statistics page** using **daily PnL series** (not per-trade). All calculations use **executed trades only** (WIN, LOSS, BE, BREAKEVEN, TIME) and exclude NoTrade results.

**Data Source**: Daily aggregated PnL (sum of all trades per trading day)

---

## 1. Sharpe Ratio

### Formula
```
Sharpe Ratio = Annualized Return / Annualized Volatility
```

### Step-by-Step Calculation

**Backend (Python) - `modules/matrix/statistics.py` lines 917-925:**

1. **Group trades by trading day**:
   ```python
   daily_df["trade_date_only"] = daily_df["trade_date"].dt.date
   daily_pnl = daily_df.groupby("trade_date_only", sort=False)[profit_col].sum()
   # Result: One PnL value per trading day
   ```

2. **Calculate daily returns**:
   ```python
   daily_returns = daily_pnl["pnl"].values  # Array of daily PnL values
   mean_daily_return = np.mean(daily_returns)  # Average daily PnL
   std_daily_return = np.std(daily_returns)  # Standard deviation of daily PnL
   ```

3. **Annualize**:
   ```python
   trading_days_per_year = 252
   annualized_return = mean_daily_return * trading_days_per_year
   annualized_volatility = std_daily_return * np.sqrt(trading_days_per_year)
   ```

4. **Calculate Sharpe**:
   ```python
   sharpe_ratio = annualized_return / annualized_volatility if annualized_volatility > 0 else 0.0
   ```

**Frontend (JavaScript) - `modules/matrix_timetable_app/frontend/src/matrixWorker.js` lines 772-783:**

1. **Group trades by trading day**:
   ```javascript
   const dailyProfits = new Map() // date string -> array of profit dollars
   // Sum all trades per day
   dailyPnL.push(profits.reduce((a, b) => a + b, 0))
   ```

2. **Calculate mean and variance**:
   ```javascript
   const meanDailyReturn = dailyPnL.reduce((a, b) => a + b, 0) / dailyPnL.length
   const variance = dailyPnL.reduce((sum, r) => 
     sum + Math.pow(r - meanDailyReturn, 2), 0) / (dailyPnL.length - 1)
   const stdDailyReturn = Math.sqrt(variance)
   ```

3. **Annualize**:
   ```javascript
   const tradingDaysPerYear = 252
   const annualizedReturn = meanDailyReturn * tradingDaysPerYear
   const annualizedVolatility = stdDailyReturn * Math.sqrt(tradingDaysPerYear)
   ```

4. **Calculate Sharpe**:
   ```javascript
   const sharpeRatio = annualizedVolatility > 0 ? annualizedReturn / annualizedVolatility : 0.0
   ```

### Key Points
- **Uses daily PnL series** (not per-trade)
- **Annualized using 252 trading days**
- **Volatility**: Standard deviation of ALL daily returns (both positive and negative)
- **Risk-free rate**: Assumed to be 0 (not subtracted from return)
- **Rounding**: Rounded to 2 decimal places

---

## 2. Sortino Ratio

### Formula
```
Sortino Ratio = Annualized Return / Annualized Downside Volatility
```

### Step-by-Step Calculation

**Backend (Python) - `modules/matrix/statistics.py` lines 927-931:**

1. **Filter downside returns** (only negative daily PnL):
   ```python
   downside_returns = daily_returns[daily_returns < 0]  # Only negative days
   ```

2. **Calculate downside volatility** (standard definition: variance around zero):
   ```python
   # Calculate downside deviation around zero (standard Sortino definition)
   downside_variance = np.mean(downside_returns ** 2) if len(downside_returns) > 0 else 0.0
   downside_std = np.sqrt(downside_variance)
   annualized_downside_vol = downside_std * np.sqrt(trading_days_per_year)
   ```

3. **Calculate Sortino**:
   ```python
   sortino_ratio = annualized_return / annualized_downside_vol if annualized_downside_vol > 0 else 0.0
   ```

**Frontend (JavaScript) - `modules/matrix_timetable_app/frontend/src/matrixWorker.js` lines 785-792:**

1. **Filter downside returns**:
   ```javascript
   const downsideReturns = dailyPnL.filter(r => r < 0)
   ```

2. **Calculate downside variance** (standard definition: variance around zero):
   ```javascript
   // Calculate downside deviation around zero (standard Sortino definition)
   const downsideVariance = downsideReturns.length > 0
     ? downsideReturns.reduce((sum, r) => sum + Math.pow(r, 2), 0) / downsideReturns.length
     : 0
   const downsideStd = Math.sqrt(downsideVariance)
   ```

3. **Annualize**:
   ```javascript
   const annualizedDownsideVol = downsideStd * Math.sqrt(tradingDaysPerYear)
   ```

4. **Calculate Sortino**:
   ```javascript
   const sortinoRatio = annualizedDownsideVol > 0 ? annualizedReturn / annualizedDownsideVol : 0.0
   ```

**Alternative Frontend Implementation - `modules/matrix_timetable_app/frontend/src/utils/statsCalculations.js` lines 288-295:**

**STANDARDIZED**: Uses zero as the target (standard Sortino definition):
```javascript
// Sortino Ratio (standard definition: variance around zero)
const downsideReturnsDollars = dailyReturnsDollars.filter(r => r < 0)
// Calculate downside deviation around zero (standard Sortino definition)
const downsideVarianceDollars = downsideReturnsDollars.length > 0
  ? downsideReturnsDollars.reduce((sum, r) => sum + Math.pow(r, 2), 0) / downsideReturnsDollars.length
  : 0
// Note: Uses r^2 (squared deviation from zero), not (r - mean)^2
```

### Key Points
- **Uses only negative daily returns** (downside volatility)
- **Annualized using 252 trading days**
- **Standard definition**: Variance around zero (not mean)
  - Formula: `downside_variance = mean(min(0, daily_return)^2)`
  - All implementations now standardized to use zero as target
- **Rounding**: Rounded to 2 decimal places

---

## 3. Time to Recovery

### Definition
**Longest number of trading days** it took to recover from a drawdown (return to previous peak equity).

### Step-by-Step Calculation

**Backend (Python) - `modules/matrix/statistics.py` lines 933-963:**

1. **Calculate cumulative PnL and running maximum**:
   ```python
   cumulative_pnl = daily_pnl["pnl"].cumsum()  # Cumulative sum of daily PnL
   running_max = cumulative_pnl.expanding().max()  # Running maximum equity
   drawdown = cumulative_pnl - running_max  # Drawdown from peak
   ```

2. **Track drawdown episodes**:
   ```python
   time_to_recovery_days = 0
   in_drawdown = False
   drawdown_start_idx = None
   
   for idx in range(len(drawdown)):
       if drawdown.iloc[idx] < 0:  # In drawdown
           if not in_drawdown:
               in_drawdown = True
               drawdown_start_idx = idx  # Mark start of drawdown
       else:  # Recovered (back to peak or above)
           if in_drawdown and drawdown_start_idx is not None:
               recovery_days = idx - drawdown_start_idx
               time_to_recovery_days = max(time_to_recovery_days, recovery_days)
               in_drawdown = False
               drawdown_start_idx = None
   ```

3. **Handle ongoing drawdown** (if still in drawdown at end):
   ```python
   if in_drawdown and drawdown_start_idx is not None:
       recovery_days = len(drawdown) - drawdown_start_idx
       time_to_recovery_days = max(time_to_recovery_days, recovery_days)
   ```

**Frontend (JavaScript) - `modules/matrix_timetable_app/frontend/src/matrixWorker.js` lines 794-820:**

1. **Track cumulative PnL and running maximum**:
   ```javascript
   let cumulativePnL = 0
   let runningMax = 0
   let timeToRecoveryDays = 0
   let inDrawdown = false
   let drawdownStartIdx = null
   
   for (let idx = 0; idx < dailyPnL.length; idx++) {
       cumulativePnL += dailyPnL[idx]
       runningMax = Math.max(runningMax, cumulativePnL)
       const drawdown = cumulativePnL - runningMax
   ```

2. **Track drawdown episodes**:
   ```javascript
       if (drawdown < 0) {  // In drawdown
           if (!inDrawdown) {
               inDrawdown = true
               drawdownStartIdx = idx  // Mark start
           }
       } else {  // Recovered
           if (inDrawdown && drawdownStartIdx !== null) {
               const recoveryDays = idx - drawdownStartIdx
               timeToRecoveryDays = Math.max(timeToRecoveryDays, recoveryDays)
               inDrawdown = false
               drawdownStartIdx = null
           }
       }
   }
   ```

**Alternative Frontend Implementation - `modules/matrix_timetable_app/frontend/src/utils/statsCalculations.js` lines 441-508:**

**DIFFERENCE**: Uses actual calendar days between trade dates (not trading day index):

1. **Track running equity and peak**:
   ```javascript
   let runningEquity = 0
   let peakValue = -Infinity
   let peakDate = null
   let inDrawdown = false
   
   sortedByDate.forEach((trade, idx) => {
       runningEquity += profitDollars
       
       if (runningEquity > peakValue) {  // New peak
           if (inDrawdown && peakDate) {
               const daysDiff = Math.floor((tradeDate.getTime() - peakDate.getTime()) / (1000 * 60 * 60 * 24))
               timeToRecoveryDays = Math.max(timeToRecoveryDays, daysDiff)
               inDrawdown = false
           }
           peakValue = runningEquity
           peakDate = tradeDate
       } else if (runningEquity < peakValue) {  // In drawdown
           if (!inDrawdown) {
               inDrawdown = true
           }
       }
   })
   ```

### Key Points
- **Measures longest recovery period** from drawdown start to new peak
- **Backend/Frontend (matrixWorker.js)**: Uses trading day index difference (counts trading days)
- **Frontend (statsCalculations.js)**: Uses calendar days between dates
- **Recovery definition**: Equity returns to or exceeds previous peak
- **Ongoing drawdowns**: Counted if still in drawdown at end of data
- **Units**: Trading days (not calendar days) in most implementations

---

## Data Filtering

All three metrics respect the **`include_filtered_executed`** toggle:

- **If `include_filtered_executed == True`**: Uses ALL executed trades (including filtered)
- **If `include_filtered_executed == False`**: Uses only executed trades where `final_allowed == True`

**Source**: `modules/matrix/statistics.py` lines 7-18

---

## Implementation Differences

### Sharpe Ratio
- **Backend**: Uses pandas `np.std()` (sample standard deviation, N-1)
- **Frontend**: Manual variance calculation with (N-1) denominator
- **Result**: Should be identical

### Sortino Ratio
- **All implementations**: Now standardized to use variance around zero
- **Backend**: Uses `np.mean(downside_returns ** 2)` (variance around zero)
- **Frontend (matrixWorker.js)**: Uses `mean(r^2)` for downside returns (variance around zero)
- **Frontend (statsCalculations.js)**: Uses `mean(r^2)` for downside returns (variance around zero)
- **Result**: All implementations now consistent (standard Sortino definition)

### Time to Recovery
- **Backend**: Trading day index difference
- **Frontend (matrixWorker.js)**: Trading day index difference (matches backend)
- **Frontend (statsCalculations.js)**: Calendar days between dates (may differ)
- **Result**: statsCalculations.js may show different values due to calendar vs trading days

---

## Example Calculation

### Sample Data
```
Day 1: +$100
Day 2: -$50
Day 3: -$30
Day 4: +$40
Day 5: +$60
```

### Cumulative PnL
```
Day 1: $100 (peak)
Day 2: $50 (drawdown starts)
Day 3: $20 (still in drawdown)
Day 4: $60 (recovered - back above $50 but not peak)
Day 5: $120 (new peak - recovered from Day 1 peak)
```

### Time to Recovery
- **Drawdown 1**: Day 2-5 = 3 trading days (Day 2 → Day 5)
- **Result**: 3 days (longest recovery period)

### Sharpe Ratio
```
Mean daily return: ($100 - $50 - $30 + $40 + $60) / 5 = $24
Std dev: sqrt(variance of [100, -50, -30, 40, 60])
Annualized return: $24 * 252 = $6,048
Annualized volatility: std_dev * sqrt(252)
Sharpe = $6,048 / annualized_volatility
```

### Sortino Ratio
```
Downside returns: [-50, -30]
Downside variance: mean([(-50)^2, (-30)^2]) = mean([2500, 900]) = 1700
Downside std: sqrt(1700) = 41.23
Annualized downside vol: 41.23 * sqrt(252) = 655.5
Sortino = $6,048 / 655.5 = 9.23
```

---

## Files Referenced

1. **Backend**: `modules/matrix/statistics.py`
   - Function: `_calculate_risk_daily_metrics()` (lines 835-1016)
   - Lines 917-925: Sharpe Ratio
   - Lines 927-931: Sortino Ratio
   - Lines 933-963: Time to Recovery

2. **Frontend (Primary)**: `modules/matrix_timetable_app/frontend/src/matrixWorker.js`
   - Function: `_calculateDailyMetrics()` (lines 668-889)
   - Lines 772-783: Sharpe Ratio
   - Lines 785-792: Sortino Ratio
   - Lines 794-820: Time to Recovery

3. **Frontend (Alternative)**: `modules/matrix_timetable_app/frontend/src/utils/statsCalculations.js`
   - Function: `calculateStats()` (lines 70-709)
   - Lines 284-286: Sharpe Ratio
   - Lines 288-295: Sortino Ratio
   - Lines 441-508: Time to Recovery

---

## Summary

| Metric | Data Source | Annualization | Key Formula |
|--------|-------------|---------------|--------------|
| **Sharpe Ratio** | Daily PnL series | 252 trading days | Annual Return / Annual Volatility |
| **Sortino Ratio** | Negative daily PnL only | 252 trading days | Annual Return / Annual Downside Volatility |
| **Time to Recovery** | Daily cumulative PnL | N/A (trading days) | Longest drawdown recovery period |

**All metrics**: Use executed trades only, respect `include_filtered_executed` toggle, rounded to 2 decimal places (except time_to_recovery_days which is integer).
