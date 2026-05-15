/**
 * StreamStatusTable component
 * Main stream status table. TIS updates when streams data changes (every 5s poll).
 * Rows use API / stream-states order (timetable order); no client-side reordering.
 */
import { useMemo } from 'react'
import { formatDuration, computeTimeInState } from '../../utils/timeUtils.ts'
import { useStreamPnl } from '../../hooks/useStreamPnl'
import type {
  CarriedActiveLifecycle,
  ExecutionExpectationGap,
  FlattenLookupMetrics,
  IntentExposure,
  OutOfTimetableActiveStream,
  StreamState,
  UnprotectedPosition,
} from '../../types/watchdog'

interface StreamStatusTableProps {
  streams: StreamState[]
  onStreamClick: (stream: StreamState) => void
  referenceTimeUtc?: string | null
  marketOpen?: boolean | null
  carriedActiveLifecycles?: CarriedActiveLifecycle[]
  outOfTimetableActiveStreams?: OutOfTimetableActiveStream[]
  executionExpectationGaps?: ExecutionExpectationGap[]
  flattenLookupMetrics?: FlattenLookupMetrics | null
  activeIntents?: IntentExposure[]
  unprotectedPositions?: UnprotectedPosition[]
  runRoot?: string | null
}

const TH_CLASS =
  'px-2 py-2 text-left text-[10px] font-semibold uppercase tracking-[0.08em] text-gray-400 whitespace-nowrap'
const TD_CLASS = 'px-2 py-2 align-middle leading-tight'
const PILL_CLASS =
  'inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide whitespace-nowrap'

function getStateBadgeColor(state: string) {
  if (!state || state === '') return 'bg-gray-700 text-gray-400'
  switch (state) {
    case 'PRE_HYDRATION':
      return 'bg-gray-600 text-white'
    case 'ARMED':
      return 'bg-blue-600 text-white'
    case 'RANGE_BUILDING':
      return 'bg-blue-500 text-white'
    case 'RANGE_LOCKED':
      return 'bg-green-600 text-white'
    case 'OPEN':
    case 'OPEN_TRACKED':
      return 'bg-emerald-600 text-white'
    case 'ENTRY_BRACKETS_WORKING':
      return 'bg-cyan-600 text-white'
    case 'DONE':
      return 'bg-gray-500 text-gray-200'
    default:
      return 'bg-gray-600 text-white'
  }
}

function parseQty(value: unknown): number {
  if (value === null || value === undefined || value === '') return 0
  const n = Number(value)
  return Number.isFinite(n) ? n : 0
}

function getStreamOpenCount(streamPnl?: { open_positions?: number | null }) {
  return Math.abs(parseQty(streamPnl?.open_positions))
}

function getInstrumentAuthorityQty(stream: StreamState) {
  const authority = stream.position_authority
  return Math.max(
    Math.abs(parseQty(authority?.broker_qty)),
    Math.abs(parseQty(authority?.real_open_qty)),
    Math.abs(parseQty(authority?.recovery_open_qty)),
    Math.abs(parseQty(authority?.journal_open_qty))
  )
}

function normalizeKey(value: unknown) {
  return String(value ?? '').trim().toUpperCase()
}

function formatCompactNumber(value: number) {
  if (!Number.isFinite(value)) return '-'
  if (Math.abs(value - Math.round(value)) < 0.000001) return String(Math.round(value))
  return value.toFixed(2)
}

function formatMoney(value: unknown) {
  const n = parseQty(value)
  const sign = n < 0 ? '-' : ''
  return `${sign}$${Math.abs(n).toFixed(2)}`
}

function getPnlClass(value: unknown) {
  const n = parseQty(value)
  if (n > 0.000001) return 'text-emerald-300'
  if (n < -0.000001) return 'text-red-300'
  return 'text-slate-300'
}

function getPnlDetail(streamPnl?: {
  realized_pnl?: number | null
  total_costs_realized?: number | null
  intent_count?: number | null
  closed_count?: number | null
  partial_count?: number | null
  open_count?: number | null
  open_positions?: number | null
  pnl_confidence?: string | null
}) {
  if (!streamPnl) {
    return 'P&L is still loading or unavailable for this stream.'
  }
  return [
    `realized=${formatMoney(streamPnl.realized_pnl)}`,
    `costs=${formatMoney(streamPnl.total_costs_realized)}`,
    `intents=${streamPnl.intent_count ?? 0}`,
    `closed=${streamPnl.closed_count ?? 0}`,
    `partial=${streamPnl.partial_count ?? 0}`,
    `open=${streamPnl.open_count ?? streamPnl.open_positions ?? 0}`,
    `confidence=${streamPnl.pnl_confidence ?? 'LOW'}`,
  ].join('; ')
}

function getActiveIntentsForStream(stream: StreamState, activeIntents: IntentExposure[]) {
  const streamKey = normalizeKey(stream.stream)
  return activeIntents.filter((intent) => {
    if (normalizeKey(intent.stream_id) !== streamKey) return false
    const state = normalizeKey(intent.state)
    return state !== 'CLOSED'
  })
}

