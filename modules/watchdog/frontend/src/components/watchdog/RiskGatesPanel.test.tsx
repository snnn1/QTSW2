import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { RiskGatesPanel } from './RiskGatesPanel'
import type { RiskGateStatus } from '../../types/watchdog'
import { executionReasonToOperatorMessage } from '../../utils/executionSeverity'

const mockGates = (over: Partial<RiskGateStatus> = {}): RiskGateStatus => ({
  timestamp_chicago: '',
  recovery_state_allowed: true,
  kill_switch_allowed: true,
  execution_safe: true,
  timetable_validated: true,
  stream_armed: [],
  session_slot_time_valid: true,
  trading_date_set: true,
  ...over,
})

describe('RiskGatesPanel', () => {
  it('shows severity message from shared reason mapping (ENGAGED)', () => {
    render(
      <RiskGatesPanel
        gates={mockGates({ recovery_state_allowed: false, execution_safe: false })}
        loading={false}
        overallExecution={{
          overall_execution_severity: 'WARNING',
          overall_execution_reason: 'RECONCILIATION_GATE_ENGAGED',
          execution_blocked: true,
        }}
      />
    )
    expect(screen.getByTestId('risk-gates-execution-severity-message').textContent).toBe(
      executionReasonToOperatorMessage('RECONCILIATION_GATE_ENGAGED')
    )
  })
})
