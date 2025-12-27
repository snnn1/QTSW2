// Web Worker for Matrix Data Processing
// Handles all heavy computations: filtering, stats, aggregations

class ColumnarData {
  constructor(data) {
    if (!data || data.length === 0) {
      this.columns = {}
      this.length = 0
      return
    }
    
    const columnNames = new Set()
    data.forEach(row => {
      Object.keys(row).forEach(key => columnNames.add(key))
    })
    
    this.columns = {}
    this.length = data.length
    
    columnNames.forEach(colName => {
      this.columns[colName] = new Array(data.length)
      data.forEach((row, idx) => {
        this.columns[colName][idx] = row[colName] ?? null
      })
    })
  }
  
  getColumn(name) {
    return this.columns[name] || new Array(this.length).fill(null)
  }
  
  getRow(index) {
    const row = {}
    Object.keys(this.columns).forEach(col => {
      row[col] = this.columns[col][index]
    })
    return row
  }
  
  getRows(indices) {
    return indices.map(idx => this.getRow(idx))
  }
  
  filter(mask) {
    const filtered = new ColumnarData([])
    filtered.length = mask.filter(Boolean).length
    
    if (filtered.length === 0) return filtered
    
    const filteredIndices = []
    for (let i = 0; i < mask.length; i++) {
      if (mask[i]) filteredIndices.push(i)
    }
    
    Object.keys(this.columns).forEach(col => {
      filtered.columns[col] = new Array(filtered.length)
      filteredIndices.forEach((origIdx, newIdx) => {
        filtered.columns[col][newIdx] = this.columns[col][origIdx]
      })
    })
    
    return filtered
  }
}

// ---------------------------------------------------------------------
// Date parsing cache
// ---------------------------------------------------------------------
const dateCache = new Map()

function parseDateCached(dateValue) {
  if (!dateValue) return null
  if (dateCache.has(dateValue)) return dateCache.get(dateValue)
  
  let parsed = null
  try {
        parsed = new Date(dateValue)
    if (isNaN(parsed.getTime())) parsed = null
  } catch {
    parsed = null
  }
  
  if (parsed) dateCache.set(dateValue, parsed)
  return parsed
}

// ---------------------------------------------------------------------
// Statistics (EXECUTED TRADES DOMAIN)
// ---------------------------------------------------------------------

// Definitions (AUTHORITATIVE)
function normalizeResult(resultValue) {
  if (!resultValue || resultValue === null || resultValue === undefined) return ''
  return String(resultValue).trim().toUpperCase()
}

function isExecutedTrade(resultNorm) {
  // is_executed_trade = ResultNorm in {"WIN","LOSS","BE","BREAKEVEN","TIME"}
  return resultNorm === 'WIN' || resultNorm === 'LOSS' || 
         resultNorm === 'BE' || resultNorm === 'BREAKEVEN' || 
         resultNorm === 'TIME'
}

function isNoTrade(resultNorm) {
  // is_notrade = ResultNorm == "NOTRADE"
  return resultNorm === 'NOTRADE'
}

