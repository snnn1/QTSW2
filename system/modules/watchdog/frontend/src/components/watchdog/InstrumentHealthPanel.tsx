/**
 * InstrumentHealthPanel - Per-instrument data status.
 * Compact by default; expands automatically only when an instrument is stale.
 */
import type { InstrumentHealth } from '../../services/watchdogApi'

interface InstrumentHealthPanelProps {
  instruments: InstrumentHealth[]
  loading?: boolean
}

export function InstrumentHealthPanel({ instruments, loading }: InstrumentHealthPanelProps) {
  if (loading) {
    return (
      <div className="watchdog-panel">
        <div className="flex items-center justify-between gap-2">
          <div className="text-sm font-semibold text-gray-300">Instrument Health</div>
          <div className="text-xs text-gray-500">Loading...</div>
        </div>
      </div>
    )
  }

  if (instruments.length === 0) {
    return (
      <div className="watchdog-panel">
        <div className="flex items-center justify-between gap-2">
          <div className="text-sm font-semibold text-gray-300">Instrument Health</div>
          <div className="text-xs text-gray-500">No instruments tracked</div>
        </div>
      </div>
    )
  }

  const stalled = instruments.filter((i) => i.status === 'DATA_STALLED')
  const ok = instruments.filter((i) => i.status === 'OK')
  const hasIssue = stalled.length > 0

  return (
    <details
      open={hasIssue}
      className={`watchdog-panel ${hasIssue ? 'border-amber-600/50 bg-amber-900/20' : ''}`}
    >
      <summary className="flex cursor-pointer list-none items-center justify-between gap-3">
        <span>
          <span className="block text-sm font-semibold text-gray-300">Instrument Health</span>
          <span className="block text-[11px] text-gray-500">
            {ok.length} OK / {stalled.length} stalled
          </span>
        </span>
        <span
          className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${
            hasIssue ? 'bg-amber-500 text-black' : 'bg-emerald-700/80 text-emerald-50'
          }`}
        >
          {hasIssue ? `${stalled.length} stalled` : `${instruments.length} OK`}
        </span>
      </summary>
      <div className="mt-3 max-h-36 space-y-1 overflow-y-auto text-xs">
        {instruments.map((inst) => (
          <div key={inst.instrument} className="flex items-center justify-between gap-3 py-0.5">
            <span className={inst.status === 'DATA_STALLED' ? 'font-mono text-amber-300' : 'font-mono text-gray-300'}>
              {inst.instrument}
            </span>
            <span className={inst.status === 'DATA_STALLED' ? 'text-amber-300' : 'text-green-500'}>
              {inst.status}
            </span>
          </div>
        ))}
      </div>
    </details>
  )
}
