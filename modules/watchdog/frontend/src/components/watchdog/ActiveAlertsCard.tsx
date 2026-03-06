/**
 * ActiveAlertsCard - Phase 1 push alerts (process stopped, heartbeat lost, etc.)
 */
import type { ActiveAlert } from '../../types/watchdog'

const ALERT_TYPE_LABELS: Record<string, string> = {
  NINJATRADER_PROCESS_STOPPED: 'NinjaTrader Process Stopped',
  ROBOT_HEARTBEAT_LOST: 'Robot Heartbeat Lost',
  CONNECTION_LOST_SUSTAINED: 'Connection Lost Sustained',
  POTENTIAL_ORPHAN_POSITION: 'Potential Orphan Position',
  CONFIRMED_ORPHAN_POSITION: 'Confirmed Orphan Position',
  LOG_FILE_STALLED: 'Log File Stalled',
}

function formatUtc(utc: string): string {
  try {
    const d = new Date(utc)
    return d.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'medium' })
  } catch {
    return utc
  }
}

interface ActiveAlertsCardProps {
  alerts: ActiveAlert[]
}

export function ActiveAlertsCard({ alerts }: ActiveAlertsCardProps) {
  if (alerts.length === 0) {
    return null
  }

  return (
    <div id="active-alerts-panel" className="bg-gray-800 rounded-lg p-4 border border-amber-600/50">
      <div className="text-sm font-semibold text-amber-400 mb-2">
        Active Alerts ({alerts.length})
      </div>
      <div className="space-y-2">
        {alerts.map((a) => (
          <div
            key={a.alert_id}
            className="text-sm bg-gray-900/50 rounded p-2 border border-gray-700"
          >
            <div className="font-medium text-amber-300">
              {ALERT_TYPE_LABELS[a.alert_type] ?? a.alert_type.replace(/_/g, ' ')}
            </div>
            <div className="text-xs text-gray-400 mt-1">
              First seen: {formatUtc(a.first_seen_utc)} · Last: {formatUtc(a.last_seen_utc)}
            </div>
            {a.context && Object.keys(a.context).length > 0 && (
              <div className="text-xs text-gray-500 mt-1 font-mono">
                {JSON.stringify(a.context)}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