function calculateStats(columnarData, streamId, contractMultiplier, contractValues, includeFilteredExecuted = true) {
  if (columnarData.length === 0) {
    return {
      sample_counts: {
        total_rows: 0,
        filtered_rows: 0,
        allowed_rows: 0,
        executed_trades_total: 0,
        executed_trades_allowed: 0,
        executed_trades_filtered: 0,
        notrade_total: 0
      },
      performance_trade_metrics: _emptyTradeMetrics(),
      performance_daily_metrics: _emptyDailyMetrics()
    }
  }

  const Result = columnarData.getColumn('Result')
  const Profit = columnarData.getColumn('Profit')
  const Instrument = columnarData.getColumn('Instrument')
  const Stream = columnarData.getColumn('Stream')
  const DateColumn = columnarData.getColumn('Date')
  const trade_date = columnarData.getColumn('trade_date')
  const FinalAllowed = columnarData.getColumn('final_allowed')
  const Time = columnarData.getColumn('Time')

  // Filter by stream if specified (not 'master')
  let dataIndices = []
  for (let i = 0; i < columnarData.length; i++) {
    if (streamId && streamId !== 'master' && Stream[i] !== streamId) {
      continue
    }
    dataIndices.push(i)
  }

  // Normalize results and identify executed trades and NoTrade
  const resultNorm = []
  const isExecuted = []
  const isNoTradeFlag = []
  
  for (let i of dataIndices) {
    const norm = normalizeResult(Result[i])
    resultNorm[i] = norm
    isExecuted[i] = isExecutedTrade(norm)
    isNoTradeFlag[i] = isNoTrade(norm)
  }

  // ========================================================================
  // SAMPLE COUNTS
  // ========================================================================
  const totalRows = dataIndices.length
  let filteredRows = 0
  let allowedRows = 0
  let executedTradesTotal = 0
  let executedTradesAllowed = 0
  let executedTradesFiltered = 0
  let notradeTotal = 0

  const executedIndices = []
  const executedAllowedIndices = []
  const executedFilteredIndices = []

  for (let i of dataIndices) {
    const finalAllowed = FinalAllowed[i] !== false // Default to true if undefined
    if (finalAllowed) {
      allowedRows++
    } else {
      filteredRows++
    }

    if (isExecuted[i]) {
      executedTradesTotal++
      executedIndices.push(i)
      if (finalAllowed) {
        executedTradesAllowed++
        executedAllowedIndices.push(i)
      } else {
        executedTradesFiltered++
        executedFilteredIndices.push(i)
      }
    }

    if (isNoTradeFlag[i]) {
      notradeTotal++
    }
  }

  const sample_counts = {
    total_rows: totalRows,
    filtered_rows: filteredRows,
    allowed_rows: allowedRows,
    executed_trades_total: executedTradesTotal,
    executed_trades_allowed: executedTradesAllowed,
    executed_trades_filtered: executedTradesFiltered,
    notrade_total: notradeTotal
  }

  // ========================================================================
  // CALCULATE DAY COUNTS
  // - executed_trading_days: from ALL executed trades (for reporting)
  // - allowed_trading_days: from stats sample (days actually traded in stats)
  // ========================================================================
  const executedDayCounts = _calculateDayCounts(executedIndices, DateColumn, trade_date)
  const executedTradingDays = executedDayCounts.executed_trading_days

  // ========================================================================
  // SELECT STATS SAMPLE: Executed trades (optionally filtered by final_allowed)
  // ========================================================================
  let statsSampleIndices = includeFilteredExecuted ? executedIndices : executedAllowedIndices

  if (statsSampleIndices.length === 0) {
    return {
      sample_counts,
      performance_trade_metrics: _emptyTradeMetrics(),
      performance_daily_metrics: _emptyDailyMetrics(),
      day_counts: {
        executed_trading_days: executedTradingDays,  // All executed trades (for reference)
        allowed_trading_days: 0  // Active days from stats sample (0 when no stats sample)
      }
    }
  }

  // ========================================================================
  // ACTIVE TRADING DAYS: Count unique days from stats sample ONLY
  // A day is active if and only if it contains ≥1 trade in the stats sample
  // When toggle OFF: Days with only filtered trades are NOT counted
  // When toggle ON: All days with executed trades are counted
  // ========================================================================
  const activeTradingDays = _countUniqueDays(statsSampleIndices, DateColumn, trade_date)
  const statsSampleTradeCount = statsSampleIndices.length

  // ========================================================================
  // PERFORMANCE TRADE METRICS (per-trade, computed on executed trades only)
  // ========================================================================
  const performance_trade_metrics = _calculateTradeMetrics(
    statsSampleIndices,
    resultNorm,
    Profit,
    Instrument,
    contractValues,
    contractMultiplier
  )

  // ========================================================================
  // PERFORMANCE DAILY METRICS (daily aggregation, computed on executed trades only)
  // Behavioral averages use active_trading_days (from stats sample ONLY)
  // Risk metrics (Sharpe/Sortino/Calmar) use daily PnL series from stats sample
  // ========================================================================
  const performance_daily_metrics = _calculateDailyMetrics(
    statsSampleIndices,
    resultNorm,
    Profit,
    Instrument,
    DateColumn,
    trade_date,
    contractValues,
    contractMultiplier,
    activeTradingDays,
    performance_trade_metrics.total_profit,
    statsSampleTradeCount
  )

  return {
    sample_counts,
    performance_trade_metrics,
    performance_daily_metrics,
    day_counts: {
      executed_trading_days: executedTradingDays,  // All executed trades (for reference)
      allowed_trading_days: activeTradingDays  // Active days from stats sample (for behavioral metrics)
    }
  }
}

function _emptyTradeMetrics() {
  return {
    total_profit: 0.0,
    wins: 0,
    losses: 0,
    be: 0,
    time: 0,
    win_rate: 0.0,
    profit_factor: 0.0,
    rr_ratio: 0.0,
    mean_pnl_per_trade: 0.0,
    median_pnl_per_trade: 0.0,
    stddev_pnl_per_trade: 0.0,
    max_consecutive_losses: 0,
    max_drawdown: 0.0,
    var95: 0.0,
    cvar95: 0.0
  }
}

function _calculateDayCounts(executedIndices, DateColumn, trade_date) {
  /**
   * Calculate executed trading day count from ALL executed trades.
   * 
   * Definition:
   * - Executed Trading Day: A calendar day with ≥1 is_executed_trade == True
   *   (includes filtered and allowed trades)
   */
  if (executedIndices.length === 0) {
    return { executed_trading_days: 0 }
  }

  const executedDays = new Set()

  for (const i of executedIndices) {
    const dateValue = DateColumn[i] || trade_date[i]
    if (!dateValue) continue

    const parsed = parseDateCached(dateValue)
    if (!parsed) continue

    const dayKey = parsed.toISOString().split('T')[0] // YYYY-MM-DD
    executedDays.add(dayKey)
  }

  return {
    executed_trading_days: executedDays.size
  }
}

function _countUniqueDays(indices, DateColumn, trade_date) {
  /**
   * Count unique trading days in a set of indices.
   * 
   * Used for calculating allowed_trading_days from stats sample.
   */
  if (indices.length === 0) {
    return 0
  }

  const uniqueDays = new Set()

  for (const i of indices) {
    const dateValue = DateColumn[i] || trade_date[i]
    if (!dateValue) continue

    const parsed = parseDateCached(dateValue)
    if (!parsed) continue

    const dayKey = parsed.toISOString().split('T')[0] // YYYY-MM-DD
    uniqueDays.add(dayKey)
  }

  return uniqueDays.size
}

