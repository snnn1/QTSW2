/**
 * CriticalAlertBanner component
 * Groups root alerts so the operator sees one actionable stack, not a wall of repeats.
 */
interface Alert {
  type: 'critical' | 'degraded'
  message: string
  scrollTo?: string
}

interface CriticalAlertBannerProps {
  alerts: Alert[]
}

export function CriticalAlertBanner({ alerts }: CriticalAlertBannerProps) {
  if (alerts.length === 0) {
    return null
  }

  const groupedAlerts = Array.from(
    alerts.reduce((acc, alert) => {
      const key = `${alert.type}|${alert.scrollTo ?? ''}|${alert.message}`
      const existing = acc.get(key)
      if (existing) {
        existing.count += 1
      } else {
        acc.set(key, { ...alert, count: 1 })
      }
      return acc
    }, new Map<string, Alert & { count: number }>()).values()
  )
  const criticalCount = alerts.filter((alert) => alert.type === 'critical').length
  const degradedCount = alerts.length - criticalCount
  const criticalAlerts = groupedAlerts.filter((alert) => alert.type === 'critical')
  const degradedAlerts = groupedAlerts.filter((alert) => alert.type === 'degraded')
  const visibleAlerts = criticalAlerts.slice(0, 4)
  const hiddenCriticalAlerts = criticalAlerts.slice(4)

  const handleClick = (scrollTo?: string) => {
    if (scrollTo) {
      const element = document.getElementById(scrollTo)
      if (element) {
        element.scrollIntoView({ behavior: 'smooth', block: 'start' })
      }
    }
  }

  return (
    <div className="mt-16 border-y border-slate-800 bg-slate-950/95">
      <div className="watchdog-content px-2 py-2">
        <div className="mb-1 flex flex-wrap items-center justify-between gap-2 text-xs">
          <div className="font-semibold text-slate-100">
            Operator alerts: {criticalCount} critical
            {degradedCount > 0 ? `, ${degradedCount} degraded` : ''}
          </div>
          <div className="text-[11px] text-slate-500">
            Grouped by root signal to reduce alert fatigue.
          </div>
        </div>
        {visibleAlerts.length > 0 ? (
          <div className="flex flex-wrap gap-2">
            {visibleAlerts.map((alert) => (
              <button
                key={`${alert.type}-${alert.scrollTo ?? ''}-${alert.message}`}
                type="button"
                onClick={() => handleClick(alert.scrollTo)}
                className="rounded-full border border-red-500/60 bg-red-950/65 px-3 py-1 text-left text-xs font-semibold text-red-100"
              >
                {alert.message}
                {alert.count > 1 ? ` (${alert.count})` : ''}
              </button>
            ))}
          </div>
        ) : degradedAlerts.length > 0 ? (
          <div className="rounded-lg border border-amber-700/50 bg-amber-950/25 px-3 py-2 text-xs text-amber-100">
            No blocking criticals. {degradedCount} warning signal{degradedCount === 1 ? '' : 's'} available below.
          </div>
        ) : null}
        {hiddenCriticalAlerts.length > 0 && (
          <details className="mt-2 text-xs text-slate-300">
            <summary className="cursor-pointer text-slate-400">
              {hiddenCriticalAlerts.length} additional critical alert{hiddenCriticalAlerts.length === 1 ? '' : 's'}
            </summary>
            <div className="mt-2 flex flex-wrap gap-2">
              {hiddenCriticalAlerts.map((alert) => (
                <button
                  key={`hidden-${alert.type}-${alert.scrollTo ?? ''}-${alert.message}`}
                  type="button"
                  onClick={() => handleClick(alert.scrollTo)}
                  className="rounded-full border border-red-500/50 bg-red-950/45 px-3 py-1 text-left text-xs font-semibold text-red-100"
                >
                  {alert.message}
                  {alert.count > 1 ? ` (${alert.count})` : ''}
                </button>
              ))}
            </div>
          </details>
        )}
        {degradedAlerts.length > 0 && (
          <details className="mt-2 text-xs text-slate-300">
            <summary className="cursor-pointer text-amber-300">
              {degradedCount} warning signal{degradedCount === 1 ? '' : 's'}
            </summary>
            <div className="mt-2 flex flex-wrap gap-2">
              {degradedAlerts.map((alert) => (
                <button
                  key={`degraded-${alert.type}-${alert.scrollTo ?? ''}-${alert.message}`}
                  type="button"
                  onClick={() => handleClick(alert.scrollTo)}
                  className="rounded-full border border-amber-500/50 bg-amber-950/45 px-3 py-1 text-left text-xs font-semibold text-amber-100"
                >
                  {alert.message}
                  {alert.count > 1 ? ` (${alert.count})` : ''}
                </button>
              ))}
            </div>
          </details>
        )}
      </div>
    </div>
  )
}
