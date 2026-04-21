/**
 * ActiveAlertsCard - Phase 1 push alerts (process stopped, heartbeat lost, etc.)
 */
import type { ActiveAlert } from '../../types/watchdog'

const ALERT_TYPE_LABELS: Record<string, string> = {
  NINJATRADER_PROCESS_STOPPED: 'NinjaTrader Process Stopped',
  ROBOT_HEARTBEAT_LOST: 'Robot Heartbeat Lost',
  CONNECTION_LOST_SUSTAINED: 'Connection Lost Sustained',
  CONNECTIVITY_INCIDENT: 'Connectivity Incident (5+ disconnects or >120s)',
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
    <div id="active-alerts-panel" className="watchdog-panel border-amber-600/50 bg-amber-950/20">
      <div className="watchdog-panel-header">
        <div>
          <div className="watchdog-panel-kicker text-amber-500">Live Alerts</div>
          <div className="watchdog-panel-title text-amber-200">Active Alerts</div>
        </div>
        <div className="watchdog-panel-meta text-amber-300">{alerts.length} open</div>
      </div>
      <div className="space-y-2">
        {alerts.map((a) => (
          <div
            key={a.alert_id}
            className="rounded-xl border border-amber-700/30 bg-gray-950/45 p-3 text-sm"
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
