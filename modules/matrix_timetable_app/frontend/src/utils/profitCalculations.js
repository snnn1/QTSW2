/**
 * Profit calculation utilities
 * Functions for calculating profit by different time periods
 */

import { getProfit } from './numberUtils'

/**
 * Get contract value for a trade
 * NOTE: These must match the canonical values in modules/matrix/statistics.py (_ensure_profit_dollars_column)
 * Canonical source: modules/matrix/statistics.py line ~144
 */
export const getContractValue = (trade) => {
  const symbol = trade.Symbol || trade.Instrument || 'ES'
  const baseSymbol = symbol.replace(/\d+$/, '') // Remove trailing numbers
  const contractValues = {
    'ES': 50,
    'MES': 5,
    'NQ': 10,
    'MNQ': 2,
    'YM': 5,
    'MYM': 0.5,
    'CL': 1000,
    'NG': 10000,
    'GC': 100,
    'RTY': 50
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
 * Common profit calculation helper
 * @param {Array} data - Trade data
 * @param {Function} getKey - Function to extract key from trade (returns key or null to skip)
 * @param {Function} sortKeys - Optional function to sort keys
 * @param {number} contractMultiplier - Contract multiplier
 * @returns {Object} Nested object: { key: { stream: profitDollars } }
 */
const calculateProfitByKey = (data, getKey, sortKeys = null, contractMultiplier = 1) => {
  const result = {}
  
  data.forEach(trade => {
    const key = getKey(trade)
    if (key === null || key === undefined) return
    
    const stream = trade.Stream || 'Unknown'
    const profit = getProfit(trade)
    const contractValue = getContractValue(trade)
    const profitDollars = profit * contractValue * contractMultiplier
    
    if (!result[key]) {
      result[key] = {}
    }
    if (!result[key][stream]) {
      result[key][stream] = 0
    }
    
    result[key][stream] += profitDollars
  })
  
  // Sort keys if sort function provided
  if (sortKeys && typeof sortKeys === 'function') {
    const sortedKeys = Object.keys(result).sort(sortKeys)
    const orderedData = {}
    sortedKeys.forEach(key => {
      orderedData[key] = result[key]
    })
    return orderedData
  }
  
  return result
}

/**
 * Calculate profit by Time slot by stream
 */
export const calculateTimeProfit = (data, contractMultiplier = 1) => {
  const getTimeKey = (trade) => {
    const time = trade.Time
    if (!time || time === 'NA' || time === '00:00') return null
    
    // Normalize time format (ensure HH:MM format)
    let timeKey = time.toString().trim()
    // If time doesn't match HH:MM format, try to extract it
    if (!/^\d{2}:\d{2}$/.test(timeKey)) {
      // Try to extract time from string
      const match = timeKey.match(/(\d{2}:\d{2})/)
      if (match) {
        timeKey = match[1]
      } else {
        return null // Skip if we can't parse the time
      }
    }
    return timeKey
  }
  
  const sortTimeKeys = (a, b) => {
    const [aHour, aMin] = a.split(':').map(Number)
    const [bHour, bMin] = b.split(':').map(Number)
    if (aHour !== bHour) return aHour - bHour
    return aMin - bMin
  }
  
  return calculateProfitByKey(data, getTimeKey, sortTimeKeys, contractMultiplier)
}

/**
 * Calculate profit by Day of Month (DOM) by stream
 */
export const calculateDOMProfit = (data, contractMultiplier = 1) => {
  const getDOMKey = (trade) => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return null
    return date.getDate() // 1-31
  }
  
  const result = calculateProfitByKey(data, getDOMKey, null, contractMultiplier)
  
  // Return data ordered by day of month (1-31)
  const orderedData = {}
  for (let day = 1; day <= 31; day++) {
    if (result[day]) {
      orderedData[day] = result[day]
    }
  }
  
  return orderedData
}

/**
 * Calculate profit by Day of Year (DOY) by stream
 * 
 * @deprecated Use backend DOY breakdown instead. This function is kept only as a fallback
 * for cases where backend data is unavailable. DOY calculation is backend-only.
 */
export const calculateDOYProfit = (data, contractMultiplier = 1) => {
  // Fallback calculation - not canonical
  // This function should rarely/never be called since DOY uses backend API exclusively
  console.warn('calculateDOYProfit: Using fallback calculation. Backend DOY breakdown should be used instead.')
  
  // For now, return empty object - backend is the source of truth
  // If fallback is truly needed, day of year calculation would need to be implemented
  // but it's better to ensure backend data is always available
  return {}
}

/**
 * Calculate profit by Day of Week (DOW) by stream
 */
export const calculateDailyProfit = (data, contractMultiplier = 1) => {
  const dowOrder = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
  
  const getDOWKey = (trade) => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return null
    
    const dow = date.toLocaleDateString('en-US', { weekday: 'long' })
    // Skip weekends
    if (!dowOrder.includes(dow)) return null
    return dow
  }
  
  const result = calculateProfitByKey(data, getDOWKey, null, contractMultiplier)
  
  // Return data ordered by DOW
  const orderedData = {}
  dowOrder.forEach(dow => {
    if (result[dow]) {
      orderedData[dow] = result[dow]
    }
  })
  
  return orderedData
}

/**
 * Calculate monthly profit by stream
 */
export const calculateMonthlyProfit = (data, contractMultiplier = 1) => {
  const getMonthKey = (trade) => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return null
    
    const year = date.getFullYear()
    const month = date.getMonth() + 1 // 1-12
    return `${year}-${String(month).padStart(2, '0')}`
  }
  
  return calculateProfitByKey(data, getMonthKey, null, contractMultiplier)
}

/**
 * Calculate daily profit by actual date (YYYY-MM-DD) by stream
 */
export const calculateDateProfit = (data, contractMultiplier = 1) => {
  const getDateKey = (trade) => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return null
    return date.toISOString().split('T')[0] // YYYY-MM-DD format
  }
  
  return calculateProfitByKey(data, getDateKey, null, contractMultiplier)
}

/**
 * Calculate yearly profit by stream
 */
export const calculateYearlyProfit = (data, contractMultiplier = 1) => {
  const getYearKey = (trade) => {
    const date = parseDateValue(trade.Date || trade.trade_date)
    if (!date) return null
    return date.getFullYear()
  }
  
  return calculateProfitByKey(data, getYearKey, null, contractMultiplier)
}


