/**
 * Execution Integrity Panel
 * Shows anomaly counts for instant system health visibility.
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

  return (
    <div className="bg-gray-800 rounded-lg p-4">
      <div className="text-sm font-medium text-gray-300 mb-3">Execution Integrity</div>
      <div className="space-y-1 font-mono text-sm">
        {ROWS.map(({ key, label }) => {
          const value = counts[key] ?? 0
          const hasAnomaly = value > 0
          return (
            <div key={key} className="flex justify-between">
              <span className={hasAnomaly ? 'text-amber-400' : 'text-gray-500'}>{label}</span>
              <span className={hasAnomaly ? 'text-amber-400 font-semibold' : 'text-gray-500'}>
                {value}
              </span>
            </div>
          )
        })}
      </div>
    </div>
  )
}