function summarizeIntentExposure(
  stream: StreamState,
  activeIntents: IntentExposure[],
  unprotectedPositions: UnprotectedPosition[]
) {
  const intents = getActiveIntentsForStream(stream, activeIntents)
  const unprotectedIntentIds = new Set(unprotectedPositions.map((p) => p.intent_id))
  const remaining = intents.reduce((sum, intent) => sum + Math.abs(parseQty(intent.remaining_exposure)), 0)
  const quantity = intents.reduce((sum, intent) => sum + Math.abs(parseQty(intent.quantity)), 0)
  const directions = Array.from(new Set(intents.map((intent) => normalizeKey(intent.direction)).filter(Boolean)))
  const unprotected = intents.some((intent) => unprotectedIntentIds.has(intent.intent_id))
  const direction =
    directions.length === 0
      ? '-'
      : directions.length === 1
        ? directions[0]
        : 'MIXED'

  return {
    intents,
    direction,
    remaining,
    quantity,
    unprotected,
  }
}

function getDirectionBadgeClass(direction: string) {
  switch (direction) {
    case 'LONG':
      return 'bg-emerald-500/15 text-emerald-300'
    case 'SHORT':
      return 'bg-rose-500/15 text-rose-300'
    case 'MIXED':
      return 'bg-purple-500/15 text-purple-300'
    default:
      return 'bg-gray-700 text-gray-300'
  }
}

function getProtectionStatus(
  exposure: ReturnType<typeof summarizeIntentExposure>,
  streamOpenCount: number
) {
  if (exposure.unprotected) {
    return {
      label: 'UNPROTECTED',
      detail: 'One or more active intents are listed by the unprotected-positions endpoint.',
      className: 'bg-red-600 text-white',
    }
  }
  if (exposure.remaining > 0) {
    return {
      label: 'PROTECTED',
      detail: `Active intent remaining exposure=${formatCompactNumber(exposure.remaining)}.`,
      className: 'bg-emerald-500/15 text-emerald-300',
    }
  }
  if (streamOpenCount > 0) {
    return {
      label: 'CHECK',
      detail: 'Stream PnL reports an open position but active intent exposure was not visible.',
      className: 'bg-amber-500/15 text-amber-300',
    }
  }
  return {
    label: '-',
    detail: 'No active stream exposure.',
    className: 'bg-gray-700 text-gray-300',
  }
}

function readableReason(reason?: string | null) {
  const value = (reason || '').trim()
  if (!value) return ''
  switch (value) {
    case 'NO_TRADE_ENTRY_BRACKETS_AT_LOCK_REJECTED':
      return 'BRACKETS REJECTED'
    case 'TRADE_COMPLETED_MARKET_CLOSE':
      return 'MARKET CLOSE'
    case 'TRADE_COMPLETED_BEFORE_FORCED_FLATTEN':
    case 'TRADE COMPLETED BEFORE FORCED FLATTEN':
      return 'DONE PRE-FLAT'
    case 'RECONCILIATION_BROKER_FLAT':
      return 'BROKER FLAT'
    case 'RANGE_INVALIDATED':
      return 'RANGE INVALID'
    case 'TRADE_COMPLETED':
      return 'TRADE DONE'
    default:
      return value.replaceAll('_', ' ')
  }
}

