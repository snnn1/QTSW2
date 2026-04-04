/**
 * Canonical operator / execution severity — single source of truth for watchdog + operator UI.
 * Priority order is fixed (first match wins). Do not reimplement in components.
 */
import type { RiskGateStatus, WatchdogStatus } from '../types/watchdog'

/** Minimal status slice for derivation (+ optional gate endpoint merge). */
export interface ExecutionSeverityStatusInput {
  execution_safe?: boolean
  kill_switch_active?: boolean
  reconciliation_gate_state?: string
  recovery_state?: string
  adoption_grace_expired_active?: boolean
  /** True when robot heartbeat timetable identity ≠ system timetable (Watchdog computed). */
  timetable_drift?: boolean
}

export type OverallExecutionSeverity = 'SAFE' | 'WARNING' | 'CRITICAL' | 'UNKNOWN'

/** Stable machine reason codes for tests and consistent UI keys */
export type OverallExecutionReason =
  | 'KILL_SWITCH'
  | 'TIMETABLE_DRIFT'
  | 'RECONCILIATION_GATE_FAIL_CLOSED'
  | 'DISCONNECT_FAIL_CLOSED'
  | 'ADOPTION_GRACE_EXPIRED'
  | 'RECONCILIATION_GATE_ENGAGED'
  | 'EXECUTION_UNSAFE'
  | 'SAFE'
  | 'UNKNOWN'

export interface OverallExecutionDerived {
  overall_execution_severity: OverallExecutionSeverity
  overall_execution_reason: OverallExecutionReason
  /** True whenever trading must not be treated as allowed (includes UNKNOWN). */
  execution_blocked: boolean
}

function mergeExecutionFields(
  status: ExecutionSeverityStatusInput | null | undefined,
  gates: RiskGateStatus | null | undefined
): {
  execution_safe: boolean | undefined
  kill_switch_active: boolean | undefined
  reconciliation_gate_state: string | undefined
  recovery_state: string | undefined
  adoption_grace_expired_active: boolean | undefined
  complete: boolean
} {
  if (!status) {
    return {
      execution_safe: undefined,
      kill_switch_active: undefined,
      reconciliation_gate_state: undefined,
      recovery_state: undefined,
      adoption_grace_expired_active: undefined,
      complete: false,
    }
  }

  let execution_safe = status.execution_safe
  if (execution_safe === undefined && gates) {
    execution_safe =
      gates.execution_safe ?? (gates.recovery_state_allowed && gates.kill_switch_allowed)
  }

  let kill_switch_active = status.kill_switch_active
  if (kill_switch_active === undefined && gates && typeof gates.kill_switch_allowed === 'boolean') {
    kill_switch_active = !gates.kill_switch_allowed
  }

  const reconciliation_gate_state = status.reconciliation_gate_state
  const recovery_state = status.recovery_state
  const adoption_grace_expired_active = status.adoption_grace_expired_active

  const complete =
    typeof execution_safe === 'boolean' &&
    typeof kill_switch_active === 'boolean' &&
    typeof reconciliation_gate_state === 'string' &&
    reconciliation_gate_state.length > 0 &&
    typeof recovery_state === 'string' &&
    recovery_state.length > 0 &&
    typeof adoption_grace_expired_active === 'boolean'

  return {
    execution_safe,
    kill_switch_active,
    reconciliation_gate_state,
    recovery_state,
    adoption_grace_expired_active,
    complete,
  }
}

/**
 * Derive overall execution severity from watchdog status (and optionally risk-gates endpoint for backfill).
 */
