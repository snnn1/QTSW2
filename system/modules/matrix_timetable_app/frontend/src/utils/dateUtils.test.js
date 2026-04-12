import { describe, it, expect } from 'vitest'
import {
  getCmeSessionTradingDateStringForUtc,
  rollWeekendCalendarYmdString,
  getCalendarDowDomFromYmd
} from './dateUtils'

/** Chicago week boundary used in product tests (2025-03-28 = Friday, CDT UTC-5). */
describe('getCmeSessionTradingDateStringForUtc (CME 18:00 Chicago + weekend)', () => {
  it('Friday 17:00 CT → same Friday', () => {
    const utc = '2025-03-28T22:00:00.000Z' // Fri 17:00 CDT
    expect(getCmeSessionTradingDateStringForUtc(utc)).toBe('2025-03-28')
  })

  it('Friday 18:01 CT → Monday (session roll + weekend)', () => {
    const utc = '2025-03-28T23:01:00.000Z' // Fri 18:01 CDT
    expect(getCmeSessionTradingDateStringForUtc(utc)).toBe('2025-03-31')
  })

  it('Saturday midday CT → Monday', () => {
    const utc = '2025-03-29T17:00:00.000Z' // Sat 12:00 CDT
    expect(getCmeSessionTradingDateStringForUtc(utc)).toBe('2025-03-31')
  })

  it('Sunday 17:00 CT → Monday', () => {
    const utc = '2025-03-30T22:00:00.000Z' // Sun 17:00 CDT
    expect(getCmeSessionTradingDateStringForUtc(utc)).toBe('2025-03-31')
  })

  it('Sunday 18:01 CT → Monday', () => {
    const utc = '2025-03-30T23:01:00.000Z' // Sun 18:01 CDT
    expect(getCmeSessionTradingDateStringForUtc(utc)).toBe('2025-03-31')
  })
})

describe('rollWeekendCalendarYmdString', () => {
  it('rolls Saturday calendar label to Monday', () => {
    expect(rollWeekendCalendarYmdString('2025-03-29')).toBe('2025-03-31')
  })

  it('leaves Monday unchanged', () => {
    expect(rollWeekendCalendarYmdString('2025-03-31')).toBe('2025-03-31')
  })
})

describe('getCalendarDowDomFromYmd', () => {
  it('returns Monday for a Monday Y-M-D regardless of local TZ', () => {
    const r = getCalendarDowDomFromYmd('2025-03-31')
    expect(r.targetDOWJS).toBe(1)
    expect(r.targetDOWName).toBe('Monday')
    expect(r.targetDOM).toBe(31)
  })
})