function getTradeStatus(
  stream: StreamState,
  streamPnl?: { open_positions?: number | null; exit_type?: string | null },
  activeRemaining = 0
) {
  const flattenStatus = (stream.flatten_status || '').toUpperCase()
  const rawState = stream.state || ''
  const streamOpenCount = getStreamOpenCount(streamPnl)
  const instrumentAuthorityQty = getInstrumentAuthorityQty(stream)
  const reason = readableReason(stream.commit_reason || streamPnl?.exit_type || stream.slot_reason)
  const authorityDetail =
    instrumentAuthorityQty > 0
      ? `; instrument authority open qty=${instrumentAuthorityQty}`
      : ''
  const rawDetail = rawState
    ? `Raw robot phase: ${rawState}${authorityDetail}`
    : `Raw robot phase unavailable${authorityDetail}`

  if (stream.instrument_blocked) {
    const block = stream.instrument_blocks?.[0]
    const blockScope = block
      ? [
          block.blocks_entries ? 'entries' : null,
          block.blocks_reentry ? 'reentry' : null,
          block.blocks_protectives ? 'protectives' : null,
          block.blocks_cancel ? 'cancel' : null,
          block.blocks_flatten ? 'flatten' : null,
        ].filter(Boolean).join(', ') || block.scope_label || 'unknown'
      : 'unknown'
    return {
      label: 'BLOCKED',
      detail: `${rawDetail}; durable instrument block (${blockScope}): ${stream.instrument_block_reason || 'risk latch active'}`,
      className: 'bg-red-700 text-white',
    }
  }

  if (
    flattenStatus === 'FAILED' ||
    flattenStatus === 'TIMEOUT' ||
    flattenStatus === 'EXPOSURE_REMAINS' ||
    flattenStatus === 'EXPOSURE_REMAINING'
  ) {
    return {
      label: 'FLATTEN RISK',
      detail: `${rawDetail}; flatten_status=${flattenStatus}`,
      className: 'bg-red-600 text-white',
    }
  }

  if (flattenStatus === 'TRIGGERED') {
    return {
      label: 'FLATTENING',
      detail: `${rawDetail}; flatten has triggered and is not terminal yet`,
      className: 'bg-orange-500 text-gray-950',
    }
  }

  if (streamOpenCount > 0 || activeRemaining > 0) {
    return {
      label: 'OPEN TRACKED',
      detail: `${rawDetail}; stream open count=${streamOpenCount}; active remaining=${formatCompactNumber(activeRemaining)}`,
      className: 'bg-amber-500/15 text-amber-300',
    }
  }

  if (rawState === 'OPEN_TRACKED') {
    return {
      label: 'OPEN TRACKED',
      detail: `${rawDetail}; execution journal proves open exposure`,
      className: 'bg-amber-500/15 text-amber-300',
    }
  }

  if (rawState === 'ENTRY_BRACKETS_WORKING') {
    return {
      label: 'ENTRY WORKING',
      detail: `${rawDetail}; unfilled entry bracket journal remains open`,
      className: 'bg-cyan-500/15 text-cyan-300',
    }
  }

  if (stream.committed) {
    const upperReason = (stream.commit_reason || '').toUpperCase()
    if (upperReason.includes('NO_TRADE') || upperReason.includes('REJECTED')) {
      return {
        label: 'NO TRADE',
        detail: `${rawDetail}; ${reason || 'terminal no-trade state'}`,
        className: 'bg-slate-700 text-slate-200',
      }
    }
    if (upperReason.includes('TARGET')) {
      return {
        label: 'FLAT TARGET',
        detail: `${rawDetail}; target completed`,
        className: 'bg-emerald-500/15 text-emerald-300',
      }
    }
    if (upperReason.includes('STOP')) {
      return {
        label: 'FLAT STOP',
        detail: `${rawDetail}; stop completed`,
        className: 'bg-rose-500/15 text-rose-300',
      }
    }
    if (upperReason.includes('MARKET_CLOSE') || upperReason.includes('FORCED_FLATTEN')) {
      return {
        label: 'FLAT CLOSE',
        detail: `${rawDetail}; ${reason || 'session-close completion'}`,
        className: 'bg-blue-500/15 text-blue-300',
      }
    }
    if (upperReason.includes('INVALID')) {
      return {
        label: 'NO TRADE',
        detail: `${rawDetail}; ${reason}`,
        className: 'bg-gray-700 text-gray-200',
      }
    }
    return {
      label: 'TERMINAL',
      detail: `${rawDetail}; ${reason || 'committed terminal state'}`,
      className: 'bg-gray-700 text-gray-200',
    }
  }

  switch (rawState) {
    case 'PRE_HYDRATION':
      return {
        label: 'WAITING',
        detail: rawDetail,
        className: 'bg-gray-700 text-gray-200',
      }
    case 'ARMED':
      return {
        label: 'ARMED',
        detail: rawDetail,
        className: 'bg-blue-500/15 text-blue-300',
      }
    case 'RANGE_BUILDING':
      return {
        label: 'BUILDING RANGE',
        detail: rawDetail,
        className: 'bg-blue-500/15 text-blue-300',
      }
    case 'RANGE_LOCKED':
    case 'OPEN':
      return {
        label: 'SETUP LIVE',
        detail: `${rawDetail}; no tracked open exposure yet`,
        className: 'bg-cyan-500/15 text-cyan-300',
      }
    default:
      return {
        label: rawState || '-',
        detail: rawDetail,
        className: getStateBadgeColor(rawState),
      }
  }
}

function getTimeInStateColor(seconds: number) {
  if (seconds > 600) return 'text-red-500'
  if (seconds > 300) return 'text-amber-500'
  return 'text-white'
}

function getFlattenStatusBadgeClass(status: string | undefined) {
  const value = status ?? 'NOT_TRIGGERED'
  switch (value) {
    case 'NOT_TRIGGERED':
      return 'bg-gray-600 text-gray-200'
    case 'TRIGGERED':
      return 'bg-yellow-500 text-gray-900'
    case 'CONFIRMED':
      return 'bg-green-600 text-white'
    case 'FAILED':
      return 'bg-red-600 text-white'
    case 'TIMEOUT':
      return 'bg-orange-600 text-white'
    case 'EXPOSURE_REMAINS':
    case 'EXPOSURE_REMAINING':
      return 'bg-red-700 text-white'
    case 'BROKER_FLAT':
      return 'bg-blue-600 text-white'
    default:
      return 'bg-gray-700 text-gray-400'
  }
}

