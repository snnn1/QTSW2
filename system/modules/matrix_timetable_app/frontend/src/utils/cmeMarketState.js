/**
 * CME-style market state from wall clock + holiday calendar (data-driven).
 * Calendar: modules/config/cme_holidays_YYYY.json (imported via Vite alias @qtsw2-config).
 */

import cmeHolidays2026 from '@qtsw2-config/cme_holidays_2026.json'

/** @type {typeof cmeHolidays2026} */
const holidaysCalendar = cmeHolidays2026

/**
 * Chicago wall calendar YYYY-MM-DD for an instant.
 * @param {Date} now
 * @returns {string}
 */
export function getChicagoDateYMD(now) {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: 'America/Chicago',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(now)
}

/**
 * @param {string} chicagoDateYmd YYYY-MM-DD (Chicago)
 * @param {typeof cmeHolidays2026} calendar
 */
function isFullCloseHoliday(chicagoDateYmd, calendar = holidaysCalendar) {
  const rows = calendar?.holidays
  if (!Array.isArray(rows)) return false
  return rows.some((h) => h.date === chicagoDateYmd && h.type === 'FULL_CLOSE')
}

/**
 * Rough CME Globex session state (full closes from JSON; not instrument-specific).
 * @param {Date|string|number} nowUtc
 * @returns {'OPEN'|'CLOSED'|'PRE-OPEN'}
 */
export function getCmeMarketState(nowUtc) {
  const now = nowUtc instanceof Date ? nowUtc : new Date(nowUtc)
  if (Number.isNaN(now.getTime())) {
    return 'CLOSED'
  }

  const chicagoFormatter = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Chicago',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    weekday: 'short',
  })

  const parts = chicagoFormatter.formatToParts(now)
  const hourPart = parts.find((p) => p.type === 'hour')
  const weekdayPart = parts.find((p) => p.type === 'weekday')
  const hour = hourPart ? parseInt(hourPart.value, 10) : 0

  const weekdayMap = {
    Sun: 0,
    Mon: 1,
    Tue: 2,
    Wed: 3,
    Thu: 4,
    Fri: 5,
    Sat: 6,
  }

  const weekdayStr = weekdayPart?.value ?? 'Mon'
  const weekday = weekdayMap[weekdayStr] ?? 1

  const chicagoDate = getChicagoDateYMD(now)

  if (isFullCloseHoliday(chicagoDate, holidaysCalendar)) {
    return 'CLOSED'
  }

  if (weekday === 6) return 'CLOSED'

  if (weekday === 0) {
    if (hour < 16) return 'CLOSED'
    if (hour < 17) return 'PRE-OPEN'
    if (hour < 18) return 'CLOSED'
    return 'OPEN'
  }

  if (hour === 17) return 'CLOSED'

  return 'OPEN'
}
