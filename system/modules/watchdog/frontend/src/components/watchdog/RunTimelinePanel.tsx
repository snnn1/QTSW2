/**
 * KEY_EVENTS.jsonl timeline (evidence, not verdict).
 */
import { useState } from 'react'
import type { KeyEventRecord } from '../../types/watchdog'

function formatTime(tsUtc: string | undefined): string {
  if (!tsUtc) return '—'
  const d = new Date(tsUtc)
  if (Number.isNaN(d.getTime())) return tsUtc.slice(11, 19) || '—'
  return d.toLocaleTimeString('en-US', {
    hour12: false,
    timeZone: 'America/Chicago',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

function rowText(ev: KeyEventRecord): string {
  const e = ev.event ?? '?'
  const ins = ev.instrument?.trim()
  return ins ? `${e} ${ins}` : e
}

export interface RunTimelinePanelProps {
  events: KeyEventRecord[]
  persistenceRoot?: string | null
  error?: string | null
  /** When API returned available: false */
  artifactReason?: string | null
  loading?: boolean
}

export function RunTimelinePanel({ events, persistenceRoot, error, artifactReason, loading }: RunTimelinePanelProps) {
  const [openIdx, setOpenIdx] = useState<number | null>(null)

  if (loading) {
    return (
      <div className="rounded-lg border border-gray-700 bg-gray-900/60 p-3 text-xs text-gray-500">
        Run timeline: loading…
      </div>
    )
  }

  if (error) {
    return (
      <div className="rounded-lg border border-gray-700 bg-gray-900/60 p-3 text-xs text-red-300">
        Run timeline: {error}
      </div>
    )
  }

  if (artifactReason) {
    return (
      <div className="rounded-lg border border-gray-700 bg-gray-900/60 p-3">
        <h2 className="text-sm font-semibold text-gray-200">Run timeline</h2>
        <p className="mt-2 text-xs text-gray-500">No timeline artifact ({artifactReason}).</p>
      </div>
    )
  }

  return (
    <div className="rounded-lg border border-gray-700 bg-gray-900/60 p-3">
      <div className="mb-2 flex items-center justify-between">
        <h2 className="text-sm font-semibold text-gray-200">Run timeline</h2>
        {persistenceRoot && (
          <span className="max-w-[50%] truncate font-mono text-[10px] text-gray-500" title={persistenceRoot}>
            {persistenceRoot}
          </span>
        )}
      </div>
      {events.length === 0 ? (
        <div className="text-xs text-gray-500">No key events in this run artifact (or file missing).</div>
      ) : (
        <ul className="max-h-80 space-y-1 overflow-y-auto pr-1">
          {events.map((ev, i) => {
            const hasData =
              ev.data != null && typeof ev.data === 'object' && Object.keys(ev.data as object).length > 0
            const expanded = openIdx === i
            return (
              <li key={`${ev.ts_utc ?? ''}-${i}-${ev.event ?? ''}`}>
                <button
                  type="button"
                  className="flex w-full items-start justify-between gap-2 rounded px-1 py-0.5 text-left text-xs hover:bg-gray-800/80"
                  onClick={() => {
                    if (!hasData) return
                    setOpenIdx(expanded ? null : i)
                  }}
                >
                  <span className="shrink-0 font-mono text-gray-500">{formatTime(ev.ts_utc)}</span>
                  <span className="flex-1 font-mono text-gray-200">{rowText(ev)}</span>
                  {ev.reason ? (
                    <span className="max-w-[40%] shrink-0 truncate text-[11px] text-gray-500" title={ev.reason ?? ''}>
                      {ev.reason}
                    </span>
                  ) : (
                    <span className="w-8 shrink-0" />
                  )}
                </button>
                {expanded && hasData && (
                  <pre className="mb-1 ml-8 max-h-40 overflow-auto rounded bg-black/40 p-2 text-[10px] text-gray-400">
                    {JSON.stringify(ev.data, null, 2)}
                  </pre>
                )}
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
