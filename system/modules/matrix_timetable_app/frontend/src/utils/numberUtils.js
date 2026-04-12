/**
 * Number formatting and parsing utilities
 */

/**
 * Get numeric value from trade object, defaulting to 0 if invalid
 * @param {Object} trade - Trade object
 * @param {string} field - Field name (e.g., 'Profit', 'Target')
 * @returns {number} Numeric value or 0
 */
export function getNumericValue(trade, field) {
  if (!trade || !field) return 0
  const value = trade[field]
  if (value === null || value === undefined) return 0
  const num = parseFloat(value)
  return isNaN(num) ? 0 : num
}

/**
 * Get profit value from trade object
 * @param {Object} trade - Trade object
 * @returns {number} Profit value or 0
 */
export function getProfit(trade) {
  return getNumericValue(trade, 'Profit')
}

/**
 * Format a ratio value, handling Infinity
 * @param {number} value - Ratio value
 * @param {number} decimals - Number of decimal places (default: 2)
 * @returns {string} Formatted ratio or '∞' for Infinity
 */
export function formatRatio(value, decimals = 2) {
  if (value === Infinity || value === -Infinity) {
    return '∞'
  }
  if (isNaN(value) || !isFinite(value)) {
    return '0.00'
  }
  return value.toFixed(decimals)
}

/**
 * Format number with appropriate decimal places based on instrument
 * @param {number|string} value - Number to format
 * @param {string} instrument - Instrument symbol (e.g., 'GC', 'NG')
 * @returns {string} Formatted number string
 */
export function formatNumber(value, instrument) {
  if (value === null || value === undefined) return '-'
  
  const numValue = typeof value === 'number' ? value : parseFloat(value)
  if (isNaN(numValue) || !isFinite(numValue)) return '-'
  
  const baseSymbol = (instrument || '').toUpperCase().replace(/\d+$/, '')
  const isNG = baseSymbol === 'NG'
  const decimalPlaces = isNG ? 3 : 2
  
  return numValue.toFixed(decimalPlaces)
}