function getOutcomeLabel(exitType?: string | null, slotReason?: string | null) {
  const exit = (exitType || '').trim()
  if (exit) {
    const normalizedExit = exit.toUpperCase()
    if (normalizedExit === 'RECONCILIATION_BROKER_FLAT') return 'BROKER FLAT'
    if (
      normalizedExit === 'TRADE_COMPLETED_BEFORE_FORCED_FLATTEN' ||
      normalizedExit === 'TRADE COMPLETED BEFORE FORCED FLATTEN'
    ) {
      return 'DONE PRE-FLAT'
    }
    return normalizedExit
  }

  const reason = (slotReason || '').trim()
  if (!reason) return '-'

  const normalized = reason.toLowerCase()
  if (normalized.includes('awaiting fill')) return 'AWAIT FILL'
  if (normalized.includes('stop brackets submitted')) return 'BRACKETS LIVE'
  if (normalized.includes('range locked')) return 'RANGE LOCKED'
  if (normalized.includes('no trade')) return 'NO TRADE'
  if (normalized.includes('invalidated')) return 'INVALIDATED'

  return reason.length > 18 ? `${reason.slice(0, 18).trimEnd()}...` : reason
}

function getOutcomeBadgeClass(label: string) {
  switch (label) {
    case 'TARGET':
      return 'bg-emerald-500/15 text-emerald-300'
    case 'STOP':
      return 'bg-rose-500/15 text-rose-300'
    case 'OPEN':
    case 'AWAIT FILL':
    case 'BRACKETS LIVE':
    case 'RANGE LOCKED':
      return 'bg-amber-500/15 text-amber-300'
    case 'NO TRADE':
    case 'INVALIDATED':
      return 'bg-gray-700 text-gray-200'
    default:
      return 'bg-slate-700 text-slate-200'
  }
}

function formatSlotTime(slotTime: string | null | undefined) {
  if (!slotTime || slotTime === '-') return '-'
  if (slotTime.includes('T')) {
    try {
      const match = slotTime.match(/T(\d{2}):(\d{2})/)
      if (match) return `${match[1]}:${match[2]}`
    } catch {
      return slotTime
    }
  }
  return slotTime
}

function formatRange(stream: StreamState) {
  const { range_high: rangeHigh, range_low: rangeLow } = stream
  if (
    rangeHigh != null &&
    rangeLow != null &&
    !isNaN(rangeHigh) &&
    !isNaN(rangeLow) &&
    isFinite(rangeHigh) &&
    isFinite(rangeLow)
  ) {
    return `${rangeHigh.toFixed(2)} / ${rangeLow.toFixed(2)}`
  }
  return '-'
}

function getNextEventLabel(stream: StreamState) {
  const flattenStatus = normalizeKey(stream.flatten_status || 'NOT_TRIGGERED')
  if (flattenStatus !== 'NOT_TRIGGERED') {
    return flattenStatus.replaceAll('_', ' ')
  }
  if (stream.committed) {
    return 'TERMINAL'
  }
  if (stream.flatten_trigger_ct) {
    return `FLAT ${stream.flatten_trigger_ct}`
  }
  if (stream.slot_time_chicago) {
    return `SLOT ${formatSlotTime(stream.slot_time_chicago)}`
  }
  return '-'
}

