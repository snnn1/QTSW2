/**
 * SessionConnectivityCard - Real-time session-based disconnect metrics
 * Shows disconnect count, total downtime, and last disconnect for current session (S1/S2).
 */
import type { SessionConnectivityInfo, ConnectivityDailySummary } from '../../types/watchdog'

function formatChicagoTime(iso: string | null): string {
  if (!iso) return '—'
  try {
    const d = new Date(iso)
    return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
  } catch {
    return iso
  }
}

function formatDuration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)}s`
  const m = Math.floor(seconds / 60)
  const s = Math.round(seconds % 60)
  return s > 0 ? `${m}m ${s}s` : `${m}m`
}

interface SessionConnectivityCardProps {
  sessionConnectivity: SessionConnectivityInfo | null | undefined
  dailySummary: ConnectivityDailySummary | null | undefined
}

export function SessionConnectivityCard({ sessionConnectivity, dailySummary }: SessionConnectivityCardProps) {
  const sc = sessionConnectivity
  // Show card when we have session info (from status poll) or daily summary
  if (!sc && !dailySummary) {
    return null
  }
  const hasDisconnects = (sc?.disconnect_count ?? 0) > 0 || (dailySummary?.disconnect_count ?? 0) > 0

  return (
    <div
      id="session-connectivity-panel"
      className={`rounded-lg p-4 border ${
        sc?.currently_disconnected
          ? 'bg-red-900/30 border-red-600/50'
          : hasDisconnects
          ? 'bg-amber-900/20 border-amber-600/30'
          : 'bg-gray-800 border-gray-700'
      }`}
    >
      <div className="text-sm font-semibold text-gray-300 mb-2">
        Session Connectivity
        {sc && (
          <span className="ml-2 text-xs font-normal text-gray-500">
            {sc.session} · {sc.trading_date}
          </span>
        )}
      </div>

      {sc?.currently_disconnected && (
        <div className="text-sm text-red-400 font-medium mb-2">Currently disconnected</div>
      )}

      <div className="space-y-1 text-sm">
        {sc && (
          <>
            <div className="flex justify-between">
              <span className="text-gray-400">Disconnects this session</span>
              <span className={sc.disconnect_count > 0 ? 'text-amber-400' : 'text-gray-300'}>
                {sc.disconnect_count}
              </span>
            </div>
            <div className="flex justify-between">
              <span className="text-gray-400">Total downtime (session)</span>
              <span className={sc.total_downtime_seconds > 0 ? 'text-amber-400' : 'text-gray-300'}>
                {formatDuration(sc.total_downtime_seconds)}
              </span>
            </div>
            {sc.last_disconnect_chicago && (
              <div className="flex justify-between">
                <span className="text-gray-400">Last disconnect</span>
                <span className="text-gray-300">{formatChicagoTime(sc.last_disconnect_chicago)}</span>
              </div>
            )}
          </>
        )}

        {dailySummary && (
          <div className="mt-2 pt-2 border-t border-gray-700">
            <div className="text-xs text-gray-500 mb-1">Daily summary</div>
            <div className="flex justify-between text-xs">
              <span className="text-gray-400">Disconnects today</span>
              <span>{dailySummary.disconnect_count}</span>
            </div>
            <div className="flex justify-between text-xs">
              <span className="text-gray-400">Avg / max duration</span>
              <span>
                {dailySummary.avg_duration_seconds.toFixed(1)}s / {dailySummary.max_duration_seconds.toFixed(1)}s
              </span>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