function _emptyDailyMetrics() {
    return {
    executed_trading_days: 0,
    allowed_trading_days: 0,
    avg_trades_per_day: 0.0,
    profit_per_day: 0.0,
    profit_per_week: 0.0,
    profit_per_month: 0.0,
    profit_per_year: 0.0,
    sharpe_ratio: 0.0,
    sortino_ratio: 0.0,
    calmar_ratio: 0.0,
    time_to_recovery_days: 0,
    monthly_return_stddev: 0.0
  }
}

function _calculateTradeMetrics(
  indices,
  resultNorm,
  Profit,
  Instrument,
  contractValues,
  contractMultiplier
) {
  if (indices.length === 0) {
    return _emptyTradeMetrics()
  }

  // Count by result type - single pass optimization
  let wins = 0, losses = 0, be = 0, time = 0
  const perTradePnLDollars = []
  let totalProfit = 0
  let grossProfit = 0
  let grossLoss = 0
  let winSum = 0
  let lossSum = 0

  for (let i of indices) {
    // Use pre-computed resultNorm instead of re-normalizing (performance optimization)
    const result = resultNorm[i]
    const profit = parseFloat(Profit[i]) || 0

    const symbol = Instrument[i] || 'ES'
    const baseSymbol = symbol.replace(/\d+$/, '') || symbol
    const contractValue = contractValues[baseSymbol] || 50
    const profitDollars = profit * contractValue * contractMultiplier

    perTradePnLDollars.push(profitDollars)
    totalProfit += profitDollars

    // Track profit/loss for profit factor
    if (profitDollars > 0) {
      grossProfit += profitDollars
    } else if (profitDollars < 0) {
      grossLoss += Math.abs(profitDollars)
    }

    if (result === 'WIN') {
      wins++
      winSum += profitDollars
    } else if (result === 'LOSS') {
      losses++
      lossSum += Math.abs(profitDollars)
    } else if (result === 'BE' || result === 'BREAKEVEN') {
      be++
    } else if (result === 'TIME') {
      time++
    }
  }

  // Win rate (wins / (wins + losses), excluding BE and TIME)
  const winLossTrades = wins + losses
  const winRate = winLossTrades > 0 ? (wins / winLossTrades) * 100 : 0.0

  // Profit factor (already calculated in loop)
  const profitFactor = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? Infinity : 0.0)

  // Risk-Reward ratio (avg_win / avg_loss) - already have sums
  const avgWin = wins > 0 ? winSum / wins : 0
  const avgLoss = losses > 0 ? lossSum / losses : 0
  const rrRatio = avgLoss > 0 ? avgWin / avgLoss : (avgWin > 0 ? Infinity : 0.0)

  // PnL statistics
  const meanPnL = perTradePnLDollars.length > 0 
    ? totalProfit / perTradePnLDollars.length 
    : 0.0

  // Median
  const sortedPnL = [...perTradePnLDollars].sort((a, b) => a - b)
  const medianPnL = sortedPnL.length % 2 === 0
    ? (sortedPnL[sortedPnL.length / 2 - 1] + sortedPnL[sortedPnL.length / 2]) / 2
    : sortedPnL[Math.floor(sortedPnL.length / 2)]
  
  // Std dev - optimized single pass
  let varianceSum = 0
  if (perTradePnLDollars.length > 1) {
    for (const pnl of perTradePnLDollars) {
      varianceSum += Math.pow(pnl - meanPnL, 2)
    }
  }
  const variance = perTradePnLDollars.length > 1 ? varianceSum / (perTradePnLDollars.length - 1) : 0
  const stdDevPnL = Math.sqrt(variance)
  
  // Max consecutive losses
  let maxConsecutiveLosses = 0
  let currentStreak = 0
  for (let i of indices) {
    if (resultNorm[i] === 'LOSS') {
      currentStreak++
      maxConsecutiveLosses = Math.max(maxConsecutiveLosses, currentStreak)
    } else {
      currentStreak = 0
    }
  }
  
  // Max drawdown (trade equity curve) - keep in dollars, no conversion
  let cumulativeProfit = 0
  let runningMax = 0
  let maxDrawdown = 0
  for (let pnl of perTradePnLDollars) {
    cumulativeProfit += pnl
    runningMax = Math.max(runningMax, cumulativeProfit)
    const drawdown = cumulativeProfit - runningMax
    maxDrawdown = Math.max(maxDrawdown, Math.abs(drawdown))
  }

  // VaR and CVaR (95%)
  if (sortedPnL.length > 0) {
    const var95Idx = Math.floor(sortedPnL.length * 0.05)
    const var95 = var95Idx < sortedPnL.length ? sortedPnL[var95Idx] : sortedPnL[0]
    const cvar95Values = sortedPnL.slice(0, var95Idx + 1)
    const cvar95 = cvar95Values.length > 0
      ? cvar95Values.reduce((a, b) => a + b, 0) / cvar95Values.length
      : var95
    return {
      total_profit: Math.round(totalProfit * 100) / 100,
      wins,
      losses,
      be,
      time,
      win_rate: Math.round(winRate * 10) / 10,
      profit_factor: Math.round(profitFactor * 100) / 100,
      rr_ratio: Math.round(rrRatio * 100) / 100,
      mean_pnl_per_trade: Math.round(meanPnL * 100) / 100,
      median_pnl_per_trade: Math.round(medianPnL * 100) / 100,
      stddev_pnl_per_trade: Math.round(stdDevPnL * 100) / 100,
      max_consecutive_losses: maxConsecutiveLosses,
      max_drawdown: Math.round(maxDrawdown * 100) / 100,
      var95: Math.round(var95 * 100) / 100,
      cvar95: Math.round(cvar95 * 100) / 100
    }
  }

  return _emptyTradeMetrics()
}

