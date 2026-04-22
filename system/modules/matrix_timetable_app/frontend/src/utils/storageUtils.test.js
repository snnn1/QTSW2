import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  readStoredBoolean,
  readStoredChoice,
  readStoredJsonObject,
  readStoredNumber,
} from './storageUtils'

describe('storageUtils', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.restoreAllMocks()
  })

  it('returns fallback and clears malformed boolean values', () => {
    localStorage.setItem('matrix_include_filtered_executed', 'undefined')
    const removeSpy = vi.spyOn(Storage.prototype, 'removeItem')

    expect(readStoredBoolean('matrix_include_filtered_executed', true)).toBe(true)
    expect(removeSpy).toHaveBeenCalledWith('matrix_include_filtered_executed')
    expect(localStorage.getItem('matrix_include_filtered_executed')).toBeNull()
  })

  it('returns stored finite numbers and falls back on invalid ones', () => {
    localStorage.setItem('matrix_master_contract_multiplier', '2.5')
    expect(readStoredNumber('matrix_master_contract_multiplier', 1)).toBe(2.5)

    localStorage.setItem('matrix_master_contract_multiplier', 'NaN')
    expect(readStoredNumber('matrix_master_contract_multiplier', 1)).toBe(1)
  })

  it('returns validated object payloads only', () => {
    localStorage.setItem('matrix_show_filters', JSON.stringify({ master: true }))
    expect(readStoredJsonObject('matrix_show_filters', {})).toEqual({ master: true })

    localStorage.setItem('matrix_show_filters', JSON.stringify(['bad-shape']))
    expect(readStoredJsonObject('matrix_show_filters', {})).toEqual({})
  })

  it('returns allowed choices and falls back on unknown values', () => {
    localStorage.setItem('matrix_timetable_mode', 'historical')
    expect(readStoredChoice('matrix_timetable_mode', ['live', 'historical'], 'live')).toBe('historical')

    localStorage.setItem('matrix_timetable_mode', 'preview')
    expect(readStoredChoice('matrix_timetable_mode', ['live', 'historical'], 'live')).toBe('live')
  })
})
