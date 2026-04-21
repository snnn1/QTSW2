import { describe, expect, it } from 'vitest'

import { computeTimeInState } from './timeUtils'

describe('computeTimeInState', () => {
  it('uses the supplied reference clock when provided', () => {
    expect(
      computeTimeInState('2026-04-13T15:30:00+00:00', '2026-04-13T20:55:00+00:00')
    ).toBe(19500)
  })

  it('clamps negative durations to zero', () => {
    expect(
      computeTimeInState('2026-04-13T20:55:00+00:00', '2026-04-13T15:30:00+00:00')
    ).toBe(0)
  })
})
