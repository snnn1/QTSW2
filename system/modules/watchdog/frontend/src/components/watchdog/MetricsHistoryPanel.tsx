/**
 * MetricsHistoryPanel - Long-term reliability trends (Phase 8)
 */
import type { MetricsHistoryPeriod } from '../../services/watchdogApi'

interface MetricsHistoryPanelProps {
  byPeriod: MetricsHistoryPeriod[]
  loading?: boolean
  granularity?: 'week' | 'month'
}

export function MetricsHistoryPanel({
  byPeriod,
  loading,
  granularity = 'week',
}: MetricsHistoryPanelProps) {
  const labelKey = granularity === 'month' ? 'month_start' : 'week_start'
  const label = granularity === 'month' ? 'Month' : 'Week'

  if (loading) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">Reliability Trends</div>
        <div className="text-sm text-gray-500">Loading...</div>
      </div>
    )
  }
  if (byPeriod.length === 0) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">Reliability Trends</div>
        <div className="text-sm text-gray-500">No history yet</div>
      </div>
    )
  }

  return (
    <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
      <div className="text-sm font-semibold text-gray-300 mb-2">
        Reliability Trends
        <span className="ml-2 text-xs font-normal text-gray-500">by {label.toLowerCase()}</span>
      </div>
      <div className="space-y-1 text-xs max-h-40 overflow-y-auto">
        {byPeriod.slice(-10).reverse().map((p) => (
          <div key={p[labelKey as keyof MetricsHistoryPeriod] ?? ''} className="flex justify-between py-0.5">
            <span className="text-gray-400">{p[labelKey as keyof MetricsHistoryPeriod] ?? '—'}</span>
            <span className="text-gray-300">
              D:{p.disconnect_incidents} E:{p.engine_stalls} DS:{p.data_stalls}
            </span>
          </div>
        ))}
      </div>
    </div>
  )
}
