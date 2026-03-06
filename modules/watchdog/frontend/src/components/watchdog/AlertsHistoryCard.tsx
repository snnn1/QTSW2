/**
 * AlertsHistoryCard - Recent alert history from Phase 1 ledger
 */
import type { AlertRecord } from '../../services/watchdogApi'

const ALERT_TYPE_LABELS: Record<string, string> = {
  NINJATRADER_PROCESS_STOPPED: 'NinjaTrader Process Stopped',
  ROBOT_HEARTBEAT_LOST: 'Robot Heartbeat Lost',
  CONNECTION_LOST_SUSTAINED: 'Connection Lost Sustained',
  POTENTIAL_ORPHAN_POSITION: 'Potential Orphan Position',
  CONFIRMED_ORPHAN_POSITION: 'Confirmed Orphan Position',
  LOG_FILE_STALLED: 'Log File Stalled',
}

function formatUtc(utc?: string): string {
  if (!utc) return '—'
  try {
    const d = new Date(utc)
    return d.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' })
  } catch {
    return utc
  }
}

interface AlertsHistoryCardProps {
  recent: AlertRecord[]
  loading?: boolean
}

export function AlertsHistoryCard({ recent, loading }: AlertsHistoryCardProps) {
  if (loading) {
    return (
      <div className="bg-gray-800 rounded-lg p-4">
        <div className="text-sm text-gray-400">Loading alert history...</div>
      </div>
    )
  }

  return (
    <div className="bg-gray-800 rounded-lg p-4">
      <div className="text-sm font-semibold text-gray-300 mb-2">Alert History (24h)</div>
      {recent.length === 0 ? (
        <div className="text-xs text-gray-500">No alerts in the last 24 hours</div>
      ) : (
        <div className="space-y-2 max-h-48 overflow-y-auto">
          {recent.slice(0, 15).map((r, i) => (
            <div
              key={r.alert_id ?? r.resolved_utc ?? i}
              className="text-xs bg-gray-900/50 rounded p-2 border border-gray-700"
            >
              {r.event === 'resolved' ? (
                <div className="text-gray-500">
                  Resolved: {ALERT_TYPE_LABELS[r.alert_type ?? ''] ?? r.alert_type} — {formatUtc(r.resolved_utc)}
                </div>
              ) : (
                <>
                  <div className="text-amber-300/90">
                    {ALERT_TYPE_LABELS[r.alert_type ?? ''] ?? r.alert_type?.replace(/_/g, ' ')}
                  </div>
                  <div className="text-gray-500 mt-0.5">
                    {formatUtc(r.first_seen_utc)} — {formatUtc(r.last_seen_utc)}
                  </div>
                </>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
