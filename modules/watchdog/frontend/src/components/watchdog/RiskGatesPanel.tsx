/**
 * RiskGatesPanel component
 * Shows execution gate status; execution severity copy/colors come from shared helper only.
 */
import type { RiskGateStatus } from '../../types/watchdog'
import type { OverallExecutionDerived } from '../../utils/executionSeverity'
import { executionReasonToOperatorMessage } from '../../utils/executionSeverity'

interface RiskGatesPanelProps {
  gates: RiskGateStatus | null
  loading: boolean
  overallExecution: OverallExecutionDerived
}

function executionRowTextClass(sev: OverallExecutionDerived['overall_execution_severity']): string {
  switch (sev) {
    case 'SAFE':
      return 'text-green-500'
    case 'WARNING':
      return 'text-amber-500'
    case 'CRITICAL':
      return 'text-red-500'
    default:
      return 'text-gray-500'
  }
}

export function RiskGatesPanel({ gates, loading, overallExecution }: RiskGatesPanelProps) {
  if (loading && !gates) {
    return (
      <div className="bg-gray-800 rounded-lg p-4">
        <h2 className="text-lg font-semibold mb-4">Execution Gates</h2>
        <div className="text-gray-500">Loading...</div>
      </div>
    )
  }

  if (!gates) {
    return (
      <div className="bg-gray-800 rounded-lg p-4">
        <h2 className="text-lg font-semibold mb-4">Execution Gates</h2>
        <div className="text-gray-500">No data</div>
      </div>
    )
  }

  const executionSafe =
    gates.execution_safe ?? (gates.recovery_state_allowed && gates.kill_switch_allowed)
  const reasonMsg = executionReasonToOperatorMessage(overallExecution.overall_execution_reason)
  const rowClass = executionRowTextClass(overallExecution.overall_execution_severity)

  return (
    <div className="bg-gray-800 rounded-lg p-4" data-testid="risk-gates-panel">
      <h2 className="text-lg font-semibold mb-4">Execution Gates</h2>
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <span>Recovery State</span>
          <span className={gates.recovery_state_allowed ? 'text-green-500' : 'text-red-500'}>
            {gates.recovery_state_allowed ? '✅' : '❌'}
          </span>
        </div>
        <div className="flex items-center justify-between">
          <span>Kill Switch</span>
          <span className={gates.kill_switch_allowed ? 'text-green-500' : 'text-red-500'}>
            {gates.kill_switch_allowed ? '✅' : '❌'}
          </span>
        </div>
        <div className="flex flex-col gap-1 border-t border-gray-700/50 pt-2 mt-1">
          <div className="flex items-center justify-between">
            <span className="font-medium" title={reasonMsg}>
              Execution safe
            </span>
            <span className={executionSafe ? 'text-green-500' : 'text-red-500'}>
              {executionSafe ? '✅' : '❌'}
            </span>
          </div>
          <div className={`text-xs ${rowClass}`} data-testid="risk-gates-execution-severity-message">
            {reasonMsg}
          </div>
        </div>
        <div className="flex items-center justify-between">
          <span>Timetable Validated</span>
          <span className={gates.timetable_validated ? 'text-green-500' : 'text-red-500'}>
            {gates.timetable_validated ? '✅' : '❌'}
          </span>
        </div>
        <div className="flex items-center justify-between">
          <span>Slot Time Valid</span>
          <span className={gates.session_slot_time_valid ? 'text-green-500' : 'text-red-500'}>
            {gates.session_slot_time_valid ? '✅' : '❌'}
          </span>
        </div>
        <div className="flex items-center justify-between">
          <span>Trading Date Set</span>
          <span className={gates.trading_date_set ? 'text-green-500' : 'text-red-500'}>
            {gates.trading_date_set ? '✅' : '❌'}
          </span>
        </div>
        <div className="mt-4">
          <div className="text-sm font-semibold mb-2">Stream Armed:</div>
          <div className="space-y-1">
            {Array.from(
              new Map(gates.stream_armed.map((stream) => [stream.stream, stream])).values()
            ).map((stream) => (
              <div key={stream.stream} className="flex items-center justify-between text-sm">
                <span className="font-mono">{stream.stream}</span>
                <span className={stream.armed ? 'text-green-500' : 'text-red-500'}>
                  {stream.armed ? '✅' : '❌'}
                </span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  )
}
