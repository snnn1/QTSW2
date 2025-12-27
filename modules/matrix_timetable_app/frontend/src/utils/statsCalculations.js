/**
 * Statistics calculation utilities
 * Complex calculations for trading statistics
 */

import { getContractValue } from './profitCalculations'

/**
 * Format currency value
 */
const formatCurrency = (value) => {
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 0,
    maximumFractionDigits: 0
  }).format(value)
}

/**
 * Normalize result string to uppercase
 */
const normalizeResult = (result) => {
  if (!result) return ''
  return result.toString().trim().toUpperCase()
}

/**
 * Check if result is an executed trade (WIN, LOSS, BE, BREAKEVEN)
 * Excludes NoTrade and TIME from executed trades
 */
const isExecutedTrade = (resultNorm) => {
  return resultNorm === 'WIN' || 
         resultNorm === 'LOSS' || 
         resultNorm === 'BE' || 
         resultNorm === 'BREAKEVEN'
}

/**
 * Check if result is NoTrade
 */
const isNoTrade = (resultNorm) => {
  return resultNorm === 'NOTRADE'
}

/**
 * Calculate comprehensive statistics for a stream
 * @param {Array} filteredData - Filtered trade data
 * @param {string} streamId - Stream identifier ('master' or stream name)
 * @param {number} contractMultiplier - Contract multiplier for master stream
 * @returns {Object|null} Statistics object or null if no data
 */
