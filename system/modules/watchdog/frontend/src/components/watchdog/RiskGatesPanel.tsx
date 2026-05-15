/**
 * RiskGatesPanel component
 */
import type { RiskGateStatus } from '../../types/watchdog'
import type { OverallExecutionDerived } from '../../utils/executionSeverity'
import { overallExecutionOperatorMessage } from '../../utils/executionSeverity'

interface RiskGatesPanelProps {
  gates: RiskGateStatus | null
  loading: boolean
  overallExecution: OverallExecutionDerived
  tradable?: boolean
}

function executionRowTextClass(sev: OverallExecutionDerived['overall_execution_severity']): string {
  switch (sev) {
    case 'SAFE':
      return 'text-green-400'
    case 'WARNING':
      return 'text-amber-400'
    case 'CRITICAL':
      return 'text-red-400'
    default:
      return 'text-gray-500'
  }
}

function gateCheck(allowed: boolean) {
  return (
    <span className={allowed ? 'text-green-400' : 'text-red-400'}>{allowed ? 'Yes' : 'No'}</span>
  )
}

export function RiskGatesPanel({ gates, loading, overallExecution, tradable }: RiskGatesPanelProps) {
  if (loading && !gates) {
    return (
      <div className="watchdog-panel">
        <div className="watchdog-panel-header">
          <div>
            <div className="watchdog-panel-kicker">Controls</div>
            <div className="watchdog-panel-title">Execution Gates</div>
          </div>
        </div>
        <div className="text-gray-500">Loading...</div>
      </div>
    )
  }

  if (!gates) {
    return (
      <div className="watchdog-panel">
        <div className="watchdog-panel-header">
          <div>
            <div className="watchdog-panel-kicker">Controls</div>
            <div className="watchdog-panel-title">Execution Gates</div>
          </div>
        </div>
        <div className="text-gray-500">No data</div>
      </div>
    )
  }

  const executionSafe =
    typeof tradable === 'boolean'
      ? tradable
      : gates.execution_safe ?? (gates.recovery_state_allowed && gates.kill_switch_allowed)
  const reasonMsg = overallExecutionOperatorMessage(overallExecution)
  const rowClass = executionRowTextClass(overallExecution.overall_execution_severity)
  const uniqueStreamArmed = Array.from(new Map(gates.stream_armed.map((stream) => [stream.stream, stream])).values())
  const armedCount = uniqueStreamArmed.filter((stream) => stream.armed).length

  return (
    <div className="watchdog-panel" data-testid="risk-gates-panel">
      <div className="watchdog-panel-header">
        <div>
          <div className="watchdog-panel-kicker">Controls</div>
          <div className="watchdog-panel-title">Execution Gates</div>
        </div>
        <div className="watchdog-panel-meta">{executionSafe ? 'Tradable' : 'Blocked'}</div>
      </div>
      <div className="space-y-2 text-sm">
        <div className="flex items-center justify-between">
          <span className="text-gray-300">Recovery state</span>
          {gateCheck(gates.recovery_state_allowed)}
        </div>
        <div className="flex items-center justify-between">
          <span className="text-gray-300">Kill switch clear</span>
          {gateCheck(gates.kill_switch_allowed)}
        </div>
        <div className="mt-1 rounded-xl border border-gray-700/70 bg-slate-900/45 p-3">
          <div className="flex items-center justify-between">
            <span className="font-medium text-gray-200" title="Canonical /status execution_safe">
              Overlay tradable
            </span>
            {gateCheck(executionSafe)}
          </div>
          <div className={`mt-2 text-xs ${rowClass}`} data-testid="risk-gates-execution-severity-message">
            {reasonMsg}
          </div>
        </div>
        <div className="flex items-center justify-between">
          <span className="text-gray-300">Timetable validated</span>
          {gateCheck(gates.timetable_validated)}
        </div>
        <div className="flex items-center justify-between">
          <span className="text-gray-300">Slot time valid</span>
          {gateCheck(gates.session_slot_time_valid)}
        </div>
        <div className="flex items-center justify-between">
          <span className="text-gray-300">Trading date set</span>
          {gateCheck(gates.trading_date_set)}
        </div>
        <div className="mt-3 border-t border-gray-700/70 pt-3">
          <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.12em] text-slate-500">
            Stream Armed
          </div>
          {armedCount === 0 && uniqueStreamArmed.length > 0 && (
            <div className="mb-2 rounded-lg border border-slate-700/70 bg-slate-900/45 px-2 py-1 text-[11px] text-slate-400">
              System may be tradable while no stream is currently armed.
            </div>
          )}
          <div className="space-y-1">
            {uniqueStreamArmed.map(
              (stream) => (
                <div key={stream.stream} className="flex items-center justify-between text-sm">
                  <span className="font-mono text-gray-300">{stream.stream}</span>
                  {gateCheck(stream.armed)}
                </div>
              )
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
