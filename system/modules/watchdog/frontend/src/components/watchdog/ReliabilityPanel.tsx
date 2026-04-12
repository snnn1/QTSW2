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
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">System Reliability</div>
        <div className="text-sm text-gray-500">Loading...</div>
      </div>
    )
  }
  if (!metrics) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">System Reliability</div>
        <div className="text-sm text-gray-500">No metrics</div>
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

  return (
    <div
      className={`rounded-lg p-4 border ${
        hasIssues ? 'bg-amber-900/20 border-amber-600/30' : 'bg-gray-800 border-gray-700'
      }`}
    >
      <div className="text-sm font-semibold text-gray-300 mb-2">
        System Reliability
        <span className="ml-2 text-xs font-normal text-gray-500">
          Last {metrics.window_hours}h
        </span>
      </div>
      <div className="space-y-2 text-sm">
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
    </div>
  )
}
