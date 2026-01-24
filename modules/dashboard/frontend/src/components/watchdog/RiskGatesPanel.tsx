/**
 * RiskGatesPanel component
 * Shows execution gate status
 */
import type { RiskGateStatus } from '../../types/watchdog'

interface RiskGatesPanelProps {
  gates: RiskGateStatus | null
  loading: boolean
}

export function RiskGatesPanel({ gates, loading }: RiskGatesPanelProps) {
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
  
  return (
    <div className="bg-gray-800 rounded-lg p-4">
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
            {gates.stream_armed.map((stream) => (
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
