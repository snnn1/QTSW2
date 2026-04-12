/**
 * InstrumentHealthPanel - Per-instrument data status (Phase 6)
 */
import type { InstrumentHealth } from '../../services/watchdogApi'

function formatChicagoTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  try {
    const d = new Date(iso)
    return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
  } catch {
    return iso
  }
}

interface InstrumentHealthPanelProps {
  instruments: InstrumentHealth[]
  loading?: boolean
}

export function InstrumentHealthPanel({ instruments, loading }: InstrumentHealthPanelProps) {
  if (loading) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">Instrument Health</div>
        <div className="text-sm text-gray-500">Loading...</div>
      </div>
    )
  }
  if (instruments.length === 0) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">Instrument Health</div>
        <div className="text-sm text-gray-500">No instruments tracked</div>
      </div>
    )
  }

  const stalled = instruments.filter((i) => i.status === 'DATA_STALLED')
  const ok = instruments.filter((i) => i.status === 'OK')

  return (
    <div
      className={`rounded-lg p-4 border ${
        stalled.length > 0 ? 'bg-amber-900/20 border-amber-600/30' : 'bg-gray-800 border-gray-700'
      }`}
    >
      <div className="text-sm font-semibold text-gray-300 mb-2">
        Instrument Health
        <span className="ml-2 text-xs font-normal text-gray-500">
          {ok.length} OK · {stalled.length} stalled
        </span>
      </div>
      <div className="space-y-1 text-xs max-h-32 overflow-y-auto">
        {instruments.map((inst) => (
          <div key={inst.instrument} className="flex justify-between items-center py-0.5">
            <span className={inst.status === 'DATA_STALLED' ? 'text-amber-400' : 'text-gray-300'}>
              {inst.instrument}
            </span>
            <span className={inst.status === 'DATA_STALLED' ? 'text-amber-400' : 'text-green-500'}>
              {inst.status}
            </span>
          </div>
        ))}
      </div>
    </div>
  )
}
