import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { CriticalAlertBanner } from './CriticalAlertBanner'

describe('CriticalAlertBanner', () => {
  it('groups duplicate alerts and keeps the operator summary compact', () => {
    render(
      <CriticalAlertBanner
        alerts={[
          { type: 'critical', message: 'RISK LATCH: MNG flatten blocked', scrollTo: 'risk-latches-panel' },
          { type: 'critical', message: 'RISK LATCH: MNG flatten blocked', scrollTo: 'risk-latches-panel' },
          { type: 'degraded', message: 'RECOVERY IN PROGRESS', scrollTo: 'risk-gates-panel' },
        ]}
      />
    )

    expect(screen.getByText('Operator alerts: 2 critical, 1 degraded')).toBeInTheDocument()
    expect(screen.getByText('RISK LATCH: MNG flatten blocked (2)')).toBeInTheDocument()
  })
})
