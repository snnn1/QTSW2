/**
 * Profit calculation utilities
 * Functions for calculating profit by different time periods
 */

/**
 * Get contract value for a trade
 */
export const getContractValue = (trade) => {
  const symbol = trade.Symbol || trade.Instrument || 'ES'
  const baseSymbol = symbol.replace(/\d+$/, '') // Remove trailing numbers
  const contractValues = {
    'ES': 50,
    'NQ': 10,
    'YM': 5,
    'CL': 1000,
    'NG': 10000,
    'GC': 100
  }
  return contractValues[baseSymbol] || 50 // Default to ES if unknown
}

/**
 * Parse date value from various formats
 */
export const parseDateValue = (dateValue) => {
  if (!dateValue) return null
  try {
    let date
    if (typeof dateValue === 'string' && dateValue.includes('/')) {
      // Handle DD/MM/YYYY format
      const parts = dateValue.split('/')
      if (parts.length === 3) {
        date = new Date(parseInt(parts[2]), parseInt(parts[1]) - 1, parseInt(parts[0]))
      } else {
        date = new Date(dateValue)
      }
    } else {
      date = new Date(dateValue)
    }
    if (isNaN(date.getTime())) return null
    return date
  } catch (e) {
    return null
  }
}

/**
 * Calculate profit by Time slot by stream
 */
export const calculateTimeProfit = (data, contractMultiplier = 1) => {
  const timeData = {}
  
  data.forEach(trade => {
    const time = trade.Time
    if (!time || time === 'NA' || time === '00:00') return
    
    // Normalize time format (ensure HH:MM format)
    let timeKey = time.toString().trim()
    // If time doesn't match HH:MM format, try to extract it
    if (!/^\d{2}:\d{2}$/.test(timeKey)) {
      // Try to extract time from string
      const match = timeKey.match(/(\d{2}:\d{2})/)
      if (match) {
        timeKey = match[1]
      } else {
        return // Skip if we can't parse the time
      }
    }
    
    const stream = trade.Stream || 'Unknown'
    
    if (!timeData[timeKey]) {
      timeData[timeKey] = {}
    }
    if (!timeData[timeKey][stream]) {
      timeData[timeKey][stream] = 0
    }
    
    const profit = parseFloat(trade.Profit) || 0
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    
    timeData[timeKey][stream] += profitDollars
  })
  
  // Sort time slots chronologically
  const sortedTimeKeys = Object.keys(timeData).sort((a, b) => {
    const [aHour, aMin] = a.split(':').map(Number)
    const [bHour, bMin] = b.split(':').map(Number)
    if (aHour !== bHour) return aHour - bHour
    return aMin - bMin
  })
  
  // Return data ordered by time
  const orderedData = {}
  sortedTimeKeys.forEach(timeKey => {
    orderedData[timeKey] = timeData[timeKey]
  })
  
  return orderedData
}

/**
 * Calculate profit by Day of Month (DOM) by stream
 */
export const calculateDOMProfit = (data, contractMultiplier = 1) => {
  const domData = {}
  
  data.forEach(trade => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return
    
    const dayOfMonth = date.getDate() // 1-31
    const stream = trade.Stream || 'Unknown'
    
    if (!domData[dayOfMonth]) {
      domData[dayOfMonth] = {}
    }
    if (!domData[dayOfMonth][stream]) {
      domData[dayOfMonth][stream] = 0
    }
    
    const profit = parseFloat(trade.Profit) || 0
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    
    domData[dayOfMonth][stream] += profitDollars
  })
  
  // Return data ordered by day of month (1-31)
  const orderedData = {}
  for (let day = 1; day <= 31; day++) {
    if (domData[day]) {
      orderedData[day] = domData[day]
    }
  }
  
  return orderedData
}

/**
 * Calculate profit by Day of Week (DOW) by stream
 */
export const calculateDailyProfit = (data, contractMultiplier = 1) => {
  const dowData = {}
  const dowOrder = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
  
  data.forEach(trade => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return
    
    const dow = date.toLocaleDateString('en-US', { weekday: 'long' })
    // Skip weekends
    if (!dowOrder.includes(dow)) return
    
    const stream = trade.Stream || 'Unknown'
    
    if (!dowData[dow]) {
      dowData[dow] = {}
    }
    if (!dowData[dow][stream]) {
      dowData[dow][stream] = 0
    }
    
    const profit = parseFloat(trade.Profit) || 0
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    
    dowData[dow][stream] += profitDollars
  })
  
  // Return data ordered by DOW
  const orderedData = {}
  dowOrder.forEach(dow => {
    if (dowData[dow]) {
      orderedData[dow] = dowData[dow]
    }
  })
  
  return orderedData
}

/**
 * Calculate monthly profit by stream
 */
export const calculateMonthlyProfit = (data, contractMultiplier = 1) => {
  const monthlyData = {}
  
  data.forEach(trade => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return
    
    const year = date.getFullYear()
    const month = date.getMonth() + 1 // 1-12
    const monthKey = `${year}-${String(month).padStart(2, '0')}`
    const stream = trade.Stream || 'Unknown'
    
    if (!monthlyData[monthKey]) {
      monthlyData[monthKey] = {}
    }
    if (!monthlyData[monthKey][stream]) {
      monthlyData[monthKey][stream] = 0
    }
    
    const profit = parseFloat(trade.Profit) || 0
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    
    monthlyData[monthKey][stream] += profitDollars
  })
  
  return monthlyData
}

/**
 * Calculate daily profit by actual date (YYYY-MM-DD) by stream
 */
export const calculateDateProfit = (data, contractMultiplier = 1) => {
  const dateData = {}
  
  data.forEach(trade => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return
    
    const dateKey = date.toISOString().split('T')[0] // YYYY-MM-DD format
    const stream = trade.Stream || 'Unknown'
    
    if (!dateData[dateKey]) {
      dateData[dateKey] = {}
    }
    if (!dateData[dateKey][stream]) {
      dateData[dateKey][stream] = 0
    }
    
    const profit = parseFloat(trade.Profit) || 0
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    
    dateData[dateKey][stream] += profitDollars
  })
  
  return dateData
}

/**
 * Calculate yearly profit by stream
 */
export const calculateYearlyProfit = (data, contractMultiplier = 1) => {
  const yearlyData = {}
  
  data.forEach(trade => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return
    
    const year = date.getFullYear()
    const stream = trade.Stream || 'Unknown'
    
    if (!yearlyData[year]) {
      yearlyData[year] = {}
    }
    if (!yearlyData[year][stream]) {
      yearlyData[year][stream] = 0
    }
    
    const profit = parseFloat(trade.Profit) || 0
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    
    yearlyData[year][stream] += profitDollars
  })
  
  return yearlyData
}


