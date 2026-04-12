/**
 * Last N engine verdicts + display-only stability (does not override engine truth).
 */
import type { RecentRunsResponse } from '../../types/watchdog'

function statusChipClass(st: string): string {
  const u = st.toUpperCase()
  if (u === 'OK') return 'bg-emerald-900/60 text-emerald-100 border-emerald-700/50'
  if (u === 'WARN') return 'bg-amber-900/50 text-amber-100 border-amber-600/50'
  if (u === 'FAIL') return 'bg-red-900/60 text-red-100 border-red-700/50'
  return 'bg-gray-800 text-gray-300 border-gray-600'
}

function stabilityClass(label: string): string {
  const u = label.toUpperCase()
  if (u === 'STABLE') return 'bg-emerald-900/40 text-emerald-200 border-emerald-700/40'
  if (u === 'DEGRADED') return 'bg-amber-900/40 text-amber-100 border-amber-600/40'
  if (u === 'UNSTABLE') return 'bg-red-900/40 text-red-100 border-red-700/40'
  return 'bg-gray-800 text-gray-400 border-gray-600'
}

export interface QuantHealthStripProps {
  recent: RecentRunsResponse | null
  error: string | null
  selectedRunRoot: string | null
  onSelectRunRoot: (runRoot: string) => void
}

export function QuantHealthStrip({ recent, error, selectedRunRoot, onSelectRunRoot }: QuantHealthStripProps) {
  if (error) {
    return (
      <div className="rounded-lg border border-gray-700 bg-gray-900/60 p-3 text-xs text-red-300">
        Recent runs: {error}
      </div>
    )
  }

  if (!recent || !recent.runs.length) {
    return (
      <div className="rounded-lg border border-gray-700 bg-gray-900/60 p-3 text-xs text-gray-500">
        Quant health: no recent run summaries found.
      </div>
    )
  }

  return (
    <div className="rounded-lg border border-gray-700 bg-gray-900/60 p-3">
      <div className="flex flex-wrap items-center gap-3">
        <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">Quant watchdog</div>
        <span
          className={`rounded border px-2 py-0.5 text-xs font-semibold ${stabilityClass(recent.stability)}`}
          title="Display-only aggregation from last summaries"
        >
          {recent.stability}
        </span>
      </div>
      <div className="mt-2 flex flex-wrap gap-2">
        {recent.runs.map((row) => {
          const st = row.summary?.status ?? '?'
          const dir = row.summary_path.replace(/[/\\]summary\.json$/i, '')
          const active = selectedRunRoot === dir
          return (
            <button
              key={row.summary_path}
              type="button"
              title={row.summary_path}
              onClick={() => onSelectRunRoot(dir)}
              className={`rounded border px-2 py-1 text-xs font-mono transition ${statusChipClass(st)} ${
                active ? 'ring-1 ring-sky-400' : 'hover:opacity-90'
              }`}
            >
              {st}
            </button>
          )
        })}
      </div>
      <p className="mt-2 text-[11px] text-gray-500">
        Last {recent.runs.length} run verdicts from summary.json — click to inspect that run&apos;s summary and timeline.
      </p>
    </div>
  )
}
