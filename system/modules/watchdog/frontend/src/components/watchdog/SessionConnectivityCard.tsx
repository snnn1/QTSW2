/**
 * SessionConnectivityCard - Real-time session-based disconnect metrics
 */
import type { ConnectivityDailySummary, SessionConnectivityInfo } from '../../types/watchdog'

function formatChicagoTime(iso: string | null): string {
  if (!iso) return '-'
  try {
    const d = new Date(iso)
    return d.toLocaleTimeString(undefined, {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    })
  } catch {
    return iso
  }
}

function formatDuration(seconds: number): string {
  if (seconds < 0) return '0s'
  if (seconds < 60) return `${Math.round(seconds)}s`
  const m = Math.floor(seconds / 60)
  const s = Math.round(seconds % 60)
  return s > 0 ? `${m}m ${s}s` : `${m}m`
}

interface SessionConnectivityCardProps {
  sessionConnectivity: SessionConnectivityInfo | null | undefined
  dailySummary: ConnectivityDailySummary | null | undefined
}

export function SessionConnectivityCard({
  sessionConnectivity,
  dailySummary,
}: SessionConnectivityCardProps) {
  const sc = sessionConnectivity
  if (!sc && !dailySummary) return null

  const hasDisconnects =
    (sc?.disconnect_count ?? 0) > 0 || (dailySummary?.disconnect_count ?? 0) > 0

  return (
    <div
      id="session-connectivity-panel"
      className={`watchdog-panel ${
        sc?.currently_disconnected
          ? 'border-red-600/50 bg-red-900/30'
          : hasDisconnects
            ? 'border-amber-600/30 bg-amber-900/20'
            : 'border-gray-700'
      }`}
    >
      <div className="watchdog-panel-header">
        <div>
          <div className="watchdog-panel-kicker">Connectivity</div>
          <div className="watchdog-panel-title">Session Connectivity</div>
        </div>
        {sc && <div className="watchdog-panel-meta">{`${sc.session} | ${sc.trading_date}`}</div>}
      </div>

      {sc?.currently_disconnected && (
        <div className="mb-3 text-sm font-medium text-red-400">Currently disconnected</div>
      )}

      <div className="space-y-2 text-sm">
        {sc && (
          <>
            <div className="flex justify-between">
              <span className="text-gray-400">Disconnects this session</span>
              <span className={sc.disconnect_count > 0 ? 'text-amber-400' : 'text-gray-300'}>
                {sc.disconnect_count}
              </span>
            </div>
            <div className="flex justify-between">
              <span className="text-gray-400">Total downtime</span>
              <span
                className={sc.total_downtime_seconds > 0 ? 'text-amber-400' : 'text-gray-300'}
              >
                {formatDuration(sc.total_downtime_seconds)}
              </span>
            </div>
            {sc.last_disconnect_chicago && (
              <div className="flex justify-between">
                <span className="text-gray-400">Last disconnect</span>
                <span className="text-gray-300">
                  {formatChicagoTime(sc.last_disconnect_chicago)}
                </span>
              </div>
            )}
          </>
        )}

        {dailySummary && (
          <div className="mt-3 border-t border-gray-700/70 pt-3">
            <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.12em] text-slate-500">
              Daily Summary
            </div>
            <div className="space-y-1 text-xs">
              <div className="flex justify-between">
                <span className="text-gray-400">Disconnects today</span>
                <span className="text-gray-300">{dailySummary.disconnect_count}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-gray-400">Avg / max duration</span>
                <span className="text-gray-300">
                  {dailySummary.avg_duration_seconds.toFixed(1)}s /{' '}
                  {dailySummary.max_duration_seconds.toFixed(1)}s
                </span>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
