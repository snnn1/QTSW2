/**
 * MetricsHistoryPanel - long-term reliability trends.
 * Historical by design, so it is collapsed by default.
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
      <div className="watchdog-panel">
        <div className="flex items-center justify-between gap-2">
          <div className="text-sm font-semibold text-gray-300">Reliability Trends</div>
          <div className="text-xs text-gray-500">Loading...</div>
        </div>
      </div>
    )
  }

  if (byPeriod.length === 0) {
    return (
      <div className="watchdog-panel">
        <div className="flex items-center justify-between gap-2">
          <div className="text-sm font-semibold text-gray-300">Reliability Trends</div>
          <div className="text-xs text-gray-500">No history yet</div>
        </div>
      </div>
    )
  }

  return (
    <details className="watchdog-panel">
      <summary className="flex cursor-pointer list-none items-center justify-between gap-3">
        <span>
          <span className="block text-sm font-semibold text-gray-300">Reliability Trends</span>
          <span className="block text-[11px] text-gray-500">Historical by {label.toLowerCase()}</span>
        </span>
        <span className="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-slate-300">
          {byPeriod.length} periods
        </span>
      </summary>
      <div className="mt-3 max-h-40 space-y-1 overflow-y-auto text-xs">
        {byPeriod.slice(-10).reverse().map((p) => (
          <div key={String(p[labelKey as keyof MetricsHistoryPeriod] ?? '')} className="flex justify-between gap-3 py-0.5">
            <span className="text-gray-400">{String(p[labelKey as keyof MetricsHistoryPeriod] ?? '-')}</span>
            <span className="font-mono text-gray-300">
              D:{p.disconnect_incidents} E:{p.engine_stalls} DS:{p.data_stalls}
            </span>
          </div>
        ))}
      </div>
    </details>
  )
}
