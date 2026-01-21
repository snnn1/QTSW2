/**
 * Date utility functions for timezone-safe date handling.
 * 
 * CRITICAL: Never use .toISOString() for date-only values as it converts to UTC
 * which can shift dates based on browser timezone.
 */

/**
 * Extract YYYY-MM-DD from a Date object without timezone conversion.
 * Uses local date components, not UTC.
 * 
 * @param {Date} date - Date object
 * @returns {string} Date string in YYYY-MM-DD format
 */
export function dateToYYYYMMDD(date) {
  if (!date || !(date instanceof Date)) {
    throw new Error(`Invalid date: ${date}`)
  }
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

/**
 * Get Chicago date as YYYY-MM-DD string.
 * Uses explicit timezone conversion to America/Chicago.
 * 
 * Note: JavaScript doesn't have native timezone support, so this uses
 * offset calculation. For DST-aware conversion, consider using a library
 * like date-fns-tz or moment-timezone.
 * 
 * @returns {string} Chicago date in YYYY-MM-DD format
 */
export function getChicagoDateNow() {
  const now = new Date()
  // Chicago is UTC-6 (CST) or UTC-5 (CDT)
  // For simplicity, using UTC-6. For production, use proper DST-aware conversion
  const chicagoOffset = -6 * 60 // CST offset in minutes
  const utc = now.getTime() + (now.getTimezoneOffset() * 60000)
  const chicagoTime = new Date(utc + (chicagoOffset * 60000))
  return dateToYYYYMMDD(chicagoTime)
}

/**
 * Get CME trading date with 17:00 Chicago rollover rule.
 * 
 * CME trading date semantics:
 * - If Chicago time < 17:00: trading_date = Chicago calendar date
 * - If Chicago time >= 17:00: trading_date = Chicago calendar date + 1 day
 * 
 * This matches the timetable generation logic.
 * 
 * @returns {string} CME trading date in YYYY-MM-DD format
 */
export function getCMETradingDate() {
  const now = new Date()
  // Get Chicago time using Intl.DateTimeFormat for DST-aware conversion
  const chicagoFormatter = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Chicago',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false
  })
  
  const parts = chicagoFormatter.formatToParts(now)
  const year = parseInt(parts.find(p => p.type === 'year').value)
  const month = parseInt(parts.find(p => p.type === 'month').value)
  const day = parseInt(parts.find(p => p.type === 'day').value)
  const hour = parseInt(parts.find(p => p.type === 'hour').value)
  
  // Apply CME rollover rule: if >= 17:00, trading_date is next calendar day
  let tradingDate = new Date(year, month - 1, day)
  if (hour >= 17) {
    tradingDate.setDate(tradingDate.getDate() + 1)
  }
  
  return dateToYYYYMMDD(tradingDate)
}

/**
 * Parse YYYY-MM-DD string to Date object.
 * Creates date at midnight local time (no timezone conversion).
 * 
 * @param {string} dateStr - Date string in YYYY-MM-DD format
 * @returns {Date} Date object
 */
export function parseYYYYMMDD(dateStr) {
  if (typeof dateStr !== 'string') {
    throw new Error(`Invalid date string: ${dateStr}`)
  }
  const parts = dateStr.split('-')
  if (parts.length !== 3) {
    throw new Error(`Invalid date format: ${dateStr}. Expected YYYY-MM-DD`)
  }
  const year = parseInt(parts[0], 10)
  const month = parseInt(parts[1], 10) - 1 // Month is 0-indexed
  const day = parseInt(parts[2], 10)
  return new Date(year, month, day)
}

/**
 * Format date/time in Chicago timezone.
 * Uses Intl.DateTimeFormat for proper DST-aware conversion.
 * 
 * @param {Date} date - Date object
 * @param {object} options - Intl.DateTimeFormat options
 * @returns {string} Formatted date/time string
 */
export function formatChicagoTime(date, options = {}) {
  if (!date || !(date instanceof Date)) {
    throw new Error(`Invalid date: ${date}`)
  }
  
  const defaultOptions = {
    timeZone: 'America/Chicago',
    ...options
  }
  
  return new Intl.DateTimeFormat('en-US', defaultOptions).format(date)
}
