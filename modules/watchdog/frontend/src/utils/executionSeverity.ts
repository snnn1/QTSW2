/**
 * Canonical operator / execution severity — single source of truth for watchdog + operator UI.
 * execution_safe comes only from GET /status (backend _compute_execution_safe); gates endpoint must match.
 * Reconciliation gate ENGAGED is diagnostic (non-blocking) when execution_safe is true; FAIL_CLOSED blocks via execution_safe.
 */
import type { RiskGateStatus, WatchdogStatus } from '../types/watchdog'

/** Minimal status slice for derivation. execution_safe must come from /status only. */
export interface ExecutionSeverityStatusInput {
  execution_safe?: boolean
  kill_switch_active?: boolean
  reconciliation_gate_state?: string
  reconciliation_gate_last_detail?: Record<string, unknown> | null
  recovery_state?: string
  adoption_grace_expired_active?: boolean
  /** True when robot heartbeat timetable identity ≠ system timetable (Watchdog computed). */
  timetable_drift?: boolean
  /** Watchdog GET /status — when false, execution is blocked for liveness reasons */
  engine_alive?: boolean
  enabled_streams_unknown?: boolean
  timetable_unavailable?: boolean
  /** For authority aggregate line (observability). */
  position_authority_by_instrument?: Record<string, { authority_state?: string }>
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
  | 'ENGINE_NOT_ALIVE'
  | 'TIMETABLE_NOT_READY'
  | 'RECOVERY_NOT_CLEAR'
  | 'SESSION_SLOT_TIME_INVALID'
  | 'TRADING_DATE_NOT_SET'
  | 'RISK_GATES_RECOVERY_BLOCKED'
  | 'EXECUTION_UNSAFE'
  | 'SAFE'
  | 'UNKNOWN'

export interface OverallExecutionDerived {
  overall_execution_severity: OverallExecutionSeverity
  overall_execution_reason: OverallExecutionReason
  /** True when operator must not treat overlay as trade-ready (UNKNOWN counts as blocked). */
  execution_blocked: boolean
  /**
   * When set, overrides EXECUTION_REASON_OPERATOR_MESSAGES for display (deterministic detail).
   */
  operator_message?: string
  /** Mirrors GET /status execution_safe — overlay tradability (canonical). */
  tradable: boolean
  /** Aggregate authority_state across instruments from robot mirror. */
  authority_aggregate: 'REAL' | 'RECOVERY' | 'UNKNOWN' | 'MIXED' | 'NONE'
  /** Short hint when tradable is false (overlay). */
  overlay_blocker_hint?: string
  /** Reconciliation gate mirror for the diagnostic line; ENGAGED does not imply tradable=false. */
  reconciliation_gate_diagnostic: string
}

const RECOVERY_CLEARED_STATES = ['CONNECTED_OK', 'RECOVERY_COMPLETE'] as const

function recoveryIsCleared(recoveryState: string): boolean {
  return (RECOVERY_CLEARED_STATES as readonly string[]).includes(recoveryState)
}

export function aggregateAuthorityFromStatus(
  byInst: Record<string, { authority_state?: string }> | undefined
): OverallExecutionDerived['authority_aggregate'] {
  const vals = Object.values(byInst ?? {})
    .map((s) => String(s.authority_state ?? '').trim().toUpperCase())
    .filter(Boolean)
  if (vals.length === 0) return 'NONE'
  const set = new Set(vals)
  if (set.size > 1) return 'MIXED'
  const only = [...set][0]
  if (only === 'REAL') return 'REAL'
  if (only === 'RECOVERY') return 'RECOVERY'
  if (only === 'UNKNOWN') return 'UNKNOWN'
  return 'MIXED'
}

function formatGateDiagnostic(status: ExecutionSeverityStatusInput | null | undefined): string {
  const g = (status?.reconciliation_gate_state ?? 'n/a').trim() || 'n/a'
  const d = status?.reconciliation_gate_last_detail
  if (g === 'OK') return 'Reconciliation gate: OK'
  let extra = ''
  if (d && typeof d === 'object' && d !== null && 'event_type' in d) {
    extra = ` (event: ${String((d as Record<string, unknown>).event_type ?? '')})`
  }
  return `Reconciliation gate: ${g}${extra}`
}

/** Last-resort deterministic line when execution_safe is false but no single predicate matched earlier rules. */
function buildMultifactorUnsafeDetail(
  status: ExecutionSeverityStatusInput | null | undefined,
  gates: RiskGateStatus | null | undefined,
  recovery_state: string
): string {
  const parts: string[] = []
  if (status?.engine_alive === false) parts.push('engine_alive=false')
  if (gates?.timetable_validated === false) parts.push('timetable_validated=false')
  if (status?.enabled_streams_unknown === true) parts.push('enabled_streams_unknown=true')
  if (status?.timetable_unavailable === true) parts.push('timetable_unavailable=true')
  if (!recoveryIsCleared(recovery_state) && recovery_state !== 'DISCONNECT_FAIL_CLOSED') {
    parts.push(`recovery_state=${recovery_state}`)
  }
  if (gates && gates.session_slot_time_valid === false) parts.push('session_slot_time_valid=false')
  if (gates && gates.trading_date_set === false) parts.push('trading_date_set=false')
  if (gates && gates.recovery_state_allowed === false) parts.push('recovery_state_allowed=false')
  if (parts.length > 0) {
    return `Overlay blocked — ${parts.join('; ')}`
  }
  return `Overlay blocked — execution_safe=false; recovery_state=${recovery_state}; see diagnostic line for gate mirror`
}

function mergeExecutionFields(
  status: ExecutionSeverityStatusInput | null | undefined,
  _gates: RiskGateStatus | null | undefined
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

  /** Canonical: only /status.execution_safe (no gates fallback). */
  const execution_safe = status.execution_safe

  let kill_switch_active = status.kill_switch_active
  if (kill_switch_active === undefined && _gates && typeof _gates.kill_switch_allowed === 'boolean') {
    kill_switch_active = !_gates.kill_switch_allowed
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
 * Derive overall execution severity from watchdog /status (gates optional for kill-switch backfill only).
 */
export function deriveOverallExecutionStatus(
  status: ExecutionSeverityStatusInput | null | undefined,
  gates?: RiskGateStatus | null | undefined
): OverallExecutionDerived {
  const agg = aggregateAuthorityFromStatus(status?.position_authority_by_instrument)
  const gateDiag = formatGateDiagnostic(status)

  const m = mergeExecutionFields(status, gates)
  if (!m.complete) {
    return {
      overall_execution_severity: 'UNKNOWN',
      overall_execution_reason: 'UNKNOWN',
      execution_blocked: true,
      tradable: false,
      authority_aggregate: agg,
      overlay_blocker_hint: 'Incomplete status snapshot',
      reconciliation_gate_diagnostic: gateDiag,
    }
  }

  const execution_safe = m.execution_safe as boolean
  const kill_switch_active = m.kill_switch_active as boolean
  const reconciliation_gate_state = m.reconciliation_gate_state as string
  const recovery_state = m.recovery_state as string
  const adoption_grace_expired_active = m.adoption_grace_expired_active as boolean

  const meta = (partial: Partial<OverallExecutionDerived>): OverallExecutionDerived =>
    ({
      authority_aggregate: agg,
      reconciliation_gate_diagnostic: gateDiag,
      tradable: execution_safe,
      ...partial,
    }) as OverallExecutionDerived

  if (kill_switch_active) {
    return meta({
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'KILL_SWITCH',
      execution_blocked: true,
      tradable: false,
      overlay_blocker_hint: 'Kill switch',
    })
  }
  if (status?.timetable_drift === true) {
    return meta({
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'TIMETABLE_DRIFT',
      execution_blocked: true,
      tradable: false,
      overlay_blocker_hint: 'Timetable identity drift',
    })
  }
  if (recovery_state === 'DISCONNECT_FAIL_CLOSED') {
    return meta({
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'DISCONNECT_FAIL_CLOSED',
      execution_blocked: true,
      tradable: false,
      overlay_blocker_hint: 'Disconnect fail-closed',
    })
  }
  if (adoption_grace_expired_active && execution_safe === false) {
    return meta({
      overall_execution_severity: 'CRITICAL',
      overall_execution_reason: 'ADOPTION_GRACE_EXPIRED',
      execution_blocked: true,
      tradable: false,
      overlay_blocker_hint: 'Adoption grace expired',
    })
  }

  if (execution_safe === false) {
    if (reconciliation_gate_state === 'FAIL_CLOSED') {
      return meta({
        overall_execution_severity: 'CRITICAL',
        overall_execution_reason: 'RECONCILIATION_GATE_FAIL_CLOSED',
        execution_blocked: true,
        tradable: false,
        overlay_blocker_hint: 'Reconciliation gate fail-closed (robot)',
        operator_message:
          'Overlay blocked — reconciliation gate FAIL_CLOSED (robot). See diagnostic for last event detail.',
      })
    }
    if (status?.engine_alive === false) {
      return meta({
        overall_execution_severity: 'WARNING',
        overall_execution_reason: 'ENGINE_NOT_ALIVE',
        execution_blocked: true,
        tradable: false,
        overlay_blocker_hint: 'Engine not alive / stalled',
      })
    }
    const timetableBlock =
      gates?.timetable_validated === false ||
      status?.enabled_streams_unknown === true ||
      status?.timetable_unavailable === true
    if (timetableBlock) {
      return meta({
        overall_execution_severity: 'WARNING',
        overall_execution_reason: 'TIMETABLE_NOT_READY',
        execution_blocked: true,
        tradable: false,
        overlay_blocker_hint: 'Timetable not ready',
      })
    }
    if (!recoveryIsCleared(recovery_state)) {
      return meta({
        overall_execution_severity: 'WARNING',
        overall_execution_reason: 'RECOVERY_NOT_CLEAR',
        execution_blocked: true,
        tradable: false,
        overlay_blocker_hint: 'Recovery not clear',
        operator_message: `Recovery state is ${recovery_state} — overlay blocked until CONNECTED_OK or RECOVERY_COMPLETE`,
      })
    }
    if (gates && gates.session_slot_time_valid === false) {
      return meta({
        overall_execution_severity: 'WARNING',
        overall_execution_reason: 'SESSION_SLOT_TIME_INVALID',
        execution_blocked: true,
        tradable: false,
        overlay_blocker_hint: 'Session slot time invalid',
      })
    }
    if (gates && gates.trading_date_set === false) {
      return meta({
        overall_execution_severity: 'WARNING',
        overall_execution_reason: 'TRADING_DATE_NOT_SET',
        execution_blocked: true,
        tradable: false,
        overlay_blocker_hint: 'Trading date not set',
      })
    }
    if (gates && gates.recovery_state_allowed === false) {
      return meta({
        overall_execution_severity: 'WARNING',
        overall_execution_reason: 'RISK_GATES_RECOVERY_BLOCKED',
        execution_blocked: true,
        tradable: false,
        overlay_blocker_hint: 'Risk gates: recovery path blocked',
      })
    }
    return meta({
      overall_execution_severity: 'WARNING',
      overall_execution_reason: 'EXECUTION_UNSAFE',
      execution_blocked: true,
      tradable: false,
      overlay_blocker_hint: 'See overlay details',
      operator_message: buildMultifactorUnsafeDetail(status, gates, recovery_state),
    })
  }

  if (reconciliation_gate_state === 'ENGAGED') {
    return meta({
      overall_execution_severity: 'WARNING',
      overall_execution_reason: 'RECONCILIATION_GATE_ENGAGED',
      execution_blocked: false,
      tradable: true,
      overlay_blocker_hint: undefined,
      operator_message:
        'Authority / overlay: tradable. Reconciliation gate ENGAGED — diagnostic only (robot validating).',
    })
  }

  return meta({
    overall_execution_severity: 'SAFE',
    overall_execution_reason: 'SAFE',
    execution_blocked: false,
    tradable: true,
    overlay_blocker_hint: undefined,
  })
}

/** Plain-English operator copy; same everywhere. */
export const EXECUTION_REASON_OPERATOR_MESSAGES: Record<OverallExecutionReason, string> = {
  KILL_SWITCH: 'Kill switch active — overlay blocked',
  TIMETABLE_DRIFT: 'Robot is running a different timetable than the system — overlay blocked',
  RECONCILIATION_GATE_FAIL_CLOSED: 'Reconciliation gate FAIL_CLOSED — overlay blocked (robot)',
  DISCONNECT_FAIL_CLOSED: 'Disconnect fail-closed — overlay blocked',
  ADOPTION_GRACE_EXPIRED: 'Adoption grace expired — overlay blocked',
  RECONCILIATION_GATE_ENGAGED:
    'Reconciliation gate ENGAGED — diagnostic only; overlay still tradable when execution_safe is true',
  ENGINE_NOT_ALIVE: 'Engine not alive — overlay blocked',
  TIMETABLE_NOT_READY: 'Timetable not validated or stream enablement unknown — overlay blocked',
  RECOVERY_NOT_CLEAR: 'Recovery not complete — overlay blocked until CONNECTED_OK or RECOVERY_COMPLETE',
  SESSION_SLOT_TIME_INVALID: 'Session slot time is not valid for the stream session (see risk gates)',
  TRADING_DATE_NOT_SET: 'Trading date is not set (see risk gates)',
  RISK_GATES_RECOVERY_BLOCKED: 'Risk gates report recovery path not allowed',
  EXECUTION_UNSAFE: 'Overlay blocked — see risk gates (details unavailable)',
  SAFE: 'Overlay tradable',
  UNKNOWN: 'Execution state unknown',
}

export function executionReasonToOperatorMessage(reason: OverallExecutionReason): string {
  return EXECUTION_REASON_OPERATOR_MESSAGES[reason]
}

/** Prefer deterministic operator_message when deriveOverallExecutionStatus set it. */
export function overallExecutionOperatorMessage(d: OverallExecutionDerived): string {
  if (d.operator_message) return d.operator_message
  return EXECUTION_REASON_OPERATOR_MESSAGES[d.overall_execution_reason]
}

/**
 * One line for "Blocked by (overlay)": reason code plus hint (e.g. ENGINE_NOT_ALIVE — Engine not alive / stalled).
 * Authority can still show REAL while this is non-none — that is expected (overlay vs visibility).
 */
export function formatOverlayBlockedByLine(d: OverallExecutionDerived): string {
  if (d.tradable) return 'none'
  const code = d.overall_execution_reason
  const hint = d.overlay_blocker_hint?.trim()
  if (hint) return `${code} — ${hint}`
  return code
}

/** Shown under SYSTEM STATUS so operators know execution_safe is overlay-driven, not authority-driven. */
export const OVERLAY_TRADABILITY_VS_AUTHORITY_NOTE =
  'Tradable follows overlay rules (engine, timetable, kill switch, gate FAIL_CLOSED, adoption, drift). Authority values are robot-reported visibility only — they do not set execution_safe.'

/** Optional scroll target for watchdog page — only when overlay blocks or CRITICAL. */
export function executionSeverityAlert(
  derived: OverallExecutionDerived
): { type: 'critical' | 'degraded'; message: string; scrollTo?: string } | null {
  if (derived.overall_execution_severity === 'SAFE') return null
  if (!derived.execution_blocked) return null
  return {
    type: derived.overall_execution_severity === 'CRITICAL' ? 'critical' : 'degraded',
    message: overallExecutionOperatorMessage(derived),
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
