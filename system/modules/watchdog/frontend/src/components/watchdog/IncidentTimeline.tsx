/**
 * IncidentTimeline - List recent incidents from incidents.jsonl (Phase 6)
 */
import type { IncidentRecord } from '../../services/watchdogApi'

function formatChicagoTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  try {
    const d = new Date(iso)
    return d.toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    })
  } catch {
    return iso
  }
}

function formatDuration(sec: number | undefined): string {
  if (sec == null || sec < 0) return '—'
  if (sec < 60) return `${sec}s`
  const m = Math.floor(sec / 60)
  const s = Math.round(sec % 60)
  return s > 0 ? `${m}m ${s}s` : `${m}m`
}

function typeColor(type: string | undefined): string {
  switch (type) {
    case 'CONNECTION_LOST':
      return 'text-amber-400'
    case 'ENGINE_STALLED':
      return 'text-red-400'
    case 'DATA_STALL':
      return 'text-orange-400'
    case 'FORCED_FLATTEN':
      return 'text-red-500'
    case 'RECONCILIATION_QTY_MISMATCH':
      return 'text-yellow-400'
    default:
      return 'text-gray-400'
  }
}

interface IncidentTimelineProps {
  incidents: IncidentRecord[]
  loading?: boolean
}

export function IncidentTimeline({ incidents, loading }: IncidentTimelineProps) {
  return (
    <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
      <div className="text-sm font-semibold text-gray-300 mb-2">Incident Timeline</div>
      {loading ? (
        <div className="text-sm text-gray-500">Loading...</div>
      ) : incidents.length === 0 ? (
        <div className="text-sm text-gray-500">No recent incidents</div>
      ) : (
        <div className="space-y-2 max-h-48 overflow-y-auto">
          {incidents.slice(0, 20).map((inc, i) => (
            <div
              key={inc.incident_id ?? i}
              className="text-xs border-l-2 border-gray-600 pl-2 py-0.5"
            >
              <span className={typeColor(inc.type)}>{inc.type ?? 'UNKNOWN'}</span>
              <span className="text-gray-500 ml-1">
                {formatChicagoTime(inc.start_ts)} · {formatDuration(inc.duration_sec)}
              </span>
              {inc.instruments && inc.instruments.length > 0 && (
                <div className="text-gray-500 mt-0.5 truncate">
                  {inc.instruments.join(', ')}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
