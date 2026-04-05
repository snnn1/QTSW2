/**
 * Session + forced flatten audit table (engine-sourced via /api/watchdog/session-flatten-state).
 */
import { useCallback, useState } from 'react'
import { usePollingInterval } from '../../hooks/usePollingInterval'
import { fetchSessionFlattenState, type SessionFlattenRow } from '../../services/watchdogApi'

const STATUS_STYLE: Record<string, { color: string }> = {
  NOT_TRIGGERED: { color: '#6b7280' },
  TRIGGERED: { color: '#ca8a04' },
  FAILED: { color: '#ea580c' },
  CONFIRMED: { color: '#16a34a' },
  TIMEOUT: { color: '#dc2626' },
  EXPOSURE_REMAINS: { color: '#b91c1c' },
}

function statusColor(status: string): string {
  return STATUS_STYLE[status]?.color ?? '#9ca3af'
}

export function SessionFlattenTab() {
  const [rows, setRows] = useState<SessionFlattenRow[]>([])
  const [error, setError] = useState<string | null>(null)
  const [lastPoll, setLastPoll] = useState<Date | null>(null)

  const poll = useCallback(async () => {
    const res = await fetchSessionFlattenState()
    if (res.error) {
      setError(res.error)
      return
    }
    setError(null)
    setRows(Array.isArray(res.data) ? res.data : [])
    setLastPoll(new Date())
  }, [])

  usePollingInterval(poll, 5000)

  return (
    <div className="space-y-4">
      <div className="flex justify-between items-center text-sm text-gray-400 gap-4">
        <span title="Rollup per instrument and session class (S1/S2). Multiple timetable streams can combine into one row.">
          Source: robot engine events only (no watchdog-side session math). Polls every 5s.{' '}
          <span className="text-gray-500 border-b border-dotted border-gray-600 cursor-help">
            Rollup: one row per instrument / session — streams may combine.
          </span>
        </span>
        {lastPoll && (
          <span>Updated {lastPoll.toLocaleTimeString()}</span>
        )}
      </div>
      {error && (
        <div className="text-red-400 text-sm" role="alert">
          {error}
        </div>
      )}
      <div className="overflow-x-auto rounded-lg border border-gray-700">
        <table className="min-w-full text-sm text-left">
          <thead className="bg-gray-900 text-gray-300 border-b border-gray-700">
            <tr>
              <th className="px-3 py-2 font-medium">Trading Date</th>
              <th className="px-3 py-2 font-medium">Session</th>
              <th className="px-3 py-2 font-medium">Instrument</th>
              <th className="px-3 py-2 font-medium">Has Session</th>
              <th className="px-3 py-2 font-medium">Session Close (CT)</th>
              <th className="px-3 py-2 font-medium">Flatten Trigger (CT)</th>
              <th className="px-3 py-2 font-medium">Source</th>
              <th className="px-3 py-2 font-medium">Status</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-800 bg-gray-950">
            {rows.length === 0 && (
              <tr>
                <td colSpan={8} className="px-3 py-6 text-gray-500 text-center">
                  No session/flatten visibility rows yet — run the robot with a loaded timetable and session resolver.
                </td>
              </tr>
            )}
            {rows.map((r, i) => (
              <tr
                key={`${r.trading_date}-${r.session_class}-${r.instrument}-${i}`}
                className={
                  r.flatten_status === 'TIMEOUT' || r.flatten_status === 'EXPOSURE_REMAINS'
                    ? 'bg-red-950/40 hover:bg-red-950/55 border-l-4 border-l-red-600'
                    : r.flatten_status === 'FAILED'
                      ? 'bg-orange-950/25 hover:bg-orange-950/40 border-l-4 border-l-orange-600'
                      : 'hover:bg-gray-900/80'
                }
              >
                <td className="px-3 py-2 text-gray-200 whitespace-nowrap">{r.trading_date}</td>
                <td className="px-3 py-2 text-gray-200">{r.session_class}</td>
                <td className="px-3 py-2 text-gray-200">{r.instrument || '—'}</td>
                <td className="px-3 py-2 text-gray-200">
                  {r.has_session === true ? 'Yes' : r.has_session === false ? 'No' : '—'}
                </td>
                <td className="px-3 py-2 text-gray-200 font-mono">{r.session_close_chicago || '—'}</td>
                <td className="px-3 py-2 text-gray-200 font-mono">{r.flatten_trigger_chicago || '—'}</td>
                <td className="px-3 py-2 text-gray-200">{r.source || '—'}</td>
                <td
                  className="px-3 py-2 font-medium"
                  style={{ color: statusColor(r.flatten_status) }}
                >
                  {r.flatten_status}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
