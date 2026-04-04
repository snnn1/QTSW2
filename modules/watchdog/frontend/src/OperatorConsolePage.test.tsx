import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import type { WatchdogStatus } from './types/watchdog'
import { OperatorConsolePage } from './OperatorConsolePage'
import { executionReasonToOperatorMessage } from './utils/executionSeverity'

function mockWatchdogStatus(partial: Partial<WatchdogStatus>): WatchdogStatus {
  return {
    timestamp_chicago: '',
    engine_alive: true,
    engine_activity_state: 'ACTIVE',
    last_engine_tick_chicago: null,
    engine_tick_stall_detected: false,
    recovery_state: 'CONNECTED_OK',
    reconciliation_gate_state: 'OK',
    adoption_grace_expired_active: false,
    execution_safe: true,
    kill_switch_active: false,
    connection_status: 'Connected',
    last_connection_event_chicago: null,
    stuck_streams: [],
    execution_blocked_count: 0,
    protective_failures_count: 0,
    data_stall_detected: {},
    market_open: true,
    last_identity_invariants_pass: null,
    last_identity_invariants_event_chicago: null,
    last_identity_violations: [],
    ...partial,
  }
}

const mockUseWatchdogStatus = vi.hoisted(() => vi.fn())

vi.mock('./hooks/useOperatorSnapshot', () => ({
  useOperatorSnapshot: () => ({
    snapshot: {},
    loading: false,
    error: null,
    lastSuccessfulFetchTimestamp: Date.now(),
  }),
}))

vi.mock('./hooks/useRiskGates', () => ({
  useRiskGates: () => ({
    gates: null,
    loading: false,
    error: null,
    lastSuccessfulPollTimestamp: Date.now(),
  }),
}))

vi.mock('./hooks/useWatchdogStatus', () => ({
  useWatchdogStatus: () => mockUseWatchdogStatus(),
}))

describe('OperatorConsolePage', () => {
  beforeEach(() => {
    mockUseWatchdogStatus.mockReturnValue({
      status: mockWatchdogStatus({
        reconciliation_gate_state: 'ENGAGED',
        execution_safe: false,
      }),
      loading: false,
      error: null,
      lastSuccessfulPollTimestamp: Date.now(),
    })
  })

  it('displays execution row from shared helper (not ad hoc execution_safe checks)', () => {
    render(
      <MemoryRouter>
        <OperatorConsolePage />
      </MemoryRouter>
    )
    expect(screen.getByTestId('operator-execution-severity').textContent).toBe('WARNING')
    expect(screen.getByTestId('operator-execution-message').textContent).toBe(
      executionReasonToOperatorMessage('RECONCILIATION_GATE_ENGAGED')
    )
  })
})