function _calculateDailyMetrics(
  indices,
  resultNorm,
  Profit,
  Instrument,
  DateColumn,
  trade_date,
  contractValues,
  contractMultiplier,
  activeTradingDays,
  totalProfit,
  statsSampleTradeCount
) {
  /**
   * Calculate daily aggregation performance metrics.
   * 
   * CRITICAL: Behavioral averages (profit_per_day, avg_trades_per_day) use active_trading_days,
   * which is computed from the stats sample ONLY. A day is active if and only if it contains
   * ≥1 trade in the stats sample. Days with zero trades in the stats sample are NOT counted.
   * 
   * Risk metrics (Sharpe/Sortino/Calmar) use daily PnL series from stats sample.
   */
  if (indices.length === 0) {
    return _emptyDailyMetrics()
  }

  // Group by trading day (date only) - for risk metrics (daily PnL series)
  const dailyProfits = new Map() // date string -> array of profit dollars

  for (let i of indices) {
    const dateValue = DateColumn[i] || trade_date[i]
    if (!dateValue) continue

      const parsed = parseDateCached(dateValue)
    if (!parsed) continue

    const dayKey = parsed.toISOString().split('T')[0] // YYYY-MM-DD

    const profit = parseFloat(Profit[i]) || 0
    const symbol = Instrument[i] || 'ES'
    const baseSymbol = symbol.replace(/\d+$/, '') || symbol
    const contractValue = contractValues[baseSymbol] || 50
    const profitDollars = profit * contractValue * contractMultiplier

        if (!dailyProfits.has(dayKey)) {
      dailyProfits.set(dayKey, [])
    }
    dailyProfits.get(dayKey).push(profitDollars)
  }

  // Sum per day
  const dailyPnL = []
  const dates = []
  for (const [dateStr, profits] of dailyProfits.entries()) {
    dates.push(dateStr)
    dailyPnL.push(profits.reduce((a, b) => a + b, 0))
  }

  if (dailyPnL.length === 0) {
    return _emptyDailyMetrics()
  }

  // Behavioral averages use active_trading_days (days with ≥1 trade in stats sample)
  // avg_trades_per_active_day = stats_sample_trade_count / active_trading_days
  const avgTradesPerDay = activeTradingDays > 0 ? statsSampleTradeCount / activeTradingDays : 0.0
  
  // profit_per_active_day = total_profit / active_trading_days
  const profitPerDay = activeTradingDays > 0 ? totalProfit / activeTradingDays : 0.0

  // Sharpe/Sortino ratios (annualized using 252 trading days)
  // Use mean of daily PnL series for risk metrics (not profitPerDay)
  const meanDailyReturn = dailyPnL.length > 0 ? dailyPnL.reduce((a, b) => a + b, 0) / dailyPnL.length : 0.0
  const variance = dailyPnL.length > 1
    ? dailyPnL.reduce((sum, r) => sum + Math.pow(r - meanDailyReturn, 2), 0) / (dailyPnL.length - 1)
    : 0
  const stdDailyReturn = Math.sqrt(variance)

    const tradingDaysPerYear = 252
    const annualizedReturn = meanDailyReturn * tradingDaysPerYear
  const annualizedVolatility = stdDailyReturn * Math.sqrt(tradingDaysPerYear)
  const sharpeRatio = annualizedVolatility > 0 ? annualizedReturn / annualizedVolatility : 0.0

  // Sortino: only downside volatility
  const downsideReturns = dailyPnL.filter(r => r < 0)
  const downsideVariance = downsideReturns.length > 1
    ? downsideReturns.reduce((sum, r) => sum + Math.pow(r - meanDailyReturn, 2), 0) / (downsideReturns.length - 1)
    : 0
  const downsideStd = Math.sqrt(downsideVariance)
  const annualizedDownsideVol = downsideStd * Math.sqrt(tradingDaysPerYear)
  const sortinoRatio = annualizedDownsideVol > 0 ? annualizedReturn / annualizedDownsideVol : 0.0

  // Time to recovery (trading days, not calendar days)
  // Count as difference in trading day index
  let cumulativePnL = 0
  let runningMax = 0
  let timeToRecoveryDays = 0
  let inDrawdown = false
  let drawdownStartIdx = null

  for (let idx = 0; idx < dailyPnL.length; idx++) {
    cumulativePnL += dailyPnL[idx]
    runningMax = Math.max(runningMax, cumulativePnL)
    const drawdown = cumulativePnL - runningMax

    if (drawdown < 0) {
      if (!inDrawdown) {
        inDrawdown = true
        drawdownStartIdx = idx
      }
    } else {
      if (inDrawdown && drawdownStartIdx !== null) {
        const recoveryDays = idx - drawdownStartIdx
        timeToRecoveryDays = Math.max(timeToRecoveryDays, recoveryDays)
        inDrawdown = false
        drawdownStartIdx = null
      }
    }
  }

  // Monthly return std dev (Option 1: actual calendar months)
  // Group by YYYY-MM
  const monthlyProfits = new Map() // "YYYY-MM" -> array of daily PnL
  for (let idx = 0; idx < dates.length; idx++) {
    const dateStr = dates[idx]
    const yearMonth = dateStr.substring(0, 7) // "YYYY-MM"
    if (!monthlyProfits.has(yearMonth)) {
      monthlyProfits.set(yearMonth, [])
    }
    monthlyProfits.get(yearMonth).push(dailyPnL[idx])
  }

  // Sum per month
  const monthlyPnL = []
  for (const profits of monthlyProfits.values()) {
    monthlyPnL.push(profits.reduce((a, b) => a + b, 0))
  }

  const monthlyReturnStdDev = monthlyPnL.length > 1
    ? (() => {
        const meanMonthly = monthlyPnL.reduce((a, b) => a + b, 0) / monthlyPnL.length
        const variance = monthlyPnL.reduce((sum, r) => sum + Math.pow(r - meanMonthly, 2), 0) / (monthlyPnL.length - 1)
        return Math.sqrt(variance)
      })()
    : 0.0

  // Calmar ratio (annualized return / max drawdown from daily)
  // Use mean daily return from PnL series for consistency with Sharpe/Sortino
  const annualReturnFromDaily = meanDailyReturn * tradingDaysPerYear
  let cumulativePnLForCalmar = 0
  let runningMaxForCalmar = 0
  let maxDrawdownFromDaily = 0
  for (const pnl of dailyPnL) {
    cumulativePnLForCalmar += pnl
    runningMaxForCalmar = Math.max(runningMaxForCalmar, cumulativePnLForCalmar)
    const drawdown = cumulativePnLForCalmar - runningMaxForCalmar
    maxDrawdownFromDaily = Math.max(maxDrawdownFromDaily, Math.abs(drawdown))
  }
  const calmarRatio = maxDrawdownFromDaily > 0 ? annualReturnFromDaily / maxDrawdownFromDaily : 0.0

  // Projected metrics (from profit_per_day)
  const profitPerWeek = profitPerDay * 5
  const profitPerMonth = profitPerDay * 21
  const profitPerYear = profitPerDay * 252

  return {
    executed_trading_days: dailyPnL.length, // Days in stats sample daily PnL series (for reference)
    allowed_trading_days: activeTradingDays, // Active trading days (used for behavioral averages)
    avg_trades_per_day: Math.round(avgTradesPerDay * 100) / 100,
    profit_per_day: Math.round(profitPerDay * 100) / 100,
    profit_per_week: Math.round(profitPerWeek * 100) / 100,
    profit_per_month: Math.round(profitPerMonth * 100) / 100,
    profit_per_year: Math.round(profitPerYear * 100) / 100,
    sharpe_ratio: Math.round(sharpeRatio * 100) / 100,
    sortino_ratio: Math.round(sortinoRatio * 100) / 100,
    calmar_ratio: Math.round(calmarRatio * 100) / 100,
    time_to_recovery_days: timeToRecoveryDays,
    monthly_return_stddev: Math.round(monthlyReturnStdDev * 100) / 100
  }
}

