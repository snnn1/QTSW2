/**
 * ActiveIncidentsPanel - Currently ongoing incidents (separate from historical)
 */
import type { ActiveIncident } from '../../services/watchdogApi'

function formatDuration(sec: number): string {
  if (sec < 0) return '0s'
  if (sec < 60) return `${sec}s`
  const m = Math.floor(sec / 60)
  const s = Math.round(sec % 60)
  return s > 0 ? `${m}m ${s}s` : `${m}m`
}

function typeColor(type: string): string {
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

interface ActiveIncidentsPanelProps {
  active: ActiveIncident[]
  loading?: boolean
}

export function ActiveIncidentsPanel({ active, loading }: ActiveIncidentsPanelProps) {
  if (loading) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">Active Incidents</div>
        <div className="text-sm text-gray-500">Loading...</div>
      </div>
    )
  }
  if (active.length === 0) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">Active Incidents</div>
        <div className="text-sm text-gray-500">None</div>
      </div>
    )
  }

  return (
    <div className="rounded-lg p-4 border bg-amber-900/20 border-amber-600/30">
      <div className="text-sm font-semibold text-amber-400 mb-2">
        Active Incidents
        <span className="ml-2 text-xs font-normal text-gray-500">{active.length} ongoing</span>
      </div>
      <div className="space-y-2">
        {active.map((inc) => (
          <div key={inc.type} className="text-xs border-l-2 border-amber-500/50 pl-2 py-1">
            <span className={typeColor(inc.type)}>{inc.type}</span>
            <span className="text-gray-400 ml-1">
              started {inc.started} · {formatDuration(inc.duration_sec)}
            </span>
            {inc.instruments && inc.instruments.length > 0 && (
              <div className="text-gray-500 mt-0.5 truncate">{inc.instruments.join(', ')}</div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
