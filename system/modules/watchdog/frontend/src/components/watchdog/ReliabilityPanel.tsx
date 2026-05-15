/**
 * ReliabilityPanel - System reliability metrics (Phase 6)
 * Uptime %, disconnects, engine stalls, data stalls, etc.
 */
import type { ReliabilityMetrics } from '../../services/watchdogApi'

function formatDuration(sec: number): string {
  if (sec < 0) return '0s'
  if (sec < 60) return `${Math.round(sec)}s`
  const m = Math.floor(sec / 60)
  const s = Math.round(sec % 60)
  return s > 0 ? `${m}m ${s}s` : `${m}m`
}

interface ReliabilityPanelProps {
  metrics: ReliabilityMetrics | null
  loading?: boolean
}

export function ReliabilityPanel({ metrics, loading }: ReliabilityPanelProps) {
  if (loading) {
    return (
      <div className="watchdog-panel">
        <div className="flex items-center justify-between gap-2">
          <div className="text-sm font-semibold text-gray-300">System Reliability</div>
          <div className="text-xs text-gray-500">Loading...</div>
        </div>
      </div>
    )
  }
  if (!metrics) {
    return (
      <div className="watchdog-panel">
        <div className="flex items-center justify-between gap-2">
          <div className="text-sm font-semibold text-gray-300">System Reliability</div>
          <div className="text-xs text-gray-500">No metrics</div>
        </div>
      </div>
    )
  }

  const conn = metrics.connection
  const engine = metrics.engine
  const data = metrics.data
  const ff = metrics.forced_flatten
  const recon = metrics.reconciliation

  const uptimeOk = conn.uptime_percent >= 99
  const hasIssues =
    conn.disconnect_incidents > 0 ||
    engine.engine_stalls > 0 ||
    data.data_stalls > 0 ||
    ff.forced_flatten_count > 0 ||
    recon.reconciliation_mismatch_count > 0

  const summary = `${conn.uptime_percent.toFixed(1)}% uptime | stalls ${engine.engine_stalls + data.data_stalls} | recon ${recon.reconciliation_mismatch_count}`

  return (
    <details
      open={hasIssues}
      className={`watchdog-panel ${
        hasIssues ? 'border-amber-600/50 bg-amber-900/20' : ''
      }`}
    >
      <summary className="flex cursor-pointer list-none items-center justify-between gap-3">
        <span>
          <span className="block text-sm font-semibold text-gray-300">System Reliability</span>
          <span className="block text-[11px] text-gray-500">Last {metrics.window_hours}h</span>
        </span>
        <span
          className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${
            hasIssues ? 'bg-amber-500 text-black' : 'bg-emerald-700/80 text-emerald-50'
          }`}
          title={summary}
        >
          {hasIssues ? 'Review' : 'OK'}
        </span>
      </summary>
      <div className="mt-3 space-y-1.5 text-sm">
        <div className="flex justify-between">
          <span className="text-gray-400">Uptime</span>
          <span className={uptimeOk ? 'text-green-400' : 'text-amber-400'}>
            {conn.uptime_percent.toFixed(1)}%
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-400">Disconnects</span>
          <span className={conn.disconnect_incidents > 0 ? 'text-amber-400' : 'text-gray-300'}>
            {conn.disconnect_incidents}
          </span>
        </div>
        {conn.disconnect_incidents > 0 && (
          <div className="flex justify-between text-xs">
            <span className="text-gray-500">Max downtime</span>
            <span className="text-gray-400">{formatDuration(conn.max_disconnect_duration)}</span>
          </div>
        )}
        <div className="flex justify-between">
          <span className="text-gray-400">Engine stalls</span>
          <span className={engine.engine_stalls > 0 ? 'text-red-400' : 'text-gray-300'}>
            {engine.engine_stalls}
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-400">Data stalls</span>
          <span className={data.data_stalls > 0 ? 'text-orange-400' : 'text-gray-300'}>
            {data.data_stalls}
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-400">Forced flattens</span>
          <span className={ff.forced_flatten_count > 0 ? 'text-red-500' : 'text-gray-300'}>
            {ff.forced_flatten_count}
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-400">Reconciliation mismatches</span>
          <span className={recon.reconciliation_mismatch_count > 0 ? 'text-yellow-400' : 'text-gray-300'}>
            {recon.reconciliation_mismatch_count}
          </span>
        </div>
      </div>
    </details>
  )
}
