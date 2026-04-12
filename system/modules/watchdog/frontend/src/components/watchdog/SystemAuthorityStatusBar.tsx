/**
 * SYSTEM STATUS — authority (robot mirror) vs overlay tradability vs reconciliation diagnostic.
 */
import { useEffect, useState } from 'react'
import {
  type OverallExecutionDerived,
  formatOverlayBlockedByLine,
  OVERLAY_TRADABILITY_VS_AUTHORITY_NOTE,
} from '../../utils/executionSeverity'
import type { PositionAuthoritySnapshot } from '../../types/watchdog'
import {
  AUTHORITY_STALE_MS,
  isAuthorityStale,
  positionAuthorityInterpretation,
} from '../../utils/positionAuthority.ts'

type Props = {
  byInstrument: Record<string, PositionAuthoritySnapshot> | undefined
  overallExecution: OverallExecutionDerived
  reconciliationGateState?: string | null
}

function chipEmoji(authorityState: string | undefined, stale: boolean): string {
  if (stale) return '⚪'
  const u = (authorityState ?? '').toUpperCase()
  if (u === 'REAL') return '🟢'
  if (u === 'RECOVERY') return '🟡'
  if (u === 'UNKNOWN') return '🔴'
  return '⚪'
}

export function SystemAuthorityStatusBar({
  byInstrument,
  overallExecution,
  reconciliationGateState,
}: Props) {
  const [nowMs, setNowMs] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNowMs(Date.now()), 2000)
    return () => clearInterval(id)
  }, [])

  const entries = Object.entries(byInstrument ?? {}).sort(([a], [b]) => a.localeCompare(b))
  const authLabel = overallExecution.authority_aggregate
  const tradable = overallExecution.tradable
  const blockedBy = formatOverlayBlockedByLine(overallExecution)
  const authorityRealButNotTradable = authLabel === 'REAL' && !tradable
  const diagnostic =
    overallExecution.reconciliation_gate_diagnostic +
    (reconciliationGateState && reconciliationGateState !== 'OK'
      ? ` · state=${reconciliationGateState}`
      : '')

  const summaryRow = (
    <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-x-4 gap-y-1 text-[11px] text-gray-300 mb-2 font-mono">
      <div>
        <span className="text-gray-500">Tradable</span>{' '}
        <span className={tradable ? 'text-emerald-400' : 'text-red-400'}>{tradable ? 'yes' : 'no'}</span>
      </div>
      <div>
        <span className="text-gray-500">Authority (aggregate)</span>{' '}
        <span className="text-gray-200">{authLabel}</span>
      </div>
      <div
        className={`sm:col-span-2 lg:col-span-1 rounded px-1 -mx-1 ${
          authorityRealButNotTradable ? 'ring-1 ring-amber-500/40 bg-amber-950/20' : ''
        }`}
        title={
          authorityRealButNotTradable
            ? 'Authority can be REAL while tradable is no — overlays (engine, kill switch, …) are independent. See Blocked by.'
            : undefined
        }
      >
        <span className="text-gray-500">Blocked by (overlay)</span>{' '}
        <span className={authorityRealButNotTradable ? 'text-amber-200' : 'text-gray-200'}>
          {blockedBy}
        </span>
      </div>
      <div className="sm:col-span-2 lg:col-span-2">
        <span className="text-gray-500">Diagnostic</span>{' '}
        <span className="text-gray-400">{diagnostic}</span>
      </div>
    </div>
  )

  const modelNote = (
    <p className="text-[10px] text-gray-500 mt-2 leading-snug border-t border-gray-700/50 pt-2">
      {OVERLAY_TRADABILITY_VS_AUTHORITY_NOTE}
    </p>
  )

  if (entries.length === 0) {
    return (
      <div className="rounded-lg border border-gray-700 bg-gray-900/60 px-3 py-2 text-xs">
        <div className="text-[11px] font-semibold text-gray-300 tracking-wide mb-1">SYSTEM STATUS</div>
        {summaryRow}
        <div className="text-gray-500">No POSITION_AUTHORITY_EVALUATED data yet — authority line is UNKNOWN until robot emits.</div>
        {modelNote}
      </div>
    )
  }

  return (
    <div className="rounded-lg border border-gray-700 bg-gray-900/60 px-3 py-2">
      <div className="text-[11px] font-semibold text-gray-300 tracking-wide mb-1">
        SYSTEM STATUS
        <span className="ml-2 font-normal text-gray-500">
          (authority stale if no event &gt; {AUTHORITY_STALE_MS / 1000}s)
        </span>
      </div>
      {summaryRow}
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-200">
        {entries.map(([key, pa]) => {
          const stale = isAuthorityStale(pa.last_authority_ts_utc, nowMs)
          const stateLabel = stale ? 'STALE' : String(pa.authority_state ?? '—')
          const title = [
            positionAuthorityInterpretation(pa.authority_state),
            '',
            `key=${key}`,
            `instrument=${pa.instrument ?? ''}`,
            `last=${pa.last_authority_ts_utc ?? ''}`,
            stale ? `STALE (>${AUTHORITY_STALE_MS / 1000}s)` : 'fresh',
          ].join('\n')
          return (
            <span key={key} className="font-mono whitespace-nowrap" title={title}>
              <span className="text-gray-400">{key}</span>
              <span className="mx-1 text-gray-600">→</span>
              <span className={stale ? 'text-gray-400' : ''}>{stateLabel}</span>
              <span className="ml-1">{chipEmoji(pa.authority_state, stale)}</span>
            </span>
          )
        })}
      </div>
      {modelNote}
    </div>
  )
}