export function StreamStatusTable({
  streams,
  onStreamClick,
  referenceTimeUtc,
  marketOpen,
  carriedActiveLifecycles = [],
  outOfTimetableActiveStreams = [],
  executionExpectationGaps = [],
  flattenLookupMetrics,
  activeIntents = [],
  unprotectedPositions = [],
  runRoot,
}: StreamStatusTableProps) {
  const sessionTradingDay = streams.find((s) => s.trading_date)?.trading_date ?? null
  const currentTradingDate = sessionTradingDay ?? new Date().toISOString().split('T')[0]
  const { pnl } = useStreamPnl(currentTradingDate, undefined, marketOpen, runRoot)
  const carriedLifecycles = carriedActiveLifecycles ?? []
  const carryOver = outOfTimetableActiveStreams ?? []
  const riskSummary = useMemo(() => {
    const openStreamCount = streams.filter((stream) => {
      const streamPnl = pnl[stream.stream]
      const exposure = summarizeIntentExposure(stream, activeIntents, unprotectedPositions)
      return getStreamOpenCount(streamPnl) > 0 || exposure.remaining > 0
    }).length
    const activeExposure = activeIntents.reduce(
      (sum, intent) => sum + Math.abs(parseQty(intent.remaining_exposure)),
      0
    )
    const realizedPnl = Object.values(pnl).reduce((sum, streamPnl) => sum + parseQty(streamPnl.realized_pnl), 0)
    const flattenRiskCount = streams.filter((stream) => {
      const flattenStatus = normalizeKey(stream.flatten_status)
      return ['FAILED', 'TIMEOUT', 'EXPOSURE_REMAINS', 'EXPOSURE_REMAINING'].includes(flattenStatus)
    }).length
    return {
      openStreamCount,
      activeExposure,
      realizedPnl,
      unprotectedCount: unprotectedPositions.length,
      flattenRiskCount,
    }
  }, [activeIntents, pnl, streams, unprotectedPositions])

  const mainTableEmptyState = () => {
    if (marketOpen === false) {
      return (
        <div className="bg-gray-800 rounded-lg p-8 text-center">
          <div className="text-gray-400 text-lg mb-2">No timetable streams</div>
          <div className="text-gray-500 text-sm">Market is closed - this is expected</div>
        </div>
      )
    }
    if (marketOpen === true) {
      return (
        <div className="bg-gray-800 rounded-lg p-8 text-center">
          <div className="text-amber-500 text-lg mb-2">No timetable streams</div>
          <div className="text-gray-400 text-sm">
            Streams will begin forming ranges when they enter their range windows
          </div>
        </div>
      )
    }
    return (
      <div className="bg-gray-800 rounded-lg p-8 text-center text-gray-500">
        No timetable streams
      </div>
    )
  }

  if (streams.length === 0) {
    return mainTableEmptyState()
  }

  return (
    <>
      <div className="overflow-hidden rounded-lg border border-gray-700/70 bg-gray-800 shadow-[0_10px_30px_rgba(0,0,0,0.18)]">
        <div className="flex items-center justify-between border-b border-gray-700/80 bg-gray-900/70 px-3 py-2">
          <div>
            <div className="text-sm font-semibold text-gray-100">Stream Feed</div>
            <div className="text-[11px] text-gray-400">
              {streams.length} timetable stream{streams.length === 1 ? '' : 's'} - risk-first row fields, schedule order preserved
            </div>
          </div>
          <div className="text-[11px] uppercase tracking-[0.12em] text-gray-500">Click row for details</div>
        </div>
        <div className="grid grid-cols-2 gap-2 border-b border-gray-700/70 bg-gray-900/35 px-3 py-2 text-xs md:grid-cols-5">
          <div>
            <div className="text-[10px] uppercase tracking-[0.12em] text-gray-500">Open Streams</div>
            <div className="font-mono text-sm text-gray-100">{riskSummary.openStreamCount}</div>
          </div>
          <div>
            <div className="text-[10px] uppercase tracking-[0.12em] text-gray-500">Active Gross Qty</div>
            <div className="font-mono text-sm text-gray-100">{formatCompactNumber(riskSummary.activeExposure)}</div>
          </div>
          <div>
            <div className="text-[10px] uppercase tracking-[0.12em] text-gray-500">Unprotected</div>
            <div className={`font-mono text-sm ${riskSummary.unprotectedCount > 0 ? 'text-red-300' : 'text-emerald-300'}`}>
              {riskSummary.unprotectedCount}
            </div>
          </div>
          <div>
            <div className="text-[10px] uppercase tracking-[0.12em] text-gray-500">Flatten Risk</div>
            <div className={`font-mono text-sm ${riskSummary.flattenRiskCount > 0 ? 'text-red-300' : 'text-emerald-300'}`}>
              {riskSummary.flattenRiskCount}
            </div>
          </div>
          <div>
            <div className="text-[10px] uppercase tracking-[0.12em] text-gray-500">Realized PnL</div>
            <div className={`font-mono text-sm ${riskSummary.realizedPnl >= 0 ? 'text-emerald-300' : 'text-red-300'}`}>
              ${riskSummary.realizedPnl.toFixed(2)}
            </div>
          </div>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full min-w-[1080px] text-sm">
            <thead className="border-b border-gray-700 bg-gray-800/95">
              <tr>
                <th className={TH_CLASS}>Stream</th>
                <th className={TH_CLASS}>Instrument</th>
                <th
                  className={TH_CLASS}
                  title="Operator-facing trade/risk status. Hover a row value for the raw robot phase."
                >
                  Lifecycle
                </th>
                <th className={TH_CLASS}>Position / Qty</th>
                <th className={TH_CLASS} title="Authoritative realized stream P&L. Open trades remain unrealized.">
                  P&L
                </th>
                <th className={TH_CLASS}>Protection</th>
                <th className={TH_CLASS}>Blocker / reason</th>
                <th className={TH_CLASS}>Reentry / flatten</th>
                <th className={TH_CLASS}>Scheduled exit / result</th>
                <th className={TH_CLASS} title="Time in current raw robot phase">Last event age</th>
              </tr>
            </thead>
            <tbody>
              {streams.map((stream) => {
                const hasState = Boolean(stream.state)
                const timeInState = hasState
                  ? computeTimeInState(stream.state_entry_time_utc, referenceTimeUtc)
                  : 0
                const streamPnl = pnl[stream.stream]
                const isCarryOver =
                  sessionTradingDay != null && stream.trading_date !== sessionTradingDay
                const dateLabel = stream.trading_date || '-'
                const outcomeLabel = getOutcomeLabel(streamPnl?.exit_type, stream.slot_reason)
                const exposure = summarizeIntentExposure(stream, activeIntents, unprotectedPositions)
                const streamOpenCount = getStreamOpenCount(streamPnl)
                const realizedPnl = streamPnl?.realized_pnl ?? null
                const tradeStatus = getTradeStatus(stream, streamPnl, exposure.remaining)
                const protectionStatus = getProtectionStatus(exposure, streamOpenCount)
                const quantityLabel =
                  exposure.remaining > 0
                    ? formatCompactNumber(exposure.remaining)
                    : exposure.quantity > 0
                      ? formatCompactNumber(exposure.quantity)
                      : '-'
                const nextEvent = getNextEventLabel(stream)
                const rowBlocked = Boolean(stream.instrument_blocked)
                const hasExposure = exposure.remaining > 0 || streamOpenCount > 0
                const blockerReason =
                  stream.instrument_block_reason ||
                  (rowBlocked ? 'instrument blocked' : '') ||
                  (!stream.system_tradable_now ? 'system not tradable' : '') ||
                  (tradeStatus.label === 'BLOCKED' ? tradeStatus.detail : '')
                const flattenLabel = stream.flatten_status ?? 'NOT_TRIGGERED'
                const scheduleDetail = `${formatSlotTime(stream.slot_time_chicago)} | ${nextEvent}`
                const reentryDetail = normalizeKey(stream.slot_reason).includes('REENTRY')
                  ? readableReason(stream.slot_reason)
                  : ''

                return (
                  <tr
                    key={`${stream.trading_date}_${stream.stream}`}
                    onClick={() => onStreamClick(stream)}
                    className={`cursor-pointer border-b border-gray-700/80 transition-colors duration-150 hover:bg-slate-700/30 ${
                      rowBlocked
                        ? 'bg-red-950/35 odd:bg-red-950/45'
                        : hasExposure
                          ? 'bg-amber-950/25 odd:bg-amber-950/35'
                          : 'bg-gray-800/70 odd:bg-gray-800/95'
                    }`}
                  >
                    <td className={`${TD_CLASS} font-mono font-semibold text-gray-100`}>
                      <div>{stream.stream}</div>
                      <div className="mt-1 flex flex-wrap gap-1">
                        {isCarryOver && (
                          <span className="rounded bg-amber-600/25 px-1.5 py-0.5 text-[9px] font-semibold uppercase text-amber-200" title={dateLabel}>
                            carried
                          </span>
                        )}
                        {stream.trading_date && (
                          <span className="rounded bg-slate-800 px-1.5 py-0.5 text-[9px] text-slate-400">
                            {stream.trading_date}
                          </span>
                        )}
                      </div>
                    </td>
                    <td className={`${TD_CLASS} whitespace-nowrap font-mono text-gray-200`}>
                      {stream.execution_instrument || stream.instrument || '-'}
                    </td>
                    <td className={TD_CLASS}>
                      <span
                        className={`${PILL_CLASS} ${tradeStatus.className}`}
                        title={tradeStatus.detail}
                      >
                        {tradeStatus.label}
                      </span>
                      <div className="mt-1 font-mono text-[10px] text-gray-500" title={stream.state || undefined}>
                        {stream.state || '-'}
                      </div>
                    </td>
                    <td className={TD_CLASS}>
                      <div className="flex flex-wrap items-center gap-1.5">
                        <span
                          className={`${PILL_CLASS} ${getDirectionBadgeClass(exposure.direction)}`}
                          title={exposure.intents.length > 0 ? `${exposure.intents.length} active intent(s)` : 'No active intent exposure'}
                        >
                          {exposure.direction}
                        </span>
                        <span className="font-mono text-sm text-gray-200">{quantityLabel}</span>
                      </div>
                    </td>
                    <td className={`${TD_CLASS} whitespace-nowrap`} title={getPnlDetail(streamPnl)}>
                      <div className={`font-mono text-sm font-semibold ${getPnlClass(realizedPnl)}`}>
                        {streamPnl ? formatMoney(realizedPnl) : '-'}
                      </div>
                      <div className="mt-1 flex flex-wrap gap-1">
                        {streamPnl?.pnl_confidence && (
                          <span className="rounded bg-slate-800 px-1.5 py-0.5 text-[9px] font-semibold uppercase text-slate-300">
                            {streamPnl.pnl_confidence}
                          </span>
                        )}
                        {streamOpenCount > 0 && (
                          <span className="rounded bg-amber-600/25 px-1.5 py-0.5 text-[9px] font-semibold uppercase text-amber-200">
                            {streamOpenCount} open
                          </span>
                        )}
                      </div>
                    </td>
                    <td className={TD_CLASS}>
                      <span
                        className={`${PILL_CLASS} ${protectionStatus.className}`}
                        title={protectionStatus.detail}
                      >
                        {protectionStatus.label}
                      </span>
                    </td>
                    <td className={`${TD_CLASS} max-w-[13rem] text-xs text-gray-300`}>
                      <span className={blockerReason ? (rowBlocked ? 'text-red-200' : 'text-amber-200') : 'text-gray-500'} title={blockerReason || 'No current blocker'}>
                        {blockerReason || '-'}
                      </span>
                    </td>
                    <td className={TD_CLASS}>
                      <span
                        className={`inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${getFlattenStatusBadgeClass(stream.flatten_status)}`}
                        title={`lookup=${stream.flatten_lookup_reason ?? 'UNKNOWN'}; trigger=${stream.flatten_trigger_ct ?? '-'}`}
                      >
                        {flattenLabel}
                      </span>
                      {reentryDetail && (
                        <div className="mt-1 font-mono text-[10px] text-sky-300">
                          {reentryDetail}
                        </div>
                      )}
                    </td>
                    <td className={`${TD_CLASS} text-xs text-gray-300`} title={stream.slot_reason ?? undefined}>
                      <div className="font-mono">{scheduleDetail}</div>
                      <span className={`mt-1 inline-flex ${PILL_CLASS} ${getOutcomeBadgeClass(outcomeLabel)}`}>
                        {outcomeLabel}
                      </span>
                    </td>
                    <td
                      className={`${TD_CLASS} whitespace-nowrap font-mono tabular-nums ${hasState ? getTimeInStateColor(timeInState) : 'text-gray-500'}`}
                      title="Time in current raw robot phase"
                    >
                      {hasState ? formatDuration(timeInState) : '-'}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {flattenLookupMetrics != null && (
            <details
              className="border-t border-gray-700 bg-gray-900/50 px-3 py-2 font-mono text-[11px] leading-relaxed text-gray-400"
              title="Cumulative counts since watchdog process start (stream_session_flatten_fields). Expect MATCH_CURRENT_DATE dominant; MISSING_KEYS and NO_TRACKER ideally zero."
            >
              <summary className="cursor-pointer uppercase tracking-wide text-gray-500">
                Debug: flatten lookup lifetime counters
              </summary>
              <div className="mt-1">
                {(['MATCH_CURRENT_DATE', 'NO_ROW_FOUND', 'MISSING_KEYS', 'NO_TRACKER'] as const).map((key) => (
                  <span key={key} className="mr-3 whitespace-nowrap">
                    <span className="text-gray-500">{key}</span>
                    <span className="ml-0.5 text-gray-200">{flattenLookupMetrics[key] ?? 0}</span>
                  </span>
                ))}
              </div>
            </details>
          )}
        </div>
      </div>

      {executionExpectationGaps.length > 0 && (
        <div className="overflow-hidden rounded-lg border border-cyan-700/50 bg-cyan-950/20">
          <div className="border-b border-cyan-700/40 bg-cyan-900/30 px-3 py-2">
            <h3 className="text-sm font-semibold tracking-tight text-cyan-100">
              Expected vs actual - slot ended, no trade
            </h3>
            <p className="mt-1 text-xs text-cyan-200/80">
              <span className="font-mono">slot_end_no_trade</span>: robot{' '}
              <span className="font-mono">SLOT_END_SUMMARY</span> with{' '}
              <span className="font-mono">trade_executed=false</span>.{' '}
              <span className="font-mono">slot_missing_summary</span>: Chicago time past timetable{' '}
              <span className="font-mono">slot_time</span> and{' '}
              <span className="font-mono">trade_executed</span> still unset for today's state key.
              Timetable streams only.
            </p>
          </div>
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-gray-900/80 text-cyan-100/90">
                <tr>
                  <th className="px-2 py-1.5 text-left">Stream</th>
                  <th className="px-2 py-1.5 text-left">Instrument</th>
                  <th className="px-2 py-1.5 text-left">Session</th>
                  <th className="px-2 py-1.5 text-left">State</th>
                  <th className="px-2 py-1.5 text-left">Expected</th>
                  <th className="px-2 py-1.5 text-left">Actual</th>
                  <th className="px-2 py-1.5 text-left">Slot reason</th>
                </tr>
              </thead>
              <tbody>
                {executionExpectationGaps.map((gap) => (
                  <tr
                    key={`gap-${gap.stream_id}-${gap.gap_type}-${gap.trading_date}-${gap.timetable_slot_time ?? ''}`}
                    className="border-b border-cyan-900/35 text-gray-200"
                  >
                    <td
                      className="px-2 py-1.5 whitespace-nowrap font-mono text-[10px] text-cyan-200/90"
                      title={gap.detail}
                    >
                      {gap.gap_type === 'slot_missing_summary'
                        ? 'missing summary'
                        : gap.gap_type === 'slot_end_no_trade'
                          ? 'no trade'
                          : gap.gap_type}
                    </td>
                    <td className="px-2 py-1.5 font-mono font-medium text-cyan-100">
                      {gap.stream_id}
                    </td>
                    <td className="px-2 py-1.5">{gap.instrument || '-'}</td>
                    <td className="px-2 py-1.5">{gap.session || '-'}</td>
                    <td className="px-2 py-1.5 font-mono text-xs">{gap.watchdog_state || '-'}</td>
                    <td className="px-2 py-1.5 text-emerald-300/90">
                      {gap.expected === 'trade_in_slot'
                        ? 'Trade in slot'
                        : gap.expected === 'slot_end_summary'
                          ? 'SLOT_END summary'
                          : gap.expected}
                    </td>
                    <td className="px-2 py-1.5 text-amber-300/95">
                      {gap.actual === 'none_executed'
                        ? 'None executed'
                        : gap.actual === 'not_received'
                          ? 'Not received'
                          : gap.actual}
                    </td>
                    <td className="px-2 py-1.5 text-xs text-gray-400" title={gap.detail}>
                      {gap.gap_type === 'slot_missing_summary'
                        ? gap.timetable_slot_time || gap.slot_boundary_chicago || '-'
                        : gap.slot_reason || '-'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {carriedLifecycles.length > 0 && (
        <div className="overflow-hidden rounded-lg border border-sky-600/60 bg-sky-950/25">
          <div className="border-b border-sky-600/40 bg-sky-900/40 px-3 py-2">
            <h3 className="text-sm font-semibold tracking-tight text-sky-200">
              Carried Stream Lifecycles
            </h3>
            <p className="mt-1 text-xs text-sky-300/90">
              Prior-date nonterminal streams retained across timetable rollover. Same stream id rows are deferred until the retained lifecycle is terminal.
            </p>
          </div>
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-gray-900/80 text-sky-200/90">
                <tr>
                  <th className="px-2 py-1.5 text-left">Stream</th>
                  <th className="px-2 py-1.5 text-left">Instrument</th>
                  <th className="px-2 py-1.5 text-left">State</th>
                  <th className="px-2 py-1.5 text-left">Prior date</th>
                  <th className="px-2 py-1.5 text-left">Current day lane</th>
                  <th className="px-2 py-1.5 text-left">Defer reason</th>
                  <th className="px-2 py-1.5 text-left">Authority</th>
                </tr>
              </thead>
              <tbody>
                {carriedLifecycles.map((row) => (
                  <tr
                    key={`carry-life-${row.trading_date}-${row.stream}`}
                    className="border-b border-sky-900/40 text-gray-200"
                  >
                    <td className="px-2 py-1.5 font-mono">{row.stream}</td>
                    <td className="px-2 py-1.5">{row.execution_instrument || row.instrument || '-'}</td>
                    <td className="px-2 py-1.5">
                      <span className={`${PILL_CLASS} ${getStateBadgeColor(row.state)}`}>
                        {row.state || '-'}
                      </span>
                    </td>
                    <td className="px-2 py-1.5 whitespace-nowrap text-sky-100/90">
                      {row.trading_date || '-'}
                    </td>
                    <td className="px-2 py-1.5">
                      {row.current_timetable_lane_present ? 'present' : 'not present'}
                    </td>
                    <td className="px-2 py-1.5 font-mono text-xs text-sky-100/90">
                      {row.same_stream_deferred_reason || '-'}
                    </td>
                    <td className="px-2 py-1.5 font-mono text-xs text-gray-300">
                      {row.position_authority?.authority_state || row.operator_classification || '-'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {carryOver.length > 0 && (
        <div className="overflow-hidden rounded-lg border border-amber-600/60 bg-amber-950/25">
          <div className="border-b border-amber-600/40 bg-amber-900/40 px-3 py-2">
            <h3 className="text-sm font-semibold tracking-tight text-amber-200">
              Open Trades Not In Today's Timetable
            </h3>
            <p className="mt-1 text-xs text-amber-400/90">
              Carry-over position (not in today's timetable)
            </p>
          </div>
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-gray-900/80 text-amber-200/90">
                <tr>
                  <th className="px-2 py-1.5 text-left">Stream</th>
                  <th className="px-2 py-1.5 text-left">Instrument</th>
                  <th className="px-2 py-1.5 text-left">Direction</th>
                  <th className="px-2 py-1.5 text-right">Remaining</th>
                  <th className="px-2 py-1.5 text-right">Entry qty</th>
                  <th className="px-2 py-1.5 text-right">Exit qty</th>
                  <th className="px-2 py-1.5 text-left">Trading date</th>
                  <th className="px-2 py-1.5 text-left">Intent</th>
                  <th className="px-2 py-1.5 text-left">Status</th>
                </tr>
              </thead>
              <tbody>
                {carryOver.map((row) => (
                  <tr
                    key={`oot-${row.intent_id}`}
                    className="border-b border-amber-900/40 text-gray-200"
                  >
                    <td className="px-2 py-1.5 font-mono">{row.stream_id}</td>
                    <td className="px-2 py-1.5">{row.instrument || '-'}</td>
                    <td className="px-2 py-1.5 capitalize">{row.direction?.toLowerCase() || '-'}</td>
                    <td className="px-2 py-1.5 text-right font-mono tabular-nums">
                      {row.remaining_exposure}
                    </td>
                    <td className="px-2 py-1.5 text-right font-mono tabular-nums">
                      {row.entry_filled_qty}
                    </td>
                    <td className="px-2 py-1.5 text-right font-mono tabular-nums">
                      {row.exit_filled_qty}
                    </td>
                    <td className="px-2 py-1.5 whitespace-nowrap text-amber-100/90">
                      {row.trading_date || '-'}
                    </td>
                    <td className="px-2 py-1.5 font-mono text-xs text-gray-400">
                      {row.intent_id}
                    </td>
                    <td className="px-2 py-1.5">
                      <span className="inline-block rounded px-2 py-0.5 text-xs text-white bg-emerald-700/80">
                        OPEN
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </>
  )
}