// ---------------------------------------------------------------------
// Minimal filtering (stream + year only, NOT DOW/DOM - those are already marked in backend)
// ---------------------------------------------------------------------
function createFilterMask(columnarData, streamFilters, streamId) {
  const length = columnarData.length
  const mask = new Array(length).fill(true)
  const Stream = columnarData.getColumn('Stream')
  const DateColumn = columnarData.getColumn('Date')
  const trade_date = columnarData.getColumn('trade_date')

  // Extract years for filtering
  const years = new Array(length)
  for (let i = 0; i < length; i++) {
    const dateValue = DateColumn[i] || trade_date[i]
    if (dateValue) {
      const parsed = parseDateCached(dateValue)
      years[i] = parsed ? parsed.getFullYear() : null
    } else {
      years[i] = null
    }
  }

  for (let i = 0; i < length; i++) {
    // Filter by stream if specified
    if (streamId && streamId !== 'master' && Stream[i] !== streamId) {
      mask[i] = false
      continue
    }

    // Apply year filters only (DOW/DOM already marked with final_allowed=False in backend)
    const rowStream = Stream[i]
    if (streamId === 'master' || !streamId) {
      const streamFilter = streamFilters[rowStream]
      if (streamFilter?.include_years?.length > 0) {
        const yearSet = new Set(streamFilter.include_years)
        if (years[i] === null || !yearSet.has(years[i])) {
          mask[i] = false
          continue
        }
      }
      
      // Master-specific year filter
      const masterFilter = streamFilters['master']
      if (masterFilter?.include_years?.length > 0) {
        const yearSet = new Set(masterFilter.include_years)
        if (years[i] === null || !yearSet.has(years[i])) {
          mask[i] = false
          continue
        }
      }
    } else if (streamId && streamId !== 'master') {
      const streamFilter = streamFilters[streamId]
      if (streamFilter?.include_years?.length > 0) {
        const yearSet = new Set(streamFilter.include_years)
        if (years[i] === null || !yearSet.has(years[i])) {
          mask[i] = false
          continue
        }
      }
    }
  }

  return mask
}

