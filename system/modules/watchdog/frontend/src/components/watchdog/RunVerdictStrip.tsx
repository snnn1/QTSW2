/**
 * Compact engine run verdict (summary.json read-through only).
 */
import type { EngineRunSummary } from '../../types/watchdog'
import { isRunSummaryUnavailable } from '../../types/watchdog'

function statusAccent(status: string): string {
  const u = status.toUpperCase()
  if (u === 'OK') return 'border-emerald-700/80 text-emerald-100'
  if (u === 'WARN') return 'border-amber-600/90 text-amber-100'
  if (u === 'FAIL') return 'border-red-700/90 text-red-100'
  return 'border-gray-600 text-gray-200'
}

function notableFlagLine(flags: EngineRunSummary['flags'] | undefined): string | null {
  if (!flags) return null
  const parts: string[] = []
  if (flags.had_recovery) parts.push('Recovery occurred')
  if (flags.had_flatten) parts.push('Flatten occurred')
  if (flags.had_mismatch_block) parts.push('Mismatch block')
  if (flags.had_commit_failure) parts.push('Commit failure')
  if (flags.had_execution_block) parts.push('Execution block')
  if (flags.had_order_rejection) parts.push('Order rejection')
  if (flags.had_protective_failure) parts.push('Protective failure')
  if (flags.had_crash_or_freeze_signal) parts.push('Crash/freeze signal')
  if (flags.had_platform_disable_signal) parts.push('Platform disable')
  if (flags.had_ninjatrader_platform_exception) parts.push('NinjaTrader platform exception')
  if (parts.length === 0) return null
  return parts.join(' • ')
}

export interface RunVerdictStripProps {
  summary: import('../../types/watchdog').RunSummaryResult | null
  peekActive: boolean
  onClearPeek?: () => void
  autoFollowPlayback?: boolean
  activePlaybackRunId?: string | null
  activePlaybackRunRoot?: string | null
  onEnableAutoFollow?: () => void
  /** When the summary request failed entirely (no payload). */
  clientError?: string | null
}

function activePlaybackLabel(runId?: string | null, runRoot?: string | null): string | null {
  if (runId && runId.trim()) return runId.trim()
  if (!runRoot) return null
  const parts = runRoot.replace(/\\/g, '/').split('/').filter(Boolean)
  return parts.length ? parts[parts.length - 1] : null
}

export function RunVerdictStrip({
  summary,
  peekActive,
  onClearPeek,
  autoFollowPlayback = false,
  activePlaybackRunId,
  activePlaybackRunRoot,
  onEnableAutoFollow,
  clientError,
}: RunVerdictStripProps) {
  const playbackLabel = activePlaybackLabel(activePlaybackRunId, activePlaybackRunRoot)

  if (clientError && !summary) {
    return (
      <div className="rounded-lg border border-red-900/60 bg-gray-900/80 p-4 text-sm text-red-300">
        Run summary could not be loaded: {clientError}
      </div>
    )
  }

  if (!summary) {
    return (
      <div className="rounded-lg border border-gray-700 bg-gray-900/80 p-4 text-sm text-gray-400">
        Loading run summary…
      </div>
    )
  }

  if (isRunSummaryUnavailable(summary)) {
    return (
      <div className="rounded-lg border border-gray-700 bg-gray-900/80 p-4">
        <div className="text-sm font-medium text-gray-200">No active run summary available</div>
        <p className="mt-2 text-xs text-gray-500">
          Watchdog live signals may still be available, but no engine run verdict was found
          {summary.reason ? ` (${summary.reason})` : ''}.
        </p>
      </div>
    )
  }

  const s = summary as EngineRunSummary
  const runLabel = `${s.date}__${s.mode}__${s.run_id || '—'}`
  const accent = statusAccent(s.status)
  const flagLine = notableFlagLine(s.flags)

  return (
    <div className={`rounded-lg border bg-gray-900/90 p-4 ${accent}`}>
      {peekActive && onClearPeek && (
        <div className="mb-3 flex items-center justify-between border-b border-gray-700/60 pb-2">
          <span className="text-xs text-amber-200/90">Run-scoped dashboard mode is active</span>
          <button
            type="button"
            className="text-xs text-sky-400 hover:text-sky-300"
            onClick={onClearPeek}
          >
            Back to active run
          </button>
        </div>
      )}
      {activePlaybackRunRoot && (
        <div className="mb-3 flex items-center justify-between gap-3 border-b border-gray-700/60 pb-2">
          <span className="text-xs text-sky-200/90">
            {autoFollowPlayback
              ? `Following active playback run${playbackLabel ? ` ${playbackLabel}` : ''}`
              : `Active playback run detected${playbackLabel ? `: ${playbackLabel}` : ''}`}
          </span>
          {!autoFollowPlayback && onEnableAutoFollow && (
            <button
              type="button"
              className="text-xs text-sky-400 hover:text-sky-300"
              onClick={onEnableAutoFollow}
            >
              Follow active playback
            </button>
          )}
        </div>
      )}
      <div className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm md:grid-cols-3 lg:grid-cols-6">
        <div>
          <div className="text-xs uppercase text-gray-500">Run</div>
          <div className="font-mono text-xs break-all">{runLabel}</div>
        </div>
        <div>
          <div className="text-xs uppercase text-gray-500">Status</div>
          <div className="font-semibold">{s.status}</div>
        </div>
        <div>
          <div className="text-xs uppercase text-gray-500">Reason</div>
          <div className="break-all text-xs">{s.status_reason}</div>
        </div>
        <div>
          <div className="text-xs uppercase text-gray-500">Action</div>
          <div>{s.recommended_action ?? '—'}</div>
        </div>
        <div>
          <div className="text-xs uppercase text-gray-500">Confidence</div>
          <div>{s.confidence ?? '—'}</div>
        </div>
      </div>
      <div className="mt-3 text-xs text-gray-400">
        Trades: {s.trades} &nbsp; Errors: {s.errors} &nbsp; Instruments:{' '}
        {s.instruments && s.instruments.length > 0 ? s.instruments.join(', ') : '—'}
      </div>
      {flagLine && <div className="mt-2 text-xs text-gray-500">{flagLine}</div>}
    </div>
  )
}