export const calculateStats = (filteredData, streamId, contractMultiplier = 1) => {
  if (!filteredData || filteredData.length === 0) {
    return null
  }
  
  const filtered = filteredData
  
  // Normalize results and separate executed trades from NoTrade and TIME
  const executedTrades = []
  let noTradeCount = 0
  let timeCount = 0
  
  filtered.forEach(trade => {
    const resultNorm = normalizeResult(trade.Result)
    if (isNoTrade(resultNorm)) {
      noTradeCount++
    } else if (resultNorm === 'TIME') {
      timeCount++
      // TIME is excluded from executed trades
    } else if (isExecutedTrade(resultNorm)) {
      executedTrades.push(trade)
    }
    // Note: Any other results are excluded from both counts
  })
  
  // totalTrades = only executed trades (excludes NoTrade and TIME)
  const totalTrades = executedTrades.length
  
  // Count wins, losses, break-even from executed trades only
  const wins = executedTrades.filter(t => normalizeResult(t.Result) === 'WIN').length
  const losses = executedTrades.filter(t => normalizeResult(t.Result) === 'LOSS').length
  const breakEven = executedTrades.filter(t => {
    const norm = normalizeResult(t.Result)
    return norm === 'BE' || norm === 'BREAKEVEN'
  }).length
  
  const winLossTrades = wins + losses
  const winRate = winLossTrades > 0 ? (wins / winLossTrades * 100) : 0
  
  // Calculate profit only from executed trades (excludes NoTrade)
  const totalProfit = executedTrades.reduce((sum, t) => sum + (parseFloat(t.Profit) || 0), 0)
  const avgProfit = totalTrades > 0 ? totalProfit / totalTrades : 0
  
  const winningTrades = executedTrades.filter(t => normalizeResult(t.Result) === 'WIN')
  const losingTrades = executedTrades.filter(t => normalizeResult(t.Result) === 'LOSS')
  const avgWin = winningTrades.length > 0 
    ? winningTrades.reduce((sum, t) => sum + (parseFloat(t.Profit) || 0), 0) / winningTrades.length 
    : 0
  const avgLoss = losingTrades.length > 0 
    ? Math.abs(losingTrades.reduce((sum, t) => sum + (parseFloat(t.Profit) || 0), 0) / losingTrades.length)
    : 0
  const rrRatio = avgLoss > 0 ? avgWin / avgLoss : (avgWin > 0 ? Infinity : 0)
  
  // Allowed/blocked trades count only executed trades
  const allowedTrades = executedTrades.filter(t => t.final_allowed !== false).length
  const blockedTrades = totalTrades - allowedTrades
  
  // Total Days - count unique dates from all rows (including NoTrade for calendar days)
  const uniqueDates = new Set(filtered.map(t => {
    const date = t.Date || t.trade_date
    if (!date) return null
    try {
      if (typeof date === 'string' && date.includes('/')) {
        return date.split(' ')[0]
      }
      const d = new Date(date)
      return isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
    } catch {
      return null
    }
  }).filter(d => d !== null))
  const totalDays = uniqueDates.size
  
  // Calculate total profit in dollars (only from executed trades, excludes NoTrade)
  const totalProfitDollars = executedTrades.reduce((sum, t) => {
    const profit = parseFloat(t.Profit) || 0
    const contractValue = getContractValue(t)
    return sum + (profit * contractValue * contractMultiplier)
  }, 0)
  
  // Sort executed trades by date and time for chronological calculations
  const sortedByDate = [...executedTrades].sort((a, b) => {
    const dateA = new Date(a.Date || a.trade_date)
    const dateB = new Date(b.Date || b.trade_date)
    const dateDiff = dateA.getTime() - dateB.getTime()
    if (dateDiff !== 0) return dateDiff
    const timeA = (a.Time || '').toString()
    const timeB = (b.Time || '').toString()
    return timeA.localeCompare(timeB)
  })
  
  // Time Changes and Final Time (only executed trades, excludes NoTrade)
  let timeChanges = 0
  let lastTime = null
  let finalTime = null
  
  sortedByDate.forEach(trade => {
    // Only include executed trades in time change tracking
    if (!isExecutedTrade(normalizeResult(trade.Result))) return
    
    const currentTime = trade.Time
    if (currentTime && currentTime !== 'NA' && currentTime !== '00:00') {
      if (lastTime !== null && currentTime !== lastTime) {
        timeChanges++
      }
      lastTime = currentTime
      finalTime = currentTime
    }
  })
  
  // Profit Factor
  const grossProfitDollars = winningTrades.reduce((sum, t) => {
    const profit = Math.max(0, parseFloat(t.Profit) || 0)
    return sum + (profit * getContractValue(t) * contractMultiplier)
  }, 0)
  const grossLossDollars = Math.abs(losingTrades.reduce((sum, t) => {
    const profit = Math.min(0, parseFloat(t.Profit) || 0)
    return sum + (profit * getContractValue(t) * contractMultiplier)
  }, 0))
  const profitFactor = grossLossDollars > 0 ? grossProfitDollars / grossLossDollars : (grossProfitDollars > 0 ? Infinity : 0)
  
  // Rolling Drawdown calculation (only executed trades, excludes NoTrade and TIME)
  let runningProfitDollars = 0
  let peakDollars = 0
  let maxDrawdownDollars = 0
  
  sortedByDate.forEach(trade => {
    // Only include executed trades in drawdown calculation
    const resultNorm = normalizeResult(trade.Result)
    if (!isExecutedTrade(resultNorm)) return
    
    const profit = parseFloat(trade.Profit) || 0
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    
    runningProfitDollars += profitDollars
    
    if (runningProfitDollars > peakDollars) {
      peakDollars = runningProfitDollars
    }
    
    const currentDrawdown = runningProfitDollars - peakDollars
    if (currentDrawdown < maxDrawdownDollars) {
      maxDrawdownDollars = currentDrawdown
    }
  })
  
  const maxDrawdownDollarsPositive = Math.abs(maxDrawdownDollars)
  const maxDrawdown = maxDrawdownDollarsPositive > 0 ? maxDrawdownDollarsPositive / 50 : 0
  
  // Sharpe Ratio calculation (only executed trades, excludes NoTrade and TIME)
  const tradingDaysPerYear = 252
  let dailyReturnsDollars = []
  
  if (streamId === 'master') {
    const tradesByDate = new Map()
    executedTrades.forEach(trade => {
      // Only include executed trades in Sharpe calculation
      const resultNorm = normalizeResult(trade.Result)
      if (!isExecutedTrade(resultNorm)) return
      
      const dateValue = trade.Date || trade.trade_date
      if (!dateValue) return
      
      try {
        let dateKey
        if (typeof dateValue === 'string') {
          if (dateValue.includes('/')) {
            dateKey = dateValue.split(' ')[0]
          } else if (dateValue.includes('-')) {
            dateKey = dateValue.split(' ')[0].split('T')[0]
          } else {
            const d = new Date(dateValue)
            dateKey = isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
          }
        } else {
          const d = new Date(dateValue)
          dateKey = isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
        }
        
        if (dateKey) {
          if (!tradesByDate.has(dateKey)) {
            tradesByDate.set(dateKey, 0)
          }
          const profit = parseFloat(trade.Profit) || 0
          const contractValue = getContractValue(trade)
          tradesByDate.set(dateKey, tradesByDate.get(dateKey) + (profit * contractValue * contractMultiplier))
        }
      } catch {
        // Skip invalid dates
      }
    })
    
    dailyReturnsDollars = Array.from(tradesByDate.values())
  } else {
    // Individual stream - only executed trades
    dailyReturnsDollars = executedTrades
      .filter(t => isExecutedTrade(normalizeResult(t.Result)))
      .map(t => {
        const profit = parseFloat(t.Profit) || 0
        return profit * getContractValue(t) * contractMultiplier
      })
  }
  
  const meanDailyReturnDollars = dailyReturnsDollars.length > 0 
    ? dailyReturnsDollars.reduce((sum, r) => sum + r, 0) / dailyReturnsDollars.length 
    : 0
  const varianceDollars = dailyReturnsDollars.length > 1 
    ? dailyReturnsDollars.reduce((sum, r) => sum + Math.pow(r - meanDailyReturnDollars, 2), 0) / (dailyReturnsDollars.length - 1)
    : 0
  const stdDevDollars = Math.sqrt(varianceDollars)
  
  const annualizedReturn = meanDailyReturnDollars * tradingDaysPerYear
  const annualizedVolatility = stdDevDollars * Math.sqrt(tradingDaysPerYear)
  const sharpeRatio = annualizedVolatility > 0 ? annualizedReturn / annualizedVolatility : 0
  
  // Sortino Ratio
  const downsideReturnsDollars = dailyReturnsDollars.filter(r => r < 0)
  const downsideVarianceDollars = downsideReturnsDollars.length > 1
    ? downsideReturnsDollars.reduce((sum, r) => sum + Math.pow(r, 2), 0) / (downsideReturnsDollars.length - 1)
    : 0
  const downsideDevDollars = Math.sqrt(downsideVarianceDollars)
  const annualizedDownsideVolatility = downsideDevDollars * Math.sqrt(tradingDaysPerYear)
  const sortinoRatio = annualizedDownsideVolatility > 0 ? annualizedReturn / annualizedDownsideVolatility : 0
  
  // Calmar Ratio
  const annualReturnDollars = totalDays > 0 ? (totalProfitDollars / totalDays) * tradingDaysPerYear : 0
  const calmarRatio = maxDrawdownDollarsPositive > 0 ? annualReturnDollars / maxDrawdownDollarsPositive : 0
  
  // Best and worst trades (only executed trades, excludes NoTrade and TIME)
  const bestTrade = executedTrades.length > 0 
    ? executedTrades.reduce((best, t) => {
        const profit = parseFloat(t.Profit) || 0
        return profit > (parseFloat(best.Profit) || 0) ? t : best
      }, executedTrades[0])
    : null
  const worstTrade = executedTrades.length > 0
    ? executedTrades.reduce((worst, t) => {
        const profit = parseFloat(t.Profit) || 0
        return profit < (parseFloat(worst.Profit) || 0) ? t : worst
      }, executedTrades[0])
    : null
  
  const avgTradesPerDay = totalDays > 0 ? totalTrades / totalDays : 0
  
  // Per-trade PnL calculations (only executed trades, excludes NoTrade and TIME)
  const perTradePnLDollars = sortedByDate
    .filter(trade => isExecutedTrade(normalizeResult(trade.Result)))
    .map(trade => {
      const profit = parseFloat(trade.Profit) || 0
      const contractValue = getContractValue(trade)
      return profit * contractValue * contractMultiplier
    })
  
  const meanPnL = perTradePnLDollars.length > 0
    ? perTradePnLDollars.reduce((sum, pnl) => sum + pnl, 0) / perTradePnLDollars.length
    : 0
  const variancePnL = perTradePnLDollars.length > 1
    ? perTradePnLDollars.reduce((sum, pnl) => sum + Math.pow(pnl - meanPnL, 2), 0) / (perTradePnLDollars.length - 1)
    : 0
  const stdDevPnL = Math.sqrt(variancePnL)
  
  // Max Consecutive Losses
  let maxConsecutiveLosses = 0
  let currentConsecutiveLosses = 0
  perTradePnLDollars.forEach(pnl => {
    if (pnl < 0) {
      currentConsecutiveLosses++
      maxConsecutiveLosses = Math.max(maxConsecutiveLosses, currentConsecutiveLosses)
    } else {
      currentConsecutiveLosses = 0
    }
  })
  
  const profitPerTrade = meanPnL
  
  // Rolling 30-Day Win Rate (for individual streams, only executed trades)
  let rolling30DayWinRate = null
  if (streamId !== 'master') {
    const tradesByDateMap = new Map()
    sortedByDate.forEach(trade => {
      // Only include executed trades
      if (!isExecutedTrade(normalizeResult(trade.Result))) return
      const dateValue = trade.Date || trade.trade_date
      if (!dateValue) return
      
      try {
        let dateKey
        if (typeof dateValue === 'string' && dateValue.includes('/')) {
          const parts = dateValue.split(' ')[0].split('/')
          if (parts.length === 3) {
            const dateObj = new Date(parseInt(parts[2]), parseInt(parts[1]) - 1, parseInt(parts[0]))
            if (!isNaN(dateObj.getTime())) {
              dateKey = dateObj.toISOString().split('T')[0]
            }
          }
        } else if (typeof dateValue === 'string' && dateValue.includes('-')) {
          dateKey = dateValue.split(' ')[0].split('T')[0]
        } else {
          const d = new Date(dateValue)
          dateKey = isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
        }
        
        if (dateKey) {
          if (!tradesByDateMap.has(dateKey)) {
            tradesByDateMap.set(dateKey, [])
          }
          tradesByDateMap.get(dateKey).push(trade)
        }
      } catch {
        // Skip invalid dates
      }
    })
    
    const uniqueDates = Array.from(tradesByDateMap.keys()).sort()
    
    if (uniqueDates.length > 0) {
      const mostRecentDate = new Date(uniqueDates[uniqueDates.length - 1])
      const thirtyDaysAgo = new Date(mostRecentDate)
      thirtyDaysAgo.setDate(thirtyDaysAgo.getDate() - 30)
      
      const recent30DayTrades = []
      uniqueDates.forEach(dateKey => {
        const dateObj = new Date(dateKey)
        if (dateObj >= thirtyDaysAgo) {
          recent30DayTrades.push(...tradesByDateMap.get(dateKey))
        }
      })
      
      if (recent30DayTrades.length > 0) {
        const recentWins = recent30DayTrades.filter(t => normalizeResult(t.Result) === 'WIN').length
        const recentLosses = recent30DayTrades.filter(t => normalizeResult(t.Result) === 'LOSS').length
        const recentWinLossTrades = recentWins + recentLosses
        rolling30DayWinRate = recentWinLossTrades > 0 ? (recentWins / recentWinLossTrades * 100) : 0
      }
    }
  }
  
  // Master stream only statistics
  const meanPnLPerTrade = profitPerTrade
  
  // Median PnL
  const validPnL = perTradePnLDollars.filter(pnl => !isNaN(pnl) && isFinite(pnl))
  const sortedPnLForMedian = [...validPnL].sort((a, b) => a - b)
  let medianPnLPerTrade = 0
  if (sortedPnLForMedian.length > 0) {
    const midIndex = Math.floor(sortedPnLForMedian.length / 2)
    if (sortedPnLForMedian.length % 2 === 0) {
      const mid1 = sortedPnLForMedian[midIndex - 1]
      const mid2 = sortedPnLForMedian[midIndex]
      medianPnLPerTrade = (mid1 + mid2) / 2
    } else {
      medianPnLPerTrade = sortedPnLForMedian[midIndex]
    }
  }
  
  // VaR 95%
  const sortedPnL = sortedPnLForMedian
  const var95Index = Math.floor(sortedPnL.length * 0.05)
  const var95 = sortedPnL.length > 0 && var95Index < sortedPnL.length
    ? sortedPnL[var95Index]
    : 0
  
  // CVaR 95%
  const cvar95Trades = sortedPnL.slice(0, var95Index + 1)
  const cvar95 = cvar95Trades.length > 0
    ? cvar95Trades.reduce((sum, pnl) => sum + pnl, 0) / cvar95Trades.length
    : 0
  
  // Time-to-Recovery
  let timeToRecoveryDays = 0
  let peakValue = -Infinity
  let peakDate = null
  let inDrawdown = false
  
  const tradeDates = sortedByDate.map(trade => {
    const dateValue = trade.Date || trade.trade_date
    if (!dateValue) return null
    try {
      if (typeof dateValue === 'string' && dateValue.includes('/')) {
        const parts = dateValue.split(' ')[0].split('/')
        if (parts.length === 3) {
          return new Date(parseInt(parts[2]), parseInt(parts[1]) - 1, parseInt(parts[0]))
        }
        return null
      } else if (typeof dateValue === 'string' && dateValue.includes('-')) {
        return new Date(dateValue.split(' ')[0].split('T')[0])
      } else {
        const d = new Date(dateValue)
        return isNaN(d.getTime()) ? null : d
      }
    } catch {
      return null
    }
  })
  
  let runningEquity = 0
  sortedByDate.forEach((trade, idx) => {
    // Only include executed trades in time-to-recovery calculation
    if (!isExecutedTrade(normalizeResult(trade.Result))) return
    
    const profit = parseFloat(trade.Profit) || 0
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    runningEquity += profitDollars
    
    const tradeDate = tradeDates[idx]
    if (!tradeDate || isNaN(tradeDate.getTime())) return
    
    if (runningEquity > peakValue) {
      if (inDrawdown && peakDate) {
        const daysDiff = Math.floor((tradeDate.getTime() - peakDate.getTime()) / (1000 * 60 * 60 * 24))
        timeToRecoveryDays = Math.max(timeToRecoveryDays, daysDiff)
        inDrawdown = false
      }
      peakValue = runningEquity
      peakDate = tradeDate
    } else if (runningEquity < peakValue) {
      if (!inDrawdown) {
        inDrawdown = true
      }
    } else if (runningEquity === peakValue && inDrawdown) {
      if (peakDate) {
        const daysDiff = Math.floor((tradeDate.getTime() - peakDate.getTime()) / (1000 * 60 * 60 * 24))
        timeToRecoveryDays = Math.max(timeToRecoveryDays, daysDiff)
        inDrawdown = false
      }
    }
  })
  
  if (inDrawdown && peakDate && sortedByDate.length > 0) {
    const lastDate = tradeDates[sortedByDate.length - 1]
    if (lastDate && runningEquity < peakValue) {
      const daysDiff = Math.floor((lastDate.getTime() - peakDate.getTime()) / (1000 * 60 * 60 * 24))
      timeToRecoveryDays = Math.max(timeToRecoveryDays, daysDiff)
    }
  }
  
  // Monthly Return Std Dev (only executed trades, excludes NoTrade and TIME)
  const monthlyReturns = new Map()
  sortedByDate.forEach((trade) => {
    // Only include executed trades
    if (!isExecutedTrade(normalizeResult(trade.Result))) return
    const dateValue = trade.Date || trade.trade_date
    if (!dateValue) return
    
    try {
      let dateObj
      if (typeof dateValue === 'string' && dateValue.includes('/')) {
        const parts = dateValue.split(' ')[0].split('/')
        if (parts.length === 3) {
          dateObj = new Date(parseInt(parts[2]), parseInt(parts[1]) - 1, parseInt(parts[0]))
          if (isNaN(dateObj.getTime())) {
            dateObj = new Date(parseInt(parts[2]), parseInt(parts[0]) - 1, parseInt(parts[1]))
          }
        } else {
          return
        }
      } else if (typeof dateValue === 'string' && dateValue.includes('-')) {
        dateObj = new Date(dateValue.split(' ')[0].split('T')[0])
      } else {
        dateObj = new Date(dateValue)
      }
      
      if (isNaN(dateObj.getTime())) return
      
      const monthKey = `${dateObj.getFullYear()}-${String(dateObj.getMonth() + 1).padStart(2, '0')}`
      const profit = parseFloat(trade.Profit) || 0
      const contractValue = getContractValue(trade)
      const profitDollars = profit * contractValue * contractMultiplier
      
      if (!monthlyReturns.has(monthKey)) {
        monthlyReturns.set(monthKey, 0)
      }
      monthlyReturns.set(monthKey, monthlyReturns.get(monthKey) + profitDollars)
    } catch {
      // Skip invalid dates
    }
  })
  
  const monthlyReturnsArray = Array.from(monthlyReturns.values())
  const meanMonthlyReturn = monthlyReturnsArray.length > 0
    ? monthlyReturnsArray.reduce((sum, ret) => sum + ret, 0) / monthlyReturnsArray.length
    : 0
  const monthlyReturnVariance = monthlyReturnsArray.length > 1
    ? monthlyReturnsArray.reduce((sum, ret) => sum + Math.pow(ret - meanMonthlyReturn, 2), 0) / (monthlyReturnsArray.length - 1)
    : 0
  const monthlyReturnStdDev = Math.sqrt(monthlyReturnVariance)
  
  // Profit per Day (only executed trades, excludes NoTrade and TIME)
  const dailyProfits = new Map()
  sortedByDate.forEach((trade) => {
    // Only include executed trades
    if (!isExecutedTrade(normalizeResult(trade.Result))) return
    const dateValue = trade.Date || trade.trade_date
    if (!dateValue) return
    
    try {
      let dateKey
      if (typeof dateValue === 'string' && dateValue.includes('/')) {
        dateKey = dateValue.split(' ')[0]
      } else if (typeof dateValue === 'string' && dateValue.includes('-')) {
        dateKey = dateValue.split(' ')[0].split('T')[0]
      } else {
        const d = new Date(dateValue)
        dateKey = isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
      }
      
      if (dateKey) {
        const profit = parseFloat(trade.Profit) || 0
        const contractValue = getContractValue(trade)
        const profitDollars = profit * contractValue * contractMultiplier
        
        if (!dailyProfits.has(dateKey)) {
          dailyProfits.set(dateKey, 0)
        }
        dailyProfits.set(dateKey, dailyProfits.get(dateKey) + profitDollars)
      }
    } catch {
      // Skip invalid dates
    }
  })
  
  const dailyProfitsArray = Array.from(dailyProfits.values())
  const profitPerDay = dailyProfitsArray.length > 0
    ? dailyProfitsArray.reduce((sum, profit) => sum + profit, 0) / dailyProfitsArray.length
    : 0
  
  // Skewness
  const n = perTradePnLDollars.length
  let skewness = 0
  if (n > 2 && stdDevPnL > 0) {
    const skewnessSum = perTradePnLDollars.reduce((sum, pnl) => {
      return sum + Math.pow((pnl - meanPnL) / stdDevPnL, 3)
    }, 0)
    skewness = (n / ((n - 1) * (n - 2))) * skewnessSum
  }
  
  // Kurtosis
  let kurtosis = 0
  if (n > 3 && stdDevPnL > 0) {
    const kurtosisSum = perTradePnLDollars.reduce((sum, pnl) => {
      return sum + Math.pow((pnl - meanPnL) / stdDevPnL, 4)
    }, 0)
    kurtosis = ((n * (n + 1)) / ((n - 1) * (n - 2) * (n - 3))) * kurtosisSum - (3 * (n - 1) * (n - 1)) / ((n - 2) * (n - 3))
  }
  
  return {
    totalTrades, // Only executed trades (excludes NoTrade and TIME)
    totalDays,
    avgTradesPerDay: avgTradesPerDay.toFixed(2),
    wins,
    losses,
    breakEven,
    timeTrades: timeCount, // TIME trades count (for display only, excluded from all calculations)
    noTrade: noTradeCount, // NoTrade count (for display only, excluded from all calculations)
    winRate: winRate.toFixed(1),
    totalProfit: totalProfit.toFixed(2),
    totalProfitDollars: formatCurrency(totalProfitDollars),
    avgProfit: avgProfit.toFixed(2),
    avgWin: avgWin.toFixed(2),
    avgLoss: avgLoss.toFixed(2),
    rrRatio: rrRatio === Infinity ? '∞' : rrRatio.toFixed(2),
    profitFactor: profitFactor === Infinity ? '∞' : profitFactor.toFixed(2),
    timeChanges,
    finalTime: finalTime || 'N/A',
    sharpeRatio: sharpeRatio.toFixed(2),
    sortinoRatio: sortinoRatio.toFixed(2),
    calmarRatio: calmarRatio.toFixed(2),
    maxDrawdown: maxDrawdown.toFixed(2),
    maxDrawdownDollars: formatCurrency(maxDrawdownDollarsPositive),
    allowedTrades,
    blockedTrades,
    bestTrade,
    worstTrade,
    stdDevPnL: formatCurrency(stdDevPnL),
    maxConsecutiveLosses: maxConsecutiveLosses,
    profitPerTrade: formatCurrency(profitPerTrade),
    rolling30DayWinRate: rolling30DayWinRate !== null ? rolling30DayWinRate.toFixed(1) : null,
    meanPnLPerTrade: streamId === 'master' ? formatCurrency(meanPnLPerTrade) : null,
    medianPnLPerTrade: streamId === 'master' ? formatCurrency(medianPnLPerTrade) : null,
    var95: streamId === 'master' ? formatCurrency(var95) : null,
    cvar95: streamId === 'master' ? formatCurrency(cvar95) : null,
    timeToRecoveryDays: streamId === 'master' ? timeToRecoveryDays : null,
    monthlyReturnStdDev: streamId === 'master' ? formatCurrency(monthlyReturnStdDev) : null,
    profitPerDay: streamId === 'master' ? formatCurrency(profitPerDay) : null,
    skewness: streamId === 'master' ? skewness.toFixed(3) : null,
    kurtosis: streamId === 'master' ? kurtosis.toFixed(3) : null
  }
}

























