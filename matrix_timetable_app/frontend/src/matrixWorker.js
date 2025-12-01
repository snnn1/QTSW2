// Web Worker for Matrix Data Processing
// Handles all heavy computations: filtering, stats, aggregations

// Columnar data structure
class ColumnarData {
  constructor(data) {
    if (!data || data.length === 0) {
      this.columns = {}
      this.length = 0
      return
    }
    
    // Extract all unique column names
    const columnNames = new Set()
    data.forEach(row => {
      Object.keys(row).forEach(key => columnNames.add(key))
    })
    
    // Convert to columnar format
    this.columns = {}
    this.length = data.length
    
    columnNames.forEach(colName => {
      this.columns[colName] = new Array(data.length)
      data.forEach((row, idx) => {
        this.columns[colName][idx] = row[colName] ?? null
      })
    })
  }
  
  // Get a column as array
  getColumn(name) {
    return this.columns[name] || new Array(this.length).fill(null)
  }
  
  // Get a row as object
  getRow(index) {
    const row = {}
    Object.keys(this.columns).forEach(col => {
      row[col] = this.columns[col][index]
    })
    return row
  }
  
  // Get multiple rows
  getRows(indices) {
    return indices.map(idx => this.getRow(idx))
  }
  
  // Filter using bitmask
  filter(mask) {
    const filtered = new ColumnarData([])
    filtered.length = mask.filter(Boolean).length
    
    if (filtered.length === 0) {
      return filtered
    }
    
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

// Date parsing cache
const dateCache = new Map()

function parseDateCached(dateValue) {
  if (!dateValue) return null
  if (dateCache.has(dateValue)) return dateCache.get(dateValue)
  
  let parsed = null
  try {
    if (typeof dateValue === 'string' && dateValue.includes('/')) {
      const parts = dateValue.split('/')
      if (parts.length === 3) {
        parsed = new Date(parseInt(parts[2]), parseInt(parts[1]) - 1, parseInt(parts[0]))
      } else {
        parsed = new Date(dateValue)
      }
    } else {
      parsed = new Date(dateValue)
    }
    if (isNaN(parsed.getTime())) parsed = null
  } catch {
    parsed = null
  }
  
  if (parsed) dateCache.set(dateValue, parsed)
  return parsed
}

// Bitmask-based filtering
function createFilterMask(columnarData, streamFilters, streamId) {
  const length = columnarData.length
  const mask = new Array(length).fill(true)
  
  // Helper to get filter sets
  const getFilterSets = (filters) => {
    if (!filters) return null
    return {
      excludeDaysOfWeek: filters.exclude_days_of_week?.length > 0 
        ? new Set(filters.exclude_days_of_week) 
        : null,
      excludeDaysOfMonth: filters.exclude_days_of_month?.length > 0 
        ? new Set(filters.exclude_days_of_month) 
        : null,
      includeYears: filters.include_years?.length > 0 
        ? new Set(filters.include_years) 
        : null
    }
  }
  
  const masterFilterSets = getFilterSets(streamFilters['master'])
  const Stream = columnarData.getColumn('Stream')
  const DateColumn = columnarData.getColumn('Date')
  const trade_date = columnarData.getColumn('trade_date')
  
  // Pre-parse dates and extract components
  const dates = new Array(length)
  const dayOfWeeks = new Array(length)
  const dayOfMonths = new Array(length)
  const years = new Array(length)
  
  for (let i = 0; i < length; i++) {
    const dateValue = DateColumn[i] || trade_date[i]
    if (dateValue) {
      const parsed = parseDateCached(dateValue)
      if (parsed) {
        dates[i] = parsed
        dayOfWeeks[i] = parsed.toLocaleDateString('en-US', { weekday: 'long' })
        dayOfMonths[i] = parsed.getDate()
        years[i] = parsed.getFullYear()
      } else {
        dates[i] = null
        dayOfWeeks[i] = null
        dayOfMonths[i] = null
        years[i] = null
      }
    } else {
      dates[i] = null
      dayOfWeeks[i] = null
      dayOfMonths[i] = null
      years[i] = null
    }
  }
  
  // Apply filters
  for (let i = 0; i < length; i++) {
    if (!mask[i]) continue
    
    const rowStream = Stream[i]
    
    // Filter by stream if specified
    if (streamId && streamId !== 'master' && rowStream !== streamId) {
      mask[i] = false
      continue
    }
    
    // Apply individual stream filters (if not master tab)
    if (streamId && streamId !== 'master') {
      const filterSets = getFilterSets(streamFilters[streamId])
      if (filterSets) {
        if (filterSets.excludeDaysOfWeek && dayOfWeeks[i] && filterSets.excludeDaysOfWeek.has(dayOfWeeks[i])) {
          mask[i] = false
          continue
        }
        if (filterSets.excludeDaysOfMonth && dayOfMonths[i] !== null && filterSets.excludeDaysOfMonth.has(dayOfMonths[i])) {
          mask[i] = false
          continue
        }
        if (filterSets.includeYears) {
          if (years[i] === null || !filterSets.includeYears.has(years[i])) {
            mask[i] = false
            continue
          }
        }
      }
    }
    
    // Apply master tab filters (each stream's filters to its own rows)
    if (streamId === 'master' || !streamId) {
      const streamFilterSets = rowStream ? getFilterSets(streamFilters[rowStream]) : null
      
      if (streamFilterSets) {
        if (streamFilterSets.excludeDaysOfWeek && dayOfWeeks[i] && streamFilterSets.excludeDaysOfWeek.has(dayOfWeeks[i])) {
          mask[i] = false
          continue
        }
        if (streamFilterSets.excludeDaysOfMonth && dayOfMonths[i] !== null && streamFilterSets.excludeDaysOfMonth.has(dayOfMonths[i])) {
          mask[i] = false
          continue
        }
        if (streamFilterSets.includeYears) {
          if (years[i] === null || !streamFilterSets.includeYears.has(years[i])) {
            mask[i] = false
            continue
          }
        }
      }
      
      // Apply master-specific filters on top
      if (masterFilterSets) {
        if (masterFilterSets.excludeDaysOfWeek && dayOfWeeks[i] && masterFilterSets.excludeDaysOfWeek.has(dayOfWeeks[i])) {
          mask[i] = false
          continue
        }
        if (masterFilterSets.excludeDaysOfMonth && dayOfMonths[i] !== null && masterFilterSets.excludeDaysOfMonth.has(dayOfMonths[i])) {
          mask[i] = false
          continue
        }
        if (masterFilterSets.includeYears) {
          if (years[i] === null || !masterFilterSets.includeYears.has(years[i])) {
            mask[i] = false
            continue
          }
        }
      }
    }
  }
  
  return mask
}

// Calculate statistics
function calculateStats(columnarData, streamId, contractMultiplier, contractValues) {
  if (columnarData.length === 0) {
    return {
      totalTrades: 0,
      totalProfit: 0,
      totalProfitDollars: 0,
      winRate: 0,
      wins: 0,
      losses: 0,
      breakEven: 0,
      noTrade: 0,
      profitFactor: 0,
      sharpeRatio: 0,
      sortinoRatio: 0,
      calmarRatio: 0,
      maxDrawdown: 0,
      maxDrawdownDollars: 0,
      riskReward: 0,
      meanPnLPerTrade: 0,
      medianPnLPerTrade: 0,
      stdDevPnL: 0,
      maxConsecutiveLosses: 0,
      timeToRecovery: 0,
      monthlyReturnStdDev: 0,
      profitPerDay: 0,
      skewness: 0,
      kurtosis: 0,
      var95: 0,
      cvar95: 0
    }
  }
  
  const Result = columnarData.getColumn('Result')
  const Profit = columnarData.getColumn('Profit')
  const Instrument = columnarData.getColumn('Instrument')
  const DateColumn = columnarData.getColumn('Date')
  const trade_date = columnarData.getColumn('trade_date')
  
  // Filter out NoTrade entries for PnL calculations
  const validTrades = []
  const perTradePnLDollars = []
  
  for (let i = 0; i < columnarData.length; i++) {
    if (Result[i] === 'NoTrade') continue
    
    const profit = parseFloat(Profit[i]) || 0
    const symbol = Instrument[i] || 'ES'
    const baseSymbol = symbol.replace(/\d+$/, '') || symbol
    const contractValue = contractValues[baseSymbol] || 50
    
    validTrades.push(i)
    perTradePnLDollars.push(profit * contractValue * contractMultiplier)
  }
  
  if (validTrades.length === 0) {
    return {
      totalTrades: columnarData.length,
      totalProfit: 0,
      totalProfitDollars: 0,
      winRate: 0,
      wins: 0,
      losses: 0,
      breakEven: 0,
      noTrade: columnarData.length,
      profitFactor: 0,
      sharpeRatio: 0,
      sortinoRatio: 0,
      calmarRatio: 0,
      maxDrawdown: 0,
      maxDrawdownDollars: 0,
      riskReward: 0,
      meanPnLPerTrade: 0,
      medianPnLPerTrade: 0,
      stdDevPnL: 0,
      maxConsecutiveLosses: 0,
      timeToRecovery: 0,
      monthlyReturnStdDev: 0,
      profitPerDay: 0,
      skewness: 0,
      kurtosis: 0,
      var95: 0,
      cvar95: 0
    }
  }
  
  // Basic counts
  let wins = 0, losses = 0, breakEven = 0, noTrade = 0
  let totalProfit = 0
  
  for (let i = 0; i < columnarData.length; i++) {
    const result = Result[i]
    const profit = parseFloat(Profit[i]) || 0
    totalProfit += profit
    
    // Check for BreakEven - could be 'BE' or 'BreakEven'
    if (result === 'Win') wins++
    else if (result === 'Loss') losses++
    else if (result === 'BreakEven' || result === 'BE') breakEven++
    else if (result === 'NoTrade') noTrade++
  }
  
  const totalProfitDollars = perTradePnLDollars.reduce((sum, pnl) => sum + pnl, 0)
  const totalTrades = validTrades.length
  // Win Rate should exclude BreakEven and NoTrade: wins / (wins + losses)
  const winLossTrades = wins + losses
  const winRate = winLossTrades > 0 ? (wins / winLossTrades) * 100 : 0
  
  // Mean and median
  const meanPnL = perTradePnLDollars.reduce((sum, pnl) => sum + pnl, 0) / perTradePnLDollars.length
  const sortedPnL = [...perTradePnLDollars].sort((a, b) => a - b)
  const medianPnL = sortedPnL.length % 2 === 0
    ? (sortedPnL[sortedPnL.length / 2 - 1] + sortedPnL[sortedPnL.length / 2]) / 2
    : sortedPnL[Math.floor(sortedPnL.length / 2)]
  
  // Std Dev
  const variance = perTradePnLDollars.reduce((sum, pnl) => sum + Math.pow(pnl - meanPnL, 2), 0) / (perTradePnLDollars.length - 1)
  const stdDevPnL = Math.sqrt(variance)
  
  // Max consecutive losses
  let maxConsecutiveLosses = 0
  let currentStreak = 0
  for (let i = 0; i < perTradePnLDollars.length; i++) {
    if (perTradePnLDollars[i] < 0) {
      currentStreak++
      maxConsecutiveLosses = Math.max(maxConsecutiveLosses, currentStreak)
    } else {
      currentStreak = 0
    }
  }
  
  // Profit Factor
  const grossProfit = perTradePnLDollars.filter(pnl => pnl > 0).reduce((sum, pnl) => sum + pnl, 0)
  const grossLoss = Math.abs(perTradePnLDollars.filter(pnl => pnl < 0).reduce((sum, pnl) => sum + pnl, 0))
  const profitFactor = grossLoss > 0 ? grossProfit / grossLoss : 0
  
  // Drawdown calculation
  let runningProfit = 0
  let peak = 0
  let maxDrawdown = 0
  let maxDrawdownDollars = 0
  
  for (let i = 0; i < perTradePnLDollars.length; i++) {
    runningProfit += perTradePnLDollars[i]
    peak = Math.max(peak, runningProfit)
    const drawdown = peak - runningProfit
    if (drawdown > maxDrawdown) {
      maxDrawdown = drawdown
      maxDrawdownDollars = drawdown
    }
  }
  
  // Time to recovery (longest drawdown duration in days)
  // Track equity curve, find each peak -> trough -> recovery cycle
  // Return the maximum duration in days
  let maxRecoveryDays = 0
  let equityCurve = []
  runningProfit = 0
  
  // Build equity curve
  for (let i = 0; i < perTradePnLDollars.length; i++) {
    runningProfit += perTradePnLDollars[i]
    equityCurve.push({
      value: runningProfit,
      tradeIdx: i,
      dataIdx: validTrades[i]
    })
  }
  
  // Find all peaks and their subsequent recoveries
  let currentPeak = -Infinity
  let currentPeakIdx = -1
  let currentPeakDate = null
  
  for (let i = 0; i < equityCurve.length; i++) {
    const current = equityCurve[i]
    
    // New peak found
    if (current.value > currentPeak) {
      // If we had a previous peak, check if we recovered from it
      if (currentPeakIdx >= 0 && currentPeakDate) {
        // We've recovered (current value >= previous peak)
        if (current.value >= currentPeak) {
          const currentDateValue = DateColumn[current.dataIdx] || trade_date[current.dataIdx]
          if (currentDateValue) {
            const currentDate = parseDateCached(currentDateValue)
            if (currentDate) {
              const diffTime = currentDate.getTime() - currentPeakDate.getTime()
              const days = Math.ceil(diffTime / (1000 * 60 * 60 * 24))
              maxRecoveryDays = Math.max(maxRecoveryDays, days)
            }
          }
        }
      }
      
      // Update to new peak
      currentPeak = current.value
      currentPeakIdx = i
      const peakDateValue = DateColumn[current.dataIdx] || trade_date[current.dataIdx]
      if (peakDateValue) {
        currentPeakDate = parseDateCached(peakDateValue)
      }
    }
  }
  
  // Check if we're still in a drawdown from the last peak
  if (currentPeakIdx >= 0 && currentPeakDate && equityCurve.length > 0) {
    const lastValue = equityCurve[equityCurve.length - 1].value
    if (lastValue < currentPeak) {
      // Still in drawdown - calculate days from peak to last trade
      const lastDataIdx = equityCurve[equityCurve.length - 1].dataIdx
      const lastDateValue = DateColumn[lastDataIdx] || trade_date[lastDataIdx]
      if (lastDateValue) {
        const lastDate = parseDateCached(lastDateValue)
        if (lastDate) {
          const diffTime = lastDate.getTime() - currentPeakDate.getTime()
          const days = Math.ceil(diffTime / (1000 * 60 * 60 * 24))
          maxRecoveryDays = Math.max(maxRecoveryDays, days)
        }
      }
    }
  }
  
  const timeToRecovery = maxRecoveryDays
  
  // Monthly return std dev
  const monthlyProfits = new Map()
  for (let i = 0; i < validTrades.length; i++) {
    const idx = validTrades[i]
    const dateValue = DateColumn[idx] || trade_date[idx]
    if (dateValue) {
      const parsed = parseDateCached(dateValue)
      if (parsed) {
        const monthKey = `${parsed.getFullYear()}-${String(parsed.getMonth() + 1).padStart(2, '0')}`
        if (!monthlyProfits.has(monthKey)) {
          monthlyProfits.set(monthKey, 0)
        }
        monthlyProfits.set(monthKey, monthlyProfits.get(monthKey) + perTradePnLDollars[i])
      }
    }
  }
  
  const monthlyReturns = Array.from(monthlyProfits.values())
  const meanMonthlyReturn = monthlyReturns.length > 0 
    ? monthlyReturns.reduce((sum, r) => sum + r, 0) / monthlyReturns.length 
    : 0
  const monthlyVariance = monthlyReturns.length > 1
    ? monthlyReturns.reduce((sum, r) => sum + Math.pow(r - meanMonthlyReturn, 2), 0) / (monthlyReturns.length - 1)
    : 0
  const monthlyReturnStdDev = Math.sqrt(monthlyVariance)
  
  // Total Days - count unique dates
  const uniqueDates = new Set()
  for (let i = 0; i < validTrades.length; i++) {
    const idx = validTrades[i]
    const dateValue = DateColumn[idx] || trade_date[idx]
    if (dateValue) {
      const parsed = parseDateCached(dateValue)
      if (parsed) {
        const dayKey = parsed.toISOString().split('T')[0]
        uniqueDates.add(dayKey)
      }
    }
  }
  const totalDays = uniqueDates.size
  const avgTradesPerDay = totalDays > 0 ? validTrades.length / totalDays : 0
  
  // Profit per day - group trades by date
  const dailyProfits = new Map()
  for (let i = 0; i < validTrades.length; i++) {
    const idx = validTrades[i]
    const dateValue = DateColumn[idx] || trade_date[idx]
    if (dateValue) {
      const parsed = parseDateCached(dateValue)
      if (parsed) {
        const dayKey = parsed.toISOString().split('T')[0]
        if (!dailyProfits.has(dayKey)) {
          dailyProfits.set(dayKey, 0)
        }
        dailyProfits.set(dayKey, dailyProfits.get(dayKey) + perTradePnLDollars[i])
      }
    }
  }
  const dailyProfitValues = Array.from(dailyProfits.values())
  const profitPerDay = dailyProfitValues.length > 0
    ? dailyProfitValues.reduce((sum, p) => sum + p, 0) / dailyProfitValues.length
    : 0
  
  // Profit per week - group trades by week of month
  const weeklyProfits = new Map()
  for (let i = 0; i < validTrades.length; i++) {
    const idx = validTrades[i]
    const dateValue = DateColumn[idx] || trade_date[idx]
    if (dateValue) {
      const parsed = parseDateCached(dateValue)
      if (parsed) {
        const day = parsed.getDate()
        // Calculate which week of the month (1-5)
        let weekOfMonth
        if (day <= 7) {
          weekOfMonth = 1
        } else if (day <= 14) {
          weekOfMonth = 2
        } else if (day <= 21) {
          weekOfMonth = 3
        } else if (day <= 28) {
          weekOfMonth = 4
        } else {
          weekOfMonth = 5
        }
        const weekKey = `Week ${weekOfMonth}`
        if (!weeklyProfits.has(weekKey)) {
          weeklyProfits.set(weekKey, 0)
        }
        weeklyProfits.set(weekKey, weeklyProfits.get(weekKey) + perTradePnLDollars[i])
      }
    }
  }
  const weeklyProfitValues = Array.from(weeklyProfits.values())
  const profitPerWeek = weeklyProfitValues.length > 0
    ? weeklyProfitValues.reduce((sum, p) => sum + p, 0) / weeklyProfitValues.length
    : 0
  
  // Profit per month - group trades by month
  const monthlyProfitsForAvg = new Map()
  for (let i = 0; i < validTrades.length; i++) {
    const idx = validTrades[i]
    const dateValue = DateColumn[idx] || trade_date[idx]
    if (dateValue) {
      const parsed = parseDateCached(dateValue)
      if (parsed) {
        const year = parsed.getFullYear()
        const month = parsed.getMonth() + 1
        const monthKey = `${year}-${String(month).padStart(2, '0')}`
        if (!monthlyProfitsForAvg.has(monthKey)) {
          monthlyProfitsForAvg.set(monthKey, 0)
        }
        monthlyProfitsForAvg.set(monthKey, monthlyProfitsForAvg.get(monthKey) + perTradePnLDollars[i])
      }
    }
  }
  const monthlyProfitValues = Array.from(monthlyProfitsForAvg.values())
  const profitPerMonth = monthlyProfitValues.length > 0
    ? monthlyProfitValues.reduce((sum, p) => sum + p, 0) / monthlyProfitValues.length
    : 0
  
  // Profit per year - group trades by year
  const yearlyProfits = new Map()
  for (let i = 0; i < validTrades.length; i++) {
    const idx = validTrades[i]
    const dateValue = DateColumn[idx] || trade_date[idx]
    if (dateValue) {
      const parsed = parseDateCached(dateValue)
      if (parsed) {
        const year = parsed.getFullYear().toString()
        if (!yearlyProfits.has(year)) {
          yearlyProfits.set(year, 0)
        }
        yearlyProfits.set(year, yearlyProfits.get(year) + perTradePnLDollars[i])
      }
    }
  }
  const yearlyProfitValues = Array.from(yearlyProfits.values())
  const profitPerYear = yearlyProfitValues.length > 0
    ? yearlyProfitValues.reduce((sum, p) => sum + p, 0) / yearlyProfitValues.length
    : 0
  
  // Calculate daily returns statistics for Sharpe/Sortino
  const meanDailyReturn = profitPerDay
  const dailyReturnsVariance = dailyProfitValues.length > 1
    ? dailyProfitValues.reduce((sum, r) => sum + Math.pow(r - meanDailyReturn, 2), 0) / (dailyProfitValues.length - 1)
    : 0
  const dailyReturnsStdDev = Math.sqrt(dailyReturnsVariance)
  
  // Downside returns (negative daily returns) for Sortino
  const downsideDailyReturns = dailyProfitValues.filter(r => r < 0)
  const downsideDailyVariance = downsideDailyReturns.length > 1
    ? downsideDailyReturns.reduce((sum, r) => sum + Math.pow(r, 2), 0) / (downsideDailyReturns.length - 1)
    : 0
  const downsideDailyStdDev = Math.sqrt(downsideDailyVariance)
  
  // Skewness and Kurtosis
  const n = perTradePnLDollars.length
  const mean = meanPnL
  const std = stdDevPnL
  
  if (std > 0 && n > 2) {
    let skewSum = 0
    let kurtSum = 0
    for (let i = 0; i < n; i++) {
      const z = (perTradePnLDollars[i] - mean) / std
      skewSum += Math.pow(z, 3)
      kurtSum += Math.pow(z, 4)
    }
    const skewness = (n / ((n - 1) * (n - 2))) * skewSum
    const kurtosis = ((n * (n + 1)) / ((n - 1) * (n - 2) * (n - 3))) * kurtSum - 3 * ((n - 1) * (n - 1)) / ((n - 2) * (n - 3))
    
    // VaR and CVaR (95%)
    const var95Idx = Math.floor(n * 0.05)
    const var95 = sortedPnL[var95Idx] || 0
    const cvar95Values = sortedPnL.slice(0, var95Idx + 1)
    const cvar95 = cvar95Values.length > 0
      ? cvar95Values.reduce((sum, v) => sum + v, 0) / cvar95Values.length
      : 0
    
    // Sharpe, Sortino, Calmar - use daily returns, not per-trade
    const tradingDaysPerYear = 252
    // Annualized return: mean daily return * trading days per year
    const annualizedReturn = meanDailyReturn * tradingDaysPerYear
    // Annualized volatility: daily std dev * sqrt(trading days per year)
    const annualizedVolatility = dailyReturnsStdDev > 0 ? dailyReturnsStdDev * Math.sqrt(tradingDaysPerYear) : 0
    // Sharpe Ratio: annualized return / annualized volatility (risk-free rate = 0)
    const sharpeRatio = annualizedVolatility > 0 ? annualizedReturn / annualizedVolatility : 0
    
    // Sortino Ratio: annualized return / annualized downside volatility
    const annualizedDownsideVol = downsideDailyStdDev > 0 ? downsideDailyStdDev * Math.sqrt(tradingDaysPerYear) : 0
    const sortinoRatio = annualizedDownsideVol > 0 ? annualizedReturn / annualizedDownsideVol : 0
    
    // Calmar Ratio: annual return / max drawdown
    // Annual return = (total profit / total days) * trading days per year
    const annualReturn = totalDays > 0 ? (totalProfitDollars / totalDays) * tradingDaysPerYear : 0
    const calmarRatio = maxDrawdownDollars > 0 ? annualReturn / maxDrawdownDollars : 0
    
    // Risk-Reward
    const avgWin = perTradePnLDollars.filter(pnl => pnl > 0).reduce((sum, pnl) => sum + pnl, 0) / wins || 0
    const avgLoss = Math.abs(perTradePnLDollars.filter(pnl => pnl < 0).reduce((sum, pnl) => sum + pnl, 0) / losses) || 0
    const riskReward = avgLoss > 0 ? avgWin / avgLoss : 0
    
    return {
      totalTrades,
      totalProfit,
      totalProfitDollars,
      totalDays,
      avgTradesPerDay,
      winRate,
      wins,
      losses,
      breakEven,
      noTrade,
      profitFactor,
      sharpeRatio,
      sortinoRatio,
      calmarRatio,
      maxDrawdown: maxDrawdownDollars,
      maxDrawdownDollars,
      riskReward,
      meanPnLPerTrade: meanPnL,
      medianPnLPerTrade: medianPnL,
      stdDevPnL,
      maxConsecutiveLosses,
      timeToRecovery,
      monthlyReturnStdDev,
      profitPerDay,
      profitPerWeek,
      profitPerMonth,
      profitPerYear,
      skewness,
      kurtosis,
      var95,
      cvar95
    }
  }
  
  // Fallback for small datasets
  return {
    totalTrades,
    totalProfit,
    totalProfitDollars,
    totalDays,
    avgTradesPerDay,
    winRate,
    wins,
    losses,
    breakEven,
    noTrade,
    profitFactor,
    sharpeRatio: 0,
    sortinoRatio: 0,
    calmarRatio: 0,
    maxDrawdown: maxDrawdownDollars,
    maxDrawdownDollars,
    riskReward: 0,
    meanPnLPerTrade: meanPnL,
    medianPnLPerTrade: medianPnL,
    stdDevPnL,
    maxConsecutiveLosses,
    timeToRecovery,
      monthlyReturnStdDev,
      profitPerDay,
      profitPerWeek,
      profitPerMonth,
      profitPerYear,
      skewness: 0,
      kurtosis: 0,
      var95: 0,
      cvar95: 0
    }
}

// Calculate profit breakdowns (Time, Day, DOM, Month, Year)
function calculateProfitBreakdown(columnarData, contractMultiplier, contractValues, breakdownType) {
  const Profit = columnarData.getColumn('Profit')
  const Stream = columnarData.getColumn('Stream')
  const Instrument = columnarData.getColumn('Instrument')
  const Time = columnarData.getColumn('Time')
  const DateColumn = columnarData.getColumn('Date')
  const trade_date = columnarData.getColumn('trade_date')
  
  const result = {}
  
  for (let i = 0; i < columnarData.length; i++) {
    const profit = parseFloat(Profit[i]) || 0
    const stream = Stream[i] || 'Unknown'
    const symbol = Instrument[i] || 'ES'
    const baseSymbol = symbol.replace(/\d+$/, '') || symbol
    const contractValue = contractValues[baseSymbol] || 50
    const profitDollars = profit * contractValue * contractMultiplier
    
    let key = null
    
    if (breakdownType === 'time') {
      const time = Time[i]
      if (!time || time === 'NA' || time === '00:00') continue
      let timeKey = time.toString().trim()
      if (!/^\d{2}:\d{2}$/.test(timeKey)) {
        const match = timeKey.match(/(\d{2}:\d{2})/)
        if (match) timeKey = match[1]
        else continue
      }
      key = timeKey
    } else if (breakdownType === 'day') {
      const dateValue = DateColumn[i] || trade_date[i]
      if (!dateValue) continue
      const parsed = parseDateCached(dateValue)
      if (!parsed) continue
      const dow = parsed.toLocaleDateString('en-US', { weekday: 'long' })
      const dowOrder = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
      if (!dowOrder.includes(dow)) continue
      key = dow
    } else if (breakdownType === 'dom') {
      const dateValue = DateColumn[i] || trade_date[i]
      if (!dateValue) continue
      const parsed = parseDateCached(dateValue)
      if (!parsed) continue
      key = parsed.getDate().toString()
    } else if (breakdownType === 'week') {
      const dateValue = DateColumn[i] || trade_date[i]
      if (!dateValue) continue
      const parsed = parseDateCached(dateValue)
      if (!parsed) continue
      const day = parsed.getDate()
      // Calculate which week of the month (1-5): 
      // Week 1: days 1-7
      // Week 2: days 8-14
      // Week 3: days 15-21
      // Week 4: days 22-28
      // Week 5: days 29-31 (all remaining days)
      // Consolidate all months into just 5 weeks
      let weekOfMonth
      if (day <= 7) {
        weekOfMonth = 1
      } else if (day <= 14) {
        weekOfMonth = 2
      } else if (day <= 21) {
        weekOfMonth = 3
      } else if (day <= 28) {
        weekOfMonth = 4
      } else {
        weekOfMonth = 5
      }
      key = `Week ${weekOfMonth}`
    } else if (breakdownType === 'month') {
      const dateValue = DateColumn[i] || trade_date[i]
      if (!dateValue) continue
      const parsed = parseDateCached(dateValue)
      if (!parsed) continue
      const year = parsed.getFullYear()
      const month = parsed.getMonth() + 1
      key = `${year}-${String(month).padStart(2, '0')}`
    } else if (breakdownType === 'year') {
      const dateValue = DateColumn[i] || trade_date[i]
      if (!dateValue) continue
      const parsed = parseDateCached(dateValue)
      if (!parsed) continue
      key = parsed.getFullYear().toString()
    }
    
    if (!key) continue
    
    if (!result[key]) result[key] = {}
    if (!result[key][stream]) result[key][stream] = 0
    result[key][stream] += profitDollars
  }
  
  // Sort results appropriately
  if (breakdownType === 'time') {
    const sorted = {}
    Object.keys(result).sort((a, b) => {
      const [aHour, aMin] = a.split(':').map(Number)
      const [bHour, bMin] = b.split(':').map(Number)
      if (aHour !== bHour) return aHour - bHour
      return aMin - bMin
    }).forEach(key => { sorted[key] = result[key] })
    return sorted
  } else if (breakdownType === 'day') {
    const dowOrder = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
    const sorted = {}
    dowOrder.forEach(dow => {
      if (result[dow]) sorted[dow] = result[dow]
    })
    return sorted
  } else if (breakdownType === 'dom') {
    const sorted = {}
    for (let day = 1; day <= 31; day++) {
      if (result[day.toString()]) sorted[day.toString()] = result[day.toString()]
    }
    return sorted
  } else if (breakdownType === 'week') {
    // Sort by week number (Week 1, Week 2, Week 3, Week 4, Week 5)
    const sorted = {}
    const weekOrder = ['Week 1', 'Week 2', 'Week 3', 'Week 4', 'Week 5']
    weekOrder.forEach(week => {
      if (result[week]) sorted[week] = result[week]
    })
    return sorted
  }
  
  return result
}

// Calculate timetable: most recent trade per stream with time changes applied
// Filters rows based on current trading day and each stream's filters
function calculateTimetable(columnarData, streamFilters, currentTradingDay) {
  if (!columnarData || columnarData.length === 0) {
    return []
  }
  
  // Get columns
  const Stream = columnarData.getColumn('Stream')
  const DateColumn = columnarData.getColumn('Date')
  const trade_date = columnarData.getColumn('trade_date')
  const Time = columnarData.getColumn('Time')
  const TimeChange = columnarData.getColumn('Time Change')
  
  // Find most recent trade per stream (across ALL data, no filters)
  const streamMap = new Map() // stream -> { index, date, time }
  
  for (let i = 0; i < columnarData.length; i++) {
    const stream = Stream[i]
    if (!stream) continue
    
    const dateValue = DateColumn[i] || trade_date[i]
    if (!dateValue) continue
    
    const parsedDate = parseDateCached(dateValue)
    if (!parsedDate) continue
    
    const existing = streamMap.get(stream)
    if (!existing) {
      streamMap.set(stream, { index: i, date: parsedDate, time: Time[i] || '' })
    } else {
      // Compare dates (newer is better)
      if (parsedDate > existing.date) {
        streamMap.set(stream, { index: i, date: parsedDate, time: Time[i] || '' })
      } else if (parsedDate.getTime() === existing.date.getTime()) {
        // Same date, compare times
        const tradeTime = Time[i] || ''
        if (tradeTime > existing.time) {
          streamMap.set(stream, { index: i, date: parsedDate, time: tradeTime })
        }
      }
    }
  }
  
  if (streamMap.size === 0) {
    return []
  }
  
  // Parse current trading day to check filters
  let tradingDayDate = null
  if (currentTradingDay) {
    tradingDayDate = typeof currentTradingDay === 'string' ? new Date(currentTradingDay) : currentTradingDay
  }
  
  // Build timetable: extract time (prefer Time Change if available) and filter by current trading day
  const timetable = []
  for (const [stream, info] of streamMap.entries()) {
    // Check if this stream would filter out the current trading day
    if (tradingDayDate) {
      const streamFilterData = streamFilters[stream] || {}
      const dayOfWeek = tradingDayDate.toLocaleDateString('en-US', { weekday: 'long' })
      const dayOfMonth = tradingDayDate.getDate()
      
      // Check day of week filter
      if (streamFilterData.exclude_days_of_week && 
          Array.isArray(streamFilterData.exclude_days_of_week) &&
          streamFilterData.exclude_days_of_week.includes(dayOfWeek)) {
        continue // Skip this stream - current trading day is filtered out
      }
      
      // Check day of month filter
      if (streamFilterData.exclude_days_of_month && 
          Array.isArray(streamFilterData.exclude_days_of_month) &&
          streamFilterData.exclude_days_of_month.includes(dayOfMonth)) {
        continue // Skip this stream - current trading day is filtered out
      }
    }
    
    let effectiveTime = info.time || ''
    
    // Check if Time Change exists
    try {
      const timeChange = TimeChange && TimeChange[info.index] !== undefined ? TimeChange[info.index] : null
      if (timeChange && typeof timeChange === 'string' && timeChange.trim() !== '') {
        const match = timeChange.match(/→\s*(\d{2}:\d{2})/)
        if (match) {
          effectiveTime = match[1]
        }
      }
    } catch (err) {
      // If Time Change column doesn't exist, just use original time
    }
    
    // Format date and calculate DOW
    const dateStr = info.date.toISOString().split('T')[0]
    const dow = info.date.toLocaleDateString('en-US', { weekday: 'short' }).toUpperCase()
    
    timetable.push({
      Date: dateStr,
      DOW: dow,
      Stream: stream,
      Time: effectiveTime
    })
  }
  
  // Sort by time (latest first)
  timetable.sort((a, b) => {
    const timeA = a.Time || '00:00'
    const timeB = b.Time || '00:00'
    const [hoursA, minsA] = timeA.split(':').map(Number)
    const [hoursB, minsB] = timeB.split(':').map(Number)
    const totalMinsA = (hoursA || 0) * 60 + (minsA || 0)
    const totalMinsB = (hoursB || 0) * 60 + (minsB || 0)
    return totalMinsB - totalMinsA // Latest first
  })
  
  return timetable
}

// Worker message handler
self.onmessage = function(e) {
  const { type, payload } = e.data
  
  try {
    switch (type) {
      case 'INIT_DATA': {
        const { data } = payload
        const columnarData = new ColumnarData(data)
        self.columnarData = columnarData
        self.postMessage({ type: 'DATA_INITIALIZED', payload: { length: columnarData.length } })
        break
      }
      
      case 'FILTER': {
        if (!self.columnarData) {
          self.postMessage({ type: 'ERROR', payload: { message: 'Data not initialized' } })
          return
        }
        
        const { streamFilters, streamId, returnRows = false, sortIndices = null } = payload
        const mask = createFilterMask(self.columnarData, streamFilters, streamId)
        const filtered = self.columnarData.filter(mask)
        
        // Get filtered indices
        const filteredIndices = []
        for (let i = 0; i < mask.length; i++) {
          if (mask[i]) filteredIndices.push(i)
        }
        
        // Sort indices if requested (for table display)
        if (sortIndices && filteredIndices.length > 0) {
          filteredIndices.sort((a, b) => {
            // Sort by date (newest first), then time (latest first)
            const dateA = self.columnarData.getColumn('Date')[a] || self.columnarData.getColumn('trade_date')[a]
            const dateB = self.columnarData.getColumn('Date')[b] || self.columnarData.getColumn('trade_date')[b]
            
            const parsedA = parseDateCached(dateA)
            const parsedB = parseDateCached(dateB)
            
            if (parsedA && parsedB) {
              const dateDiff = parsedB.getTime() - parsedA.getTime()
              if (dateDiff !== 0) return dateDiff
              
              // Then by time
              const timeA = self.columnarData.getColumn('Time')[a] || ''
              const timeB = self.columnarData.getColumn('Time')[b] || ''
              if (timeA && timeB) {
                const [hA, mA] = timeA.split(':').map(Number)
                const [hB, mB] = timeB.split(':').map(Number)
                const minsA = (hA || 0) * 60 + (mA || 0)
                const minsB = (hB || 0) * 60 + (mB || 0)
                if (minsA !== minsB) return minsB - minsA // Latest first
              }
            }
            return 0
          })
        }
        
        const response = {
          length: filtered.length,
          mask: mask,
          indices: filteredIndices
        }
        
        // Optionally return rows (for initial render)
        if (returnRows && filteredIndices.length > 0) {
          const maxRows = Math.min(filteredIndices.length, 100) // Return first 100 for initial render
          response.rows = self.columnarData.getRows(filteredIndices.slice(0, maxRows))
        }
        
        self.postMessage({ 
          type: 'FILTERED', 
          payload: response
        })
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
        
        const { streamFilters, streamId, contractMultiplier, contractValues } = payload
        const mask = createFilterMask(self.columnarData, streamFilters, streamId)
        const filtered = self.columnarData.filter(mask)
        const stats = calculateStats(filtered, streamId, contractMultiplier, contractValues)
        
        self.postMessage({ type: 'STATS', payload: { stats } })
        break
      }
      
      case 'CALCULATE_PROFIT_BREAKDOWN': {
        if (!self.columnarData) {
          self.postMessage({ type: 'ERROR', payload: { message: 'Data not initialized' } })
          return
        }
        
        const { streamFilters, streamId, contractMultiplier, contractValues, breakdownType, useFiltered } = payload
        
        let dataToUse = self.columnarData
        if (useFiltered) {
          const mask = createFilterMask(self.columnarData, streamFilters, streamId)
          dataToUse = self.columnarData.filter(mask)
        }
        
        // Extract the base breakdown type (remove _before or _after suffix)
        const baseBreakdownType = breakdownType.replace(/_before$|_after$/, '')
        const breakdown = calculateProfitBreakdown(dataToUse, contractMultiplier, contractValues, baseBreakdownType)
        // Send back with the full breakdownType (including _before or _after)
        self.postMessage({ type: 'PROFIT_BREAKDOWN', payload: { breakdown, breakdownType } })
        break
      }
      
      case 'CALCULATE_TIMETABLE': {
        if (!self.columnarData || self.columnarData.length === 0) {
          self.postMessage({ type: 'TIMETABLE', payload: { timetable: [] } })
          return
        }
        
        try {
          const { streamFilters, currentTradingDay } = payload
          // Ensure we have valid filters object
          const filters = streamFilters || {}
          const timetable = calculateTimetable(self.columnarData, filters, currentTradingDay)
          self.postMessage({ type: 'TIMETABLE', payload: { timetable } })
        } catch (err) {
          console.error('Timetable calculation error:', err)
          self.postMessage({ type: 'TIMETABLE', payload: { timetable: [] } })
          self.postMessage({ type: 'ERROR', payload: { message: `Timetable calculation error: ${err.message}`, stack: err.stack } })
        }
        break
      }
      
      default:
        self.postMessage({ type: 'ERROR', payload: { message: `Unknown message type: ${type}` } })
    }
  } catch (error) {
    self.postMessage({ type: 'ERROR', payload: { message: error.message, stack: error.stack } })
  }
}