// ---------------------------------------------------------------------
// Worker message handler
// ---------------------------------------------------------------------
self.onmessage = function(e) {
  const { type, payload } = e.data
  
  try {
    switch (type) {
      case 'INIT_DATA': {
        self.columnarData = new ColumnarData(payload.data)
        self.postMessage({ type: 'DATA_INITIALIZED', payload: { length: self.columnarData.length } })
        break
      }
      
      case 'FILTER': {
        if (!self.columnarData) {
          self.postMessage({ type: 'ERROR', payload: { message: 'Data not initialized' } })
          return
        }
        
        const { streamFilters, streamId, returnRows = false, sortIndices = null } = payload
        const mask = createFilterMask(self.columnarData, streamFilters || {}, streamId)
        
        // Get filtered indices
        const filteredIndices = []
        for (let i = 0; i < mask.length; i++) {
          if (mask[i]) filteredIndices.push(i)
        }
        
        // Sort indices if requested
        if (sortIndices && filteredIndices.length > 0) {
          filteredIndices.sort((a, b) => {
            const dateA = self.columnarData.getColumn('Date')[a] || self.columnarData.getColumn('trade_date')[a]
            const dateB = self.columnarData.getColumn('Date')[b] || self.columnarData.getColumn('trade_date')[b]
            const parsedA = parseDateCached(dateA)
            const parsedB = parseDateCached(dateB)
            if (parsedA && parsedB) {
              const dateDiff = parsedB.getTime() - parsedA.getTime()
              if (dateDiff !== 0) return dateDiff
              const timeA = self.columnarData.getColumn('Time')[a] || ''
              const timeB = self.columnarData.getColumn('Time')[b] || ''
              if (timeA && timeB) {
                const [hA, mA] = timeA.split(':').map(Number)
                const [hB, mB] = timeB.split(':').map(Number)
                const minsA = (hA || 0) * 60 + (mA || 0)
                const minsB = (hB || 0) * 60 + (mB || 0)
                if (minsA !== minsB) return minsB - minsA
              }
            }
            return 0
          })
        }
        
        const response = {
          length: filteredIndices.length,
          mask: mask,
          indices: filteredIndices
        }
        
        // Optionally return rows (for initial render)
        if (returnRows && filteredIndices.length > 0) {
          const maxRows = Math.min(filteredIndices.length, 100)
          response.rows = self.columnarData.getRows(filteredIndices.slice(0, maxRows))
        }
        
        self.postMessage({ type: 'FILTERED', payload: response })
        break
      }
      
      case 'GET_ROWS': {
        if (!self.columnarData) {
          self.postMessage({ type: 'ERROR', payload: { message: 'Data not initialized' } })
          return
        }
        const { indices } = payload
        const rows = self.columnarData.getRows(indices)
        self.postMessage({ type: 'ROWS', payload: { rows } })
        break
      }
      
      case 'CALCULATE_STATS': {
        if (!self.columnarData) {
          self.postMessage({ type: 'ERROR', payload: { message: 'Data not initialized' } })
          return
        }
        // Note: streamFilters not used here - stream filtering happens in calculateStats via streamId
        const { streamId, contractMultiplier, contractValues, includeFilteredExecuted = true } = payload
        const stats = calculateStats(self.columnarData, streamId, contractMultiplier, contractValues, includeFilteredExecuted)
        self.postMessage({ type: 'STATS', payload: { stats } })
        break
      }
      
      case 'CALCULATE_TIMETABLE': {
        if (!self.columnarData) {
          self.postMessage({ type: 'ERROR', payload: { message: 'Data not initialized' } })
          return
        }
        
        const { streamFilters = {}, currentTradingDay } = payload
        
        // Extract day-of-week and day-of-month from current trading day
        // JavaScript getDay(): 0=Sunday, 1=Monday, 2=Tuesday, 3=Wednesday, 4=Thursday, 5=Friday, 6=Saturday
        let targetDOWJS = null // JavaScript day-of-week (0-6)
        let targetDOWName = null // Day name ("Monday", "Tuesday", etc.)
        let targetDOM = null // 1-31
        if (currentTradingDay) {
          let dateObj = null
          if (currentTradingDay instanceof Date) {
            dateObj = currentTradingDay
          } else if (typeof currentTradingDay === 'string') {
            dateObj = parseDateCached(currentTradingDay)
          }
          if (dateObj) {
            targetDOWJS = dateObj.getDay() // 0-6
            targetDOM = dateObj.getDate() // 1-31
            
            // Map to day name for comparison with string filters
            const dayNames = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
            targetDOWName = dayNames[targetDOWJS]
          }
        }
        
        if (targetDOWJS === null || targetDOM === null) {
          self.postMessage({ type: 'TIMETABLE', payload: { timetable: [] } })
          return
        }
        
        // Diagnostic logging
        console.log('[Worker] Timetable DOW/DOM:', {
          targetDOWJS,
          targetDOWName,
          targetDOM,
          sampleStreamFilters: Object.keys(streamFilters).slice(0, 3).reduce((acc, key) => {
            acc[key] = streamFilters[key]?.exclude_days_of_week
            return acc
          }, {})
        })
        
        const DateColumn = self.columnarData.getColumn('Date')
        const trade_date = self.columnarData.getColumn('trade_date')
        const Stream = self.columnarData.getColumn('Stream')
        const Time = self.columnarData.getColumn('Time')
        const TimeChange = self.columnarData.getColumn('Time Change') || []
        
        // Find the latest date in the dataset
        let latestDateStr = null
        let latestDateParsed = null
        const dateStrings = new Set()
        
        for (let i = 0; i < self.columnarData.length; i++) {
          const dateValue = DateColumn[i] || trade_date[i]
          if (!dateValue) continue
          
          const parsed = parseDateCached(dateValue)
          if (!parsed) continue
          
          const dayKey = parsed.toISOString().split('T')[0] // YYYY-MM-DD
          dateStrings.add(dayKey)
          
          if (!latestDateParsed || parsed > latestDateParsed) {
            latestDateParsed = parsed
            latestDateStr = dayKey
          }
        }
        
        if (!latestDateStr) {
          self.postMessage({ type: 'TIMETABLE', payload: { timetable: [] } })
          return
        }
        
        // Build timetable from latest date, applying filters based on current trading day's DOW/DOM
        const timetableRows = []
        
        for (let i = 0; i < self.columnarData.length; i++) {
          const dateValue = DateColumn[i] || trade_date[i]
          if (!dateValue) continue
          
          const parsed = parseDateCached(dateValue)
          if (!parsed) continue
          
          const dayKey = parsed.toISOString().split('T')[0]
          if (dayKey !== latestDateStr) continue // Only use latest date
          
          const stream = Stream[i] || ''
          const time = Time[i] || ''
          const timeChange = TimeChange[i] || ''
          
          if (!stream || !time) continue
          
          // Check if this stream has the target DOW filtered out
          const streamFilter = streamFilters[stream]
          if (streamFilter?.exclude_days_of_week?.length > 0) {
            const excludedDOW = streamFilter.exclude_days_of_week
            // Check if filter matches either as number (JS day) or as string (day name)
            const isFiltered = excludedDOW.some(d => {
              const filterVal = typeof d === 'string' ? d : String(d)
              const filterNum = parseInt(filterVal)
              // Match as string name or as number (JS day-of-week)
              const matches = filterVal === targetDOWName || filterNum === targetDOWJS
              if (stream === 'YM1' && matches) {
                console.log('[Worker] YM1 filtered by DOW:', { filterVal, filterNum, targetDOWName, targetDOWJS, excludedDOW })
              }
              return matches
            })
            if (isFiltered) {
              continue // This stream has this day of week filtered out
            }
          }
          
          // Check if this stream has the target DOM filtered out
          if (streamFilter?.exclude_days_of_month?.length > 0) {
            const excludedDOM = streamFilter.exclude_days_of_month.map(d => parseInt(d))
            if (excludedDOM.includes(targetDOM)) {
              continue // This stream has this day of month filtered out
            }
          }
          
          // Check master stream filters
          const masterFilter = streamFilters['master']
          if (masterFilter?.exclude_days_of_week?.length > 0) {
            const excludedDOW = masterFilter.exclude_days_of_week
            const isFiltered = excludedDOW.some(d => {
              const filterVal = typeof d === 'string' ? d : String(d)
              const filterNum = parseInt(filterVal)
              return filterVal === targetDOWName || filterNum === targetDOWJS
            })
            if (isFiltered) {
              continue
            }
          }
          if (masterFilter?.exclude_days_of_month?.length > 0) {
            const excludedDOM = masterFilter.exclude_days_of_month.map(d => parseInt(d))
            if (excludedDOM.includes(targetDOM)) {
              continue
            }
          }
          
          // Use Time Change if available, otherwise use Time
          // Time Change format is like "09:30 -> 10:00", extract the target time (after ->)
          let displayTime = time
          if (timeChange && timeChange.includes('->')) {
            const parts = timeChange.split('->')
            if (parts.length === 2) {
              displayTime = parts[1].trim()
            }
          }
          
          timetableRows.push({ Stream: stream, Time: displayTime })
        }
        
        // Sort by time descending (latest time first, earliest time last)
        timetableRows.sort((a, b) => {
          const parseTime = (timeStr) => {
            if (!timeStr) return 0
            const [h, m] = (timeStr || '').split(':').map(Number)
            return (h || 0) * 60 + (m || 0)
          }
          
          const minsA = parseTime(a.Time)
          const minsB = parseTime(b.Time)
          
          // Descending order (latest first)
          return minsB - minsA
        })
        
        self.postMessage({ type: 'TIMETABLE', payload: { timetable: timetableRows } })
        break
      }
      
      case 'CALCULATE_PROFIT_BREAKDOWN': {
        if (!self.columnarData) {
          self.postMessage({ type: 'ERROR', payload: { message: 'Data not initialized' } })
          return
        }
        
        const { streamFilters = {}, streamId = 'master', contractMultiplier = 1, contractValues = {}, breakdownType, useFiltered = false } = payload
        
        // Parse breakdown type (e.g., "time_before", "day_after")
        const [periodType, filterType] = breakdownType.split('_')
        
        // Get data indices based on filtering
        let dataIndices = []
        if (useFiltered) {
          // Use filtered data (apply stream/year filters AND final_allowed)
          const mask = createFilterMask(self.columnarData, streamFilters, streamId)
          const FinalAllowed = self.columnarData.getColumn('final_allowed')
          for (let i = 0; i < mask.length; i++) {
            // Must pass the mask AND have final_allowed !== false
            if (mask[i] && FinalAllowed[i] !== false) {
              dataIndices.push(i)
            }
          }
        } else {
          // Use all data
          for (let i = 0; i < self.columnarData.length; i++) {
            // Filter by stream if specified
            if (streamId && streamId !== 'master') {
              const Stream = self.columnarData.getColumn('Stream')
              if (Stream[i] !== streamId) continue
            }
            dataIndices.push(i)
          }
        }
        
        if (dataIndices.length === 0) {
          self.postMessage({ type: 'PROFIT_BREAKDOWN', payload: { breakdown: {}, breakdownType } })
          return
        }
        
        // Get columns
        const DateColumn = self.columnarData.getColumn('Date')
        const trade_date = self.columnarData.getColumn('trade_date')
        const Stream = self.columnarData.getColumn('Stream')
        const Time = self.columnarData.getColumn('Time')
        const Profit = self.columnarData.getColumn('Profit')
        const Instrument = self.columnarData.getColumn('Instrument')
        
        // Contract values helper
        const getContractValue = (symbol) => {
          if (!symbol) return 50
          const baseSymbol = symbol.replace(/\d+$/, '') || symbol
          return contractValues[baseSymbol] || 50
        }
        
        const breakdownData = {}
        
        // Calculate profit breakdown based on period type
        for (const i of dataIndices) {
          const profit = parseFloat(Profit[i]) || 0
          const symbol = Instrument[i] || 'ES'
          const stream = Stream[i] || 'Unknown'
          const contractValue = getContractValue(symbol)
          const profitDollars = profit * contractValue * contractMultiplier
          
          let periodKey = null
          
          if (periodType === 'time') {
            const time = Time[i] || ''
            if (!time || time === 'NA' || time === '00:00') continue
            let timeKey = time.toString().trim()
            if (!/^\d{2}:\d{2}$/.test(timeKey)) {
              const match = timeKey.match(/(\d{2}:\d{2})/)
              if (match) timeKey = match[1]
              else continue
            }
            periodKey = timeKey
          } else {
            const dateValue = DateColumn[i] || trade_date[i]
            if (!dateValue) continue
            const parsed = parseDateCached(dateValue)
            if (!parsed) continue
            
            if (periodType === 'day') {
              // Day of Week (Monday, Tuesday, etc.)
              const dow = parsed.toLocaleDateString('en-US', { weekday: 'long' })
              const dowOrder = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
              if (!dowOrder.includes(dow)) continue
              periodKey = dow
            } else if (periodType === 'dom') {
              periodKey = parsed.getDate() // 1-31
            } else if (periodType === 'date') {
              periodKey = parsed.toISOString().split('T')[0] // YYYY-MM-DD
            } else if (periodType === 'month') {
              const year = parsed.getFullYear()
              const month = parsed.getMonth() + 1
              periodKey = `${year}-${String(month).padStart(2, '0')}`
            } else if (periodType === 'year') {
              periodKey = parsed.getFullYear()
            }
          }
          
          if (periodKey === null) continue
          
          if (!breakdownData[periodKey]) {
            breakdownData[periodKey] = {}
          }
          if (!breakdownData[periodKey][stream]) {
            breakdownData[periodKey][stream] = 0
          }
          breakdownData[periodKey][stream] += profitDollars
        }
        
        // Sort results based on period type
        let sortedBreakdown = {}
        if (periodType === 'time') {
          const sortedKeys = Object.keys(breakdownData).sort((a, b) => {
            const [aHour, aMin] = a.split(':').map(Number)
            const [bHour, bMin] = b.split(':').map(Number)
            if (aHour !== bHour) return aHour - bHour
            return aMin - bMin
          })
          sortedKeys.forEach(key => {
            sortedBreakdown[key] = breakdownData[key]
          })
        } else if (periodType === 'dom') {
          for (let day = 1; day <= 31; day++) {
            if (breakdownData[day]) {
              sortedBreakdown[day] = breakdownData[day]
            }
          }
        } else if (periodType === 'day') {
          // Sort day of week in order: Monday, Tuesday, Wednesday, Thursday, Friday
          const dowOrder = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
          dowOrder.forEach(dow => {
            if (breakdownData[dow]) {
              sortedBreakdown[dow] = breakdownData[dow]
            }
          })
        } else {
          // date, month, year: sort keys naturally (descending for dates)
          if (periodType === 'date') {
            // Sort dates descending (latest first)
            const sortedKeys = Object.keys(breakdownData).sort((a, b) => b.localeCompare(a))
            sortedKeys.forEach(key => {
              sortedBreakdown[key] = breakdownData[key]
            })
          } else {
            sortedBreakdown = breakdownData
          }
        }
        
        self.postMessage({ type: 'PROFIT_BREAKDOWN', payload: { breakdown: sortedBreakdown, breakdownType } })
        break
      }
      
      default:
        self.postMessage({ type: 'ERROR', payload: { message: `Unknown message type: ${type}` } })
    }
  } catch (err) {
    self.postMessage({ type: 'ERROR', payload: { message: err.message, stack: err.stack } })
  }
}
