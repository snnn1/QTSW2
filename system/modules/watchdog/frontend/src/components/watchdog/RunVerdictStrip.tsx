/**
 * Compact engine run verdict (summary.json read-through only).
 */
import type { EngineRunSummary, RunSummaryResult } from '../../types/watchdog'
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
  return parts.join(' | ')
}

function countValue(summary: EngineRunSummary, key: string): number {
  const raw = summary.key_counts?.[key]
  if (typeof raw === 'number' && Number.isFinite(raw)) return raw
  const parsed = Number(raw)
  return Number.isFinite(parsed) ? parsed : 0
}

function countTone(value: number): string {
  if (value > 0) return 'border-red-800/70 bg-red-950/25 text-red-100'
  return 'border-slate-700/70 bg-slate-900/45 text-slate-200'
}

function proofLabel(summary: EngineRunSummary): string {
  return summary.watchdog_overlay?.proof_level ?? `${summary.mode}/runtime artifact`
}

export interface RunVerdictStripProps {
  summary: RunSummaryResult | null
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
        Loading run summary...
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
  const runLabel = `${s.date}__${s.mode}__${s.run_id || '-'}`
  const accent = statusAccent(s.status)
  const flagLine = notableFlagLine(s.flags)
  const brokerQty = countValue(s, 'broker_position_qty_at_shutdown')
  const workingOrders = countValue(s, 'broker_working_orders_at_shutdown')
  const openStreams = countValue(s, 'open_position_at_shutdown')
  const journalQty = countValue(s, 'ownership_journal_open_qty_at_shutdown')
  const platformEvents = countValue(s, 'ninjatrader_platform_exception_events')
  const platformCrash =
    s.watchdog_platform_diagnostics?.had_platform_crash_or_freeze_signal === true ||
    s.flags?.had_crash_or_freeze_signal === true

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
          <div className="break-all font-mono text-xs">{runLabel}</div>
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
          <div>{s.recommended_action ?? '-'}</div>
        </div>
        <div>
          <div className="text-xs uppercase text-gray-500">Confidence</div>
          <div>{s.confidence ?? '-'}</div>
        </div>
      </div>
      <div className="mt-3 grid grid-cols-2 gap-2 text-xs md:grid-cols-5">
        <div className={`rounded border px-2 py-1.5 ${countTone(brokerQty)}`}>
          <div className="uppercase tracking-[0.12em] text-gray-500">Broker qty</div>
          <div className="mt-0.5 font-mono text-sm font-semibold">{brokerQty}</div>
        </div>
        <div className={`rounded border px-2 py-1.5 ${countTone(workingOrders)}`}>
          <div className="uppercase tracking-[0.12em] text-gray-500">Working orders</div>
          <div className="mt-0.5 font-mono text-sm font-semibold">{workingOrders}</div>
        </div>
        <div className={`rounded border px-2 py-1.5 ${countTone(openStreams)}`}>
          <div className="uppercase tracking-[0.12em] text-gray-500">Open streams</div>
          <div className="mt-0.5 font-mono text-sm font-semibold">{openStreams}</div>
        </div>
        <div className={`rounded border px-2 py-1.5 ${countTone(journalQty)}`}>
          <div className="uppercase tracking-[0.12em] text-gray-500">Journal qty</div>
          <div className="mt-0.5 font-mono text-sm font-semibold">{journalQty}</div>
        </div>
        <div className={`rounded border px-2 py-1.5 ${platformCrash ? 'border-red-800/70 bg-red-950/25 text-red-100' : 'border-slate-700/70 bg-slate-900/45 text-slate-200'}`}>
          <div className="uppercase tracking-[0.12em] text-gray-500">Proof</div>
          <div className="mt-0.5 truncate font-mono text-sm font-semibold" title={proofLabel(s)}>
            {proofLabel(s)}
          </div>
          {platformEvents > 0 && (
            <div className="mt-0.5 text-[10px] text-red-200">{platformEvents} platform event(s)</div>
          )}
        </div>
      </div>
      <div className="mt-3 text-xs text-gray-400">
        Trades: {s.trades} | Errors: {s.errors} | Instruments:{' '}
        {s.instruments && s.instruments.length > 0 ? s.instruments.join(', ') : '-'}
      </div>
      {flagLine && <div className="mt-2 text-xs text-gray-500">{flagLine}</div>}
    </div>
  )
}