export function deriveOverallExecutionStatus(
  status: ExecutionSeverityStatusInput | null | undefined,
  gates?: RiskGateStatus | null | undefined
): OverallExecutionDerived {
  const m = mergeExecutionFields(status, gates)
  if (!m.complete) {
    return {
      overall_execution_severity: 'UNKNOWN',
      overall_execution_reason: 'UNKNOWN',
      execution_blocked: true,
    }
  }

  const execution_safe = m.execution_safe as boolean
  const kill_switch_active = m.kill_switch_active as boolean
  const reconciliation_gate_state = m.reconciliation_gate_state as string
  const recovery_state = m.recovery_state as string
  const adoption_grace_expired_active = m.adoption_grace_expired_active as boolean

  if (kill_switch_active) {
    return {
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'KILL_SWITCH',
      execution_blocked: true,
    }
  }
  if (status?.timetable_drift === true) {
    return {
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'TIMETABLE_DRIFT',
      execution_blocked: true,
    }
  }
  if (reconciliation_gate_state === 'FAIL_CLOSED') {
    return {
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'RECONCILIATION_GATE_FAIL_CLOSED',
      execution_blocked: true,
    }
  }
  if (recovery_state === 'DISCONNECT_FAIL_CLOSED') {
    return {
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'DISCONNECT_FAIL_CLOSED',
      execution_blocked: true,
    }
  }
  if (adoption_grace_expired_active && execution_safe === false) {
    return {
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'ADOPTION_GRACE_EXPIRED',
      execution_blocked: true,
    }
  }
  if (reconciliation_gate_state === 'ENGAGED') {
    return {
      overall_execution_severity: 'WARNING',
      overall_execution_reason: 'RECONCILIATION_GATE_ENGAGED',
      execution_blocked: true,
    }
  }
  if (execution_safe === false) {
    return {
      overall_execution_severity: 'WARNING',
      overall_execution_reason: 'EXECUTION_UNSAFE',
      execution_blocked: true,
    }
  }

  return {
    overall_execution_severity: 'SAFE',
    overall_execution_reason: 'SAFE',
    execution_blocked: false,
  }
}

/** Plain-English operator copy; same everywhere. */
export const EXECUTION_REASON_OPERATOR_MESSAGES: Record<OverallExecutionReason, string> = {
  KILL_SWITCH: 'Kill switch active — execution blocked',
  TIMETABLE_DRIFT: 'Robot is running a different timetable than the system',
  RECONCILIATION_GATE_FAIL_CLOSED: 'Reconciliation gate fail-closed — execution blocked',
  DISCONNECT_FAIL_CLOSED: 'Disconnect fail-closed — execution blocked',
  ADOPTION_GRACE_EXPIRED: 'Adoption grace expired — execution blocked',
  RECONCILIATION_GATE_ENGAGED: 'Reconciliation gate engaged — execution paused pending validation',
  EXECUTION_UNSAFE: 'Execution not safe — trading blocked',
  SAFE: 'Execution safe',
  UNKNOWN: 'Execution state unknown',
}

export function executionReasonToOperatorMessage(reason: OverallExecutionReason): string {
  return EXECUTION_REASON_OPERATOR_MESSAGES[reason]
}

/** Optional scroll target for watchdog page */
export function executionSeverityAlert(
  derived: OverallExecutionDerived
): { type: 'critical' | 'degraded'; message: string; scrollTo?: string } | null {
  if (derived.overall_execution_severity === 'SAFE') return null
  return {
    type: derived.overall_execution_severity === 'CRITICAL' ? 'critical' : 'degraded',
    message: executionReasonToOperatorMessage(derived.overall_execution_reason),
    scrollTo: 'risk-gates-panel',
  }
}

export type OperatorSystemStatus = 'SAFE' | 'WARNING' | 'CRITICAL' | 'UNKNOWN'

/**
 * Operator console top-level System status: execution helper first (never downgrade CRITICAL), then transport / instruments.
 */
export function deriveOperatorSystemStatus(
  status: WatchdogStatus | null | undefined,
  gates: RiskGateStatus | null | undefined,
  snapshot: Record<string, { status: string }> | null | undefined
): OperatorSystemStatus {
  const exec = deriveOverallExecutionStatus(status, gates)
  if (exec.overall_execution_severity === 'CRITICAL') return 'CRITICAL'
  if (exec.overall_execution_severity === 'UNKNOWN') {
    if (!snapshot || Object.keys(snapshot).length === 0) return 'UNKNOWN'
    const statuses = Object.values(snapshot).map((s) => s.status)
    if (statuses.includes('CRITICAL')) return 'CRITICAL'
    if (statuses.includes('WARNING')) return 'WARNING'
    return 'UNKNOWN'
  }

  const derivedConn = status?.derived_connection_state
  if (derivedConn === 'LOST' || status?.connection_status === 'ConnectionLost' || status?.connection_status === 'ConnectionLostSustained') {
    return 'CRITICAL'
  }

  if (exec.overall_execution_severity === 'WARNING') {
    if (snapshot && Object.keys(snapshot).length > 0) {
      const statuses = Object.values(snapshot).map((s) => s.status)
      if (statuses.includes('CRITICAL')) return 'CRITICAL'
    }
    return 'WARNING'
  }

  if (!snapshot || Object.keys(snapshot).length === 0) return 'SAFE'
  const statuses = Object.values(snapshot).map((s) => s.status)
  if (statuses.includes('CRITICAL')) return 'CRITICAL'
  if (statuses.includes('WARNING')) return 'WARNING'
  return 'SAFE'
}
