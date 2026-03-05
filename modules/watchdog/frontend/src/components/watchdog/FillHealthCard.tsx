/**
 * FillHealthCard - Execution logging hygiene metrics
 *
 * Shows fill_coverage_rate, unmapped_rate, null_trading_date_rate from new canonical fill logging.
 * Targets: coverage=100%, unmapped=0, null_td=0.
 */
import type { FillHealthInfo } from '../../types/watchdog'

interface FillHealthCardProps {
  fillHealth: FillHealthInfo | null | undefined
}

export function FillHealthCard({ fillHealth }: FillHealthCardProps) {
  if (!fillHealth) {
    return (
      <div className="bg-gray-800 rounded-lg p-4">
        <div className="text-sm text-gray-400 mb-1">Fill Health</div>
        <div className="text-xs text-gray-500">No fill data for today</div>
      </div>
    )
  }

  const ok = fillHealth.fill_health_ok
  const coveragePct = (fillHealth.fill_coverage_rate * 100).toFixed(1)
  const unmappedPct = (fillHealth.unmapped_rate * 100).toFixed(1)
  const nullTdPct = (fillHealth.null_trading_date_rate * 100).toFixed(1)

  return (
    <div className={`rounded-lg p-4 ${ok ? 'bg-gray-800' : 'bg-amber-900/30 border border-amber-600/50'}`}>
      <div className="flex items-center justify-between mb-2">
        <div className="text-sm text-gray-400">Fill Health</div>
        <span
          className={`px-2 py-0.5 rounded text-xs font-medium ${
            ok ? 'bg-green-600/80 text-white' : 'bg-amber-600 text-black'
          }`}
        >
          {ok ? 'OK' : 'WARN'}
        </span>
      </div>
      <div className="space-y-1 text-sm">
        <div className="flex justify-between">
          <span className="text-gray-500">Coverage</span>
          <span className={fillHealth.fill_coverage_rate >= 1 ? 'text-green-400' : 'text-amber-400'}>
            {coveragePct}%
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-500">Unmapped</span>
          <span className={fillHealth.unmapped_rate <= 0 ? 'text-green-400' : 'text-amber-400'}>
            {fillHealth.unmapped_fills} ({unmappedPct}%)
          </span>
        </div>
        <div className="flex justify-between">
          <span className="text-gray-500">Null trading_date</span>
          <span className={fillHealth.null_trading_date_rate <= 0 ? 'text-green-400' : 'text-amber-400'}>
            {fillHealth.null_trading_date_fills} ({nullTdPct}%)
          </span>
        </div>
        <div className="text-xs text-gray-500 mt-2 pt-2 border-t border-gray-700">
          {fillHealth.total_fills} total fills ({fillHealth.trading_date})
        </div>
      </div>
    </div>
  )
}
