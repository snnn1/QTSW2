import { describe, it, expect } from 'vitest'
import type { WatchdogStatus } from '../types/watchdog'
import {
  deriveOverallExecutionStatus,
  deriveOperatorSystemStatus,
  EXECUTION_REASON_OPERATOR_MESSAGES,
} from './executionSeverity'

function baseStatus(over: Partial<WatchdogStatus> = {}): WatchdogStatus {
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
    ...over,
  }
}

describe('deriveOverallExecutionStatus', () => {
  it('kill switch active → CRITICAL / KILL_SWITCH', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: true,
        execution_safe: false,
        reconciliation_gate_state: 'OK',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: false,
      },
      null
    )
    expect(d.overall_execution_severity).toBe('CRITICAL')
    expect(d.overall_execution_reason).toBe('KILL_SWITCH')
    expect(d.execution_blocked).toBe(true)
  })

  it('timetable drift → CRITICAL / TIMETABLE_DRIFT (after kill switch ordering)', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        timetable_drift: true,
        reconciliation_gate_state: 'OK',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: false,
      },
      null
    )
    expect(d.overall_execution_severity).toBe('CRITICAL')
    expect(d.overall_execution_reason).toBe('TIMETABLE_DRIFT')
    expect(d.execution_blocked).toBe(true)
  })

  it('kill switch wins over timetable drift', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: true,
        execution_safe: false,
        timetable_drift: true,
        reconciliation_gate_state: 'OK',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: false,
      },
      null
    )
    expect(d.overall_execution_reason).toBe('KILL_SWITCH')
  })

  it('gate fail-closed → CRITICAL / RECONCILIATION_GATE_FAIL_CLOSED', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'FAIL_CLOSED',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: false,
      },
      null
    )
    expect(d.overall_execution_severity).toBe('CRITICAL')
    expect(d.overall_execution_reason).toBe('RECONCILIATION_GATE_FAIL_CLOSED')
  })

  it('disconnect fail-closed → CRITICAL / DISCONNECT_FAIL_CLOSED', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'OK',
        recovery_state: 'DISCONNECT_FAIL_CLOSED',
        adoption_grace_expired_active: false,
      },
      null
    )
    expect(d.overall_execution_severity).toBe('CRITICAL')
    expect(d.overall_execution_reason).toBe('DISCONNECT_FAIL_CLOSED')
  })

  it('adoption grace + execution_safe false → CRITICAL / ADOPTION_GRACE_EXPIRED', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'OK',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: true,
      },
      null
    )
    expect(d.overall_execution_severity).toBe('CRITICAL')
    expect(d.overall_execution_reason).toBe('ADOPTION_GRACE_EXPIRED')
  })

  it('gate engaged → WARNING / RECONCILIATION_GATE_ENGAGED', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'ENGAGED',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: false,
      },
      null
    )
    expect(d.overall_execution_severity).toBe('WARNING')
    expect(d.overall_execution_reason).toBe('RECONCILIATION_GATE_ENGAGED')
  })

  it('execution_safe false + recovery running → WARNING / RECOVERY_NOT_CLEAR (deterministic)', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'OK',
        recovery_state: 'RECOVERY_RUNNING',
        adoption_grace_expired_active: false,
        engine_alive: true,
      },
      null
    )
    expect(d.overall_execution_severity).toBe('WARNING')
    expect(d.overall_execution_reason).toBe('RECOVERY_NOT_CLEAR')
    expect(d.operator_message).toContain('RECOVERY_RUNNING')
    expect(d.operator_message).toContain('CONNECTED_OK')
  })

  it('execution_safe false + engine not alive precedes recovery message', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'OK',
        recovery_state: 'RECOVERY_RUNNING',
        adoption_grace_expired_active: false,
        engine_alive: false,
      },
      null
    )
    expect(d.overall_execution_reason).toBe('ENGINE_NOT_ALIVE')
  })

  it('execution_safe false + timetable not validated → TIMETABLE_NOT_READY', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'OK',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: false,
        engine_alive: true,
      },
      {
        timestamp_chicago: '',
        recovery_state_allowed: true,
        kill_switch_allowed: true,
        execution_safe: false,
        timetable_validated: false,
        stream_armed: [],
        session_slot_time_valid: true,
        trading_date_set: true,
      }
    )
    expect(d.overall_execution_reason).toBe('TIMETABLE_NOT_READY')
  })

  it('all clear → SAFE / SAFE', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: true,
        reconciliation_gate_state: 'OK',
        recovery_state: 'RECOVERY_COMPLETE',
        adoption_grace_expired_active: false,
      },
      null
    )
    expect(d.overall_execution_severity).toBe('SAFE')
    expect(d.overall_execution_reason).toBe('SAFE')
    expect(d.execution_blocked).toBe(false)
  })

  it('missing required fields → UNKNOWN / UNKNOWN', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        recovery_state: 'CONNECTED_OK',
      },
      null
    )
    expect(d.overall_execution_severity).toBe('UNKNOWN')
    expect(d.overall_execution_reason).toBe('UNKNOWN')
    expect(d.execution_blocked).toBe(true)
  })

  it('execution_safe false with no gate payload → EXECUTION_UNSAFE with deterministic tail', () => {
    const d = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'OK',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: false,
        engine_alive: true,
      },
      null
    )
    expect(d.overall_execution_reason).toBe('EXECUTION_UNSAFE')
    expect(d.operator_message).toContain('execution_safe=false')
    expect(d.operator_message).toContain('CONNECTED_OK')
  })

  it('stable operator messages for reasons', () => {
    expect(EXECUTION_REASON_OPERATOR_MESSAGES.KILL_SWITCH).toContain('Kill switch')
    expect(EXECUTION_REASON_OPERATOR_MESSAGES.TIMETABLE_DRIFT).toContain('different timetable')
    expect(EXECUTION_REASON_OPERATOR_MESSAGES.SAFE).toBe('Execution safe')
  })
})

describe('deriveOperatorSystemStatus', () => {
  it('does not downgrade helper CRITICAL when instruments are SAFE', () => {
    const status = baseStatus({
      kill_switch_active: true,
      execution_safe: false,
    })
    const snap = { ES: { status: 'SAFE' as const } }
    expect(deriveOperatorSystemStatus(status, null, snap)).toBe('CRITICAL')
  })
})
