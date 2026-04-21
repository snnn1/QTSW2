/**
 * FillHealthCard - Execution logging hygiene metrics
 */
import type { FillHealthInfo } from '../../types/watchdog'

interface FillHealthCardProps {
  fillHealth: FillHealthInfo | null | undefined
}

export function FillHealthCard({ fillHealth }: FillHealthCardProps) {
  if (!fillHealth) {
    return (
      <div className="watchdog-panel">
        <div className="watchdog-panel-header">
          <div>
            <div className="watchdog-panel-kicker">Execution Logging</div>
            <div className="watchdog-panel-title">Fill Health</div>
          </div>
        </div>
        <div className="text-xs text-gray-500">No fill data for today</div>
      </div>
    )
  }

  const ok = fillHealth.fill_health_ok
  const coveragePct = (fillHealth.fill_coverage_rate * 100).toFixed(1)
  const unmappedPct = (fillHealth.unmapped_rate * 100).toFixed(1)
  const nullTdPct = (fillHealth.null_trading_date_rate * 100).toFixed(1)

  return (
    <div className={`watchdog-panel ${ok ? '' : 'border-amber-600/50 bg-amber-900/20'}`}>
      <div className="watchdog-panel-header">
        <div>
          <div className="watchdog-panel-kicker">Execution Logging</div>
          <div className="watchdog-panel-title">Fill Health</div>
        </div>
        <span
          className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${
            ok ? 'bg-green-600/80 text-white' : 'bg-amber-500 text-black'
          }`}
        >
          {ok ? 'OK' : 'Warn'}
        </span>
      </div>
      <div className="space-y-2 text-sm">
        <div className="flex justify-between">
          <span className="text-gray-400">Coverage</span>
          <span className={fillHealth.fill_coverage_rate >= 1 ? 'text-green-400' : 'text-amber-400'}>
            {coveragePct}%
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-400">Unmapped</span>
          <span className={fillHealth.unmapped_rate <= 0 ? 'text-green-400' : 'text-amber-400'}>
            {fillHealth.unmapped_fills} ({unmappedPct}%)
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-400">Null trading date</span>
          <span
            className={fillHealth.null_trading_date_rate <= 0 ? 'text-green-400' : 'text-amber-400'}
          >
            {fillHealth.null_trading_date_fills} ({nullTdPct}%)
          </span>
        </div>
        {(fillHealth.broker_flatten_fill_count ?? 0) > 0 ||
        (fillHealth.execution_update_unknown_order_critical_count ?? 0) > 0 ||
        (fillHealth.execution_fill_blocked_count ?? 0) > 0 ||
        (fillHealth.execution_fill_unmapped_count ?? 0) > 0 ? (
          <div className="mt-3 space-y-1 border-t border-gray-700/70 pt-3 text-xs text-amber-300">
            {(fillHealth.broker_flatten_fill_count ?? 0) > 0 && (
              <div>Broker flatten: {fillHealth.broker_flatten_fill_count}</div>
            )}
            {(fillHealth.execution_update_unknown_order_critical_count ?? 0) > 0 && (
              <div>Unknown order critical: {fillHealth.execution_update_unknown_order_critical_count}</div>
            )}
            {(fillHealth.execution_fill_blocked_count ?? 0) > 0 && (
              <div>Fill blocked: {fillHealth.execution_fill_blocked_count}</div>
            )}
            {(fillHealth.execution_fill_unmapped_count ?? 0) > 0 && (
              <div>Fill unmapped: {fillHealth.execution_fill_unmapped_count}</div>
            )}
          </div>
        ) : null}
        <div className="mt-3 border-t border-gray-700/70 pt-3 text-xs text-gray-500">
          {fillHealth.total_fills} total fills ({fillHealth.trading_date})
        </div>
      </div>
    </div>
  )
}
