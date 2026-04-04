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

/** Chicago wall-clock fields for an instant (DST-aware via Intl). */
export function getChicagoWallPartsFromUtc(dateUtc) {
  const d = dateUtc instanceof Date ? dateUtc : new Date(dateUtc)
  const fmt = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Chicago',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false
  })
  const parts = fmt.formatToParts(d)
  const pick = (t) => parseInt(parts.find((p) => p.type === t).value, 10)
  return {
    year: pick('year'),
    month: pick('month'),
    day: pick('day'),
    hour: pick('hour'),
    minute: pick('minute'),
    second: pick('second')
  }
}

/** Add n days to a Gregorian calendar Y-M-D (timezone-agnostic). */
export function addDaysCalendarYmd(y, m, d, n) {
  const dt = new Date(Date.UTC(y, m - 1, d + n))
  return { y: dt.getUTCFullYear(), m: dt.getUTCMonth() + 1, d: dt.getUTCDate() }
}

export function calendarYmdToString(y, m, d) {
  return `${y}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`
}

/**
 * Roll Saturday/Sunday calendar days to Monday (CME equity-style week boundary for session label).
 * Weekday is computed in UTC on the calendar date (Y-M-D is unambiguous).
 */
export function applyCmeWeekendToCalendarYmd(y, m, d) {
  const jd = new Date(Date.UTC(y, m - 1, d, 12, 0, 0))
  const dow = jd.getUTCDay() // 0 Sun .. 6 Sat
  if (dow === 6) return addDaysCalendarYmd(y, m, d, 2)
  if (dow === 0) return addDaysCalendarYmd(y, m, d, 1)
  return { y, m, d }
}

/**
 * CME session calendar Y-M-D from an instant: Chicago wall 18:00 rollover, then weekend roll.
 * Matches modules/timetable/cme_session.get_cme_trading_date + weekend handling for Matrix UI parity.
 */
export function getCmeSessionTradingCalendarYmdFromUtc(dateUtc) {
  const { year, month, day, hour } = getChicagoWallPartsFromUtc(dateUtc)
  let y = year
  let m = month
  let d = day
  if (hour >= 18) {
    const n = addDaysCalendarYmd(y, m, d, 1)
    y = n.y
    m = n.m
    d = n.d
  }
  return applyCmeWeekendToCalendarYmd(y, m, d)
}

/** @param {Date|string|number} dateUtc */
export function getCmeSessionTradingDateStringForUtc(dateUtc) {
  const { y, m, d } = getCmeSessionTradingCalendarYmdFromUtc(dateUtc)
  return calendarYmdToString(y, m, d)
}

/**
 * If YYYY-MM-DD is a weekend calendar label, roll to Monday (defensive for worker / bad payloads).
 * Does not apply 18:00 (caller already passes session calendar day when known).
 */
export function rollWeekendCalendarYmdString(ymdStr) {
  if (typeof ymdStr !== 'string' || !ymdStr) return ymdStr
  const head = ymdStr.split('T')[0]
  const segs = head.split('-')
  if (segs.length !== 3) return ymdStr
  const y = parseInt(segs[0], 10)
  const mo = parseInt(segs[1], 10)
  const d = parseInt(segs[2], 10)
  if (!y || !mo || !d) return ymdStr
  const rolled = applyCmeWeekendToCalendarYmd(y, mo, d)
  return calendarYmdToString(rolled.y, rolled.m, rolled.d)
}

/** DOW/DOM for filters from a calendar Y-M-D string (timezone-agnostic weekday). */
export function getCalendarDowDomFromYmd(ymdStr) {
  const head = typeof ymdStr === 'string' ? ymdStr.split('T')[0] : ''
  const segs = head.split('-')
  if (segs.length !== 3) return null
  const y = parseInt(segs[0], 10)
  const m = parseInt(segs[1], 10)
  const d = parseInt(segs[2], 10)
  if (!y || !m || !d) return null
  const jd = new Date(Date.UTC(y, m - 1, d, 12, 0, 0))
  const targetDOWJS = jd.getUTCDay()
  const dayNames = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
  return {
    targetDOWJS,
    targetDOM: d,
    targetDOWName: dayNames[targetDOWJS]
  }
}

/**
 * Get CME session trading date with 18:00 America/Chicago rollover + weekend → Monday.
 * Aligns with watchdog / timetable_builder / robot when combined with rollWeekendCalendarYmdString for raw Y-M-D.
 *
 * @returns {string} CME session trading date in YYYY-MM-DD format
 */
export function getCMETradingDate() {
  return getCmeSessionTradingDateStringForUtc(new Date())
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

/**
 * Move Saturday/Sunday to Monday (same rules as matrix timetable trading day).
 * @param {Date} date - Local calendar date at midnight
 * @returns {Date} New Date instance (does not mutate input)
 */
export function applyCmeWeekendTradingDay(date) {
  if (!date || !(date instanceof Date)) {
    throw new Error(`Invalid date: ${date}`)
  }
  const ymd = dateToYYYYMMDD(date)
  const rolled = rollWeekendCalendarYmdString(ymd)
  return parseYYYYMMDD(rolled)
}

/**
 * Wall-clock instant formatted for audit logs (Chicago, labeled).
 */
export function formatChicagoWallIso(date) {
  if (!date || !(date instanceof Date)) {
    throw new Error(`Invalid date: ${date}`)
  }
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Chicago',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false
  }).formatToParts(date)
  const y = parts.find(p => p.type === 'year')?.value
  const m = parts.find(p => p.type === 'month')?.value
  const d = parts.find(p => p.type === 'day')?.value
  const h = parts.find(p => p.type === 'hour')?.value
  const min = parts.find(p => p.type === 'minute')?.value
  const s = parts.find(p => p.type === 'second')?.value
  return `${y}-${m}-${d}T${h}:${min}:${s} America/Chicago`
}

/** Wall-clock instant in UTC for audit / debug overlay (labeled). */
export function formatUtcWallIso(date) {
  if (!date || !(date instanceof Date)) {
    throw new Error(`Invalid date: ${date}`)
  }
  const y = date.getUTCFullYear()
  const m = String(date.getUTCMonth() + 1).padStart(2, '0')
  const d = String(date.getUTCDate()).padStart(2, '0')
  const h = String(date.getUTCHours()).padStart(2, '0')
  const min = String(date.getUTCMinutes()).padStart(2, '0')
  const s = String(date.getUTCSeconds()).padStart(2, '0')
  return `${y}-${m}-${d}T${h}:${min}:${s}Z`
}
