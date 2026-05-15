import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { OperatorSafetyBar } from './OperatorSafetyBar'
import type { EngineRunSummary, WatchdogStatus } from '../../types/watchdog'
import type { OverallExecutionDerived } from '../../utils/executionSeverity'

const status: WatchdogStatus = {
  timestamp_chicago: '',
  engine_alive: true,
  engine_activity_state: 'ACTIVE',
  last_engine_tick_chicago: null,
  engine_tick_stall_detected: false,
  recovery_state: 'CONNECTED_OK',
  execution_safe: true,
  reconciliation_gate_state: 'OK',
  adoption_grace_expired_active: false,
  kill_switch_active: false,
  connection_status: 'Connected',
  derived_connection_state: 'STABLE',
  last_connection_event_chicago: null,
  stuck_streams: [],
  execution_blocked_count: 0,
  protective_failures_count: 0,
  data_stall_detected: {},
  market_open: true,
  last_identity_invariants_pass: null,
  last_identity_invariants_event_chicago: null,
  last_identity_violations: [],
  trading_date: '2026-05-13',
  session_trading_date: '2026-05-13',
  position_authority_by_instrument: {},
}

const execution: OverallExecutionDerived = {
  overall_execution_severity: 'SAFE',
  overall_execution_reason: 'SAFE',
  execution_blocked: false,
  tradable: true,
  authority_aggregate: 'REAL',
  reconciliation_gate_diagnostic: 'Reconciliation gate: OK',
}

const unsafeSummary: EngineRunSummary = {
  run_id: '991956c6fb1e40b58a4e6c3c21d9da81',
  date: '2026-05-10',
  mode: 'SIM',
  status: 'FAIL',
  status_reason: 'OPEN_EXPOSURE_AT_SHUTDOWN',
  recommended_action: 'STOP',
  confidence: 'HIGH',
  instruments: ['MNG', 'MYM'],
  trades: 12,
  pnl: null,
  errors: 10,
  key_counts: {
    broker_position_qty_at_shutdown: 8,
    broker_working_orders_at_shutdown: 8,
    open_position_at_shutdown: 4,
    ownership_journal_open_qty_at_shutdown: 8,
  },
  flags: {
    had_crash_or_freeze_signal: true,
  },
  watchdog_overlay: {
    proof_level: 'windows-event-log-proven',
  },
  deployed_runtime: {
    exists: true,
    sha256: 'ae804c4e445742aeb141503448a233690f6e94da3783348f842e6741e2669560',
    path: 'C:\\Users\\jakej\\Documents\\NinjaTrader 8\\bin\\Custom\\Robot.Core.dll',
  },
}

describe('OperatorSafetyBar', () => {
  it('surfaces unsafe shutdown exposure and runtime proof in the first viewport summary', () => {
    render(
      <OperatorSafetyBar
        status={status}
        summary={unsafeSummary}
        overallExecution={execution}
        streams={[]}
        activeIntents={[]}
        unprotectedPositions={[]}
        alerts={[]}
        dataFlowStatus="FLOWING"
        viewMode="live"
      />
    )

    expect(screen.getByText('unknown / 8 run')).toBeInTheDocument()
    expect(screen.getByText('DLL ae804c4e4457')).toBeInTheDocument()
  })

  it('surfaces live working orders from authority snapshots', () => {
    render(
      <OperatorSafetyBar
        status={{
          ...status,
          position_authority_by_instrument: {
            MYM: {
              instrument: 'MYM',
              broker_qty: 4,
              journal_open_qty: 4,
              broker_working_count: 4,
              iea_trusted_working_count: 4,
              source_event: 'RELEASE_READINESS_INPUT_AUDIT',
            },
          },
        }}
        summary={unsafeSummary}
        overallExecution={execution}
        streams={[]}
        activeIntents={[]}
        unprotectedPositions={[]}
        alerts={[]}
        dataFlowStatus="FLOWING"
        viewMode="live"
      />
    )

    expect(screen.getAllByText('4 live / 8 run').length).toBeGreaterThanOrEqual(2)
    expect(screen.getByText('IEA trusted 4; 4 open stream(s) at shutdown')).toBeInTheDocument()
  })
})
