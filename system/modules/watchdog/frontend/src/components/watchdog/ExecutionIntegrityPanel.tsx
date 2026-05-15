/**
 * Execution Integrity Panel
 */
interface ExecutionIntegrityCounts {
  ghost_fills: number
  protective_drift: number
  orphan_orders: number
  duplicate_orders: number
  position_drift: number
  exposure_integrity: number
  stuck_orders: number
  latency_spikes: number
  recovery_loops: number
  order_lifecycle_invalid: number
}

interface ExecutionIntegrityPanelProps {
  counts: ExecutionIntegrityCounts | null
}

const ROWS: Array<{ key: keyof ExecutionIntegrityCounts; label: string }> = [
  { key: 'ghost_fills', label: 'Ghost Fills' },
  { key: 'protective_drift', label: 'Protective Drift' },
  { key: 'orphan_orders', label: 'Orphan Orders' },
  { key: 'duplicate_orders', label: 'Duplicate Orders' },
  { key: 'position_drift', label: 'Position Drift' },
  { key: 'exposure_integrity', label: 'Exposure Integrity' },
  { key: 'stuck_orders', label: 'Stuck Orders' },
  { key: 'latency_spikes', label: 'Latency Spikes' },
  { key: 'recovery_loops', label: 'Recovery Loops' },
  { key: 'order_lifecycle_invalid', label: 'Order Lifecycle Invalid' },
]

export function ExecutionIntegrityPanel({ counts }: ExecutionIntegrityPanelProps) {
  if (!counts) return null

  const anomalyCount = ROWS.reduce((sum, row) => sum + (counts[row.key] ?? 0), 0)

  return (
    <details
      open={anomalyCount > 0}
      className={`watchdog-panel ${anomalyCount > 0 ? 'border-amber-600/50 bg-amber-900/20' : ''}`}
    >
      <summary className="flex cursor-pointer list-none items-center justify-between gap-3">
        <span>
          <span className="block text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-500">
            Integrity
          </span>
          <span className="block text-sm font-semibold text-gray-300">Execution Integrity</span>
        </span>
        <span
          className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${
            anomalyCount > 0 ? 'bg-amber-500 text-black' : 'bg-emerald-700/80 text-emerald-50'
          }`}
        >
          {anomalyCount > 0 ? `${anomalyCount} anomalies` : '0 anomalies'}
        </span>
      </summary>
      <div className="mt-3 space-y-1 font-mono text-sm">
        {ROWS.map(({ key, label }) => {
          const value = counts[key] ?? 0
          const hasAnomaly = value > 0
          return (
            <div key={key} className="flex justify-between rounded-lg px-2 py-1 hover:bg-slate-800/45">
              <span className={hasAnomaly ? 'text-amber-300' : 'text-gray-500'}>{label}</span>
              <span className={hasAnomaly ? 'font-semibold text-amber-300' : 'text-gray-500'}>
                {value}
              </span>
            </div>
          )
        })}
      </div>
    </details>
  )
}
