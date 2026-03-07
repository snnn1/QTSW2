import { describe, it, expect, beforeEach, vi } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { useMatrixFilters } from './useMatrixFilters'

describe('useMatrixFilters', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.clearAllMocks()
  })

  it('loads filters from localStorage on init', () => {
    const saved = {
      master: {
        exclude_days_of_week: ['Wednesday'],
        exclude_days_of_month: [],
        exclude_times: [],
        include_years: [],
        include_streams: []
      }
    }
    localStorage.setItem('matrix_stream_filters', JSON.stringify(saved))
    const { result } = renderHook(() => useMatrixFilters())
    expect(result.current.streamFilters.master.exclude_days_of_week).toEqual(['Wednesday'])
  })

  it('returns default filters for stream with no saved filters', () => {
    const { result } = renderHook(() => useMatrixFilters())
    const filters = result.current.getFiltersForStream('ES1')
    expect(filters).toHaveProperty('exclude_days_of_week', [])
    expect(filters).toHaveProperty('exclude_days_of_month', [])
    expect(filters).toHaveProperty('exclude_times', [])
    expect(filters).toHaveProperty('include_years', [])
  })

  it('updateStreamFilter adds/removes exclude_days_of_week', () => {
    const { result } = renderHook(() => useMatrixFilters())
    act(() => {
      result.current.updateStreamFilter('master', 'exclude_days_of_week', 'Wednesday')
    })
    expect(result.current.streamFilters.master.exclude_days_of_week).toContain('Wednesday')
    act(() => {
      result.current.updateStreamFilter('master', 'exclude_days_of_week', 'Wednesday')
    })
    expect(result.current.streamFilters.master.exclude_days_of_week).not.toContain('Wednesday')
  })

  it('updateStreamFilter adds/removes exclude_times', () => {
    const { result } = renderHook(() => useMatrixFilters())
    act(() => {
      result.current.updateStreamFilter('ES1', 'exclude_times', '07:30')
    })
    expect(result.current.streamFilters.ES1.exclude_times).toContain('07:30')
  })
})
