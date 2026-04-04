import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { WatchdogHeader } from './WatchdogHeader'
import type { OverallExecutionDerived } from '../../utils/executionSeverity'
import { executionReasonToOperatorMessage } from '../../utils/executionSeverity'

const baseExec = (over: Partial<OverallExecutionDerived> = {}): OverallExecutionDerived => ({
  overall_execution_severity: 'SAFE',
  overall_execution_reason: 'SAFE',
  execution_blocked: false,
  ...over,
})

const minimalHeaderProps = {
  runId: null as string | null,
  engineStatus: 'ALIVE' as const,
  marketOpen: true as boolean | null,
  connectionStatus: 'Connected' as string | null,
  dataFlowStatus: 'FLOWING' as const,
  chicagoTime: '',
  lastEngineTick: null as string | null,
  lastSuccessfulPollTimestamp: Date.now(),
  identityInvariantsPass: true as boolean | null,
  identityViolations: [] as string[],
}

describe('WatchdogHeader', () => {
  it('renders execution badge from overallExecution prop (CRITICAL)', () => {
    const overallExecution = baseExec({
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'KILL_SWITCH',
      execution_blocked: true,
    })
    render(<WatchdogHeader {...minimalHeaderProps} overallExecution={overallExecution} />)
    const badge = screen.getByTestId('execution-severity-badge')
    expect(badge).toHaveAttribute(
      'title',
      executionReasonToOperatorMessage('KILL_SWITCH')
    )
    expect(badge.textContent).toContain('CRITICAL')
  })

  it('renders CRITICAL (Timetable Drift) label when reason is TIMETABLE_DRIFT', () => {
    const overallExecution = baseExec({
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'TIMETABLE_DRIFT',
      execution_blocked: true,
    })
    render(
      <WatchdogHeader
        {...minimalHeaderProps}
        overallExecution={overallExecution}
        executionHashDetail={{ robot: 'abc123def', publisher: 'ffeeddcc', content: '11223344' }}
      />
    )
    const badge = screen.getByTestId('execution-severity-badge')
    expect(badge.textContent).toContain('Timetable Drift')
    expect(badge.getAttribute('title') || '').toContain('Robot is running')
  })

  it('renders WARNING execution badge when overallExecution is WARNING', () => {
    const overallExecution = baseExec({
      overall_execution_severity: 'WARNING',
      overall_execution_reason: 'RECONCILIATION_GATE_ENGAGED',
      execution_blocked: true,
    })
    render(<WatchdogHeader {...minimalHeaderProps} overallExecution={overallExecution} />)
    expect(screen.getByTestId('execution-severity-badge').textContent).toContain('WARNING')
  })
})
