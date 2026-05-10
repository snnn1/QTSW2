/**
 * StreamStatusTable component
 * Main stream status table. TIS updates when streams data changes (every 5s poll).
 * Rows use API / stream-states order (timetable order); no client-side reordering.
 */
import { formatDuration, computeTimeInState } from '../../utils/timeUtils.ts'
import { useStreamPnl } from '../../hooks/useStreamPnl'
import type {
  CarriedActiveLifecycle,
  ExecutionExpectationGap,
  FlattenLookupMetrics,
  OutOfTimetableActiveStream,
  StreamState,
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
      return 'bg-emerald-600 text-white'
    case 'DONE':
      return 'bg-gray-500 text-gray-200'
    default:
      return 'bg-gray-600 text-white'
  }
}

function getTimeInStateColor(seconds: number) {
  if (seconds > 600) return 'text-red-500'
  if (seconds > 300) return 'text-amber-500'
  return 'text-white'
}

function getFlattenInfoChip(reason: string | undefined) {
  const value = reason ?? 'NO_ROW_FOUND'
  switch (value) {
    case 'MATCH_CURRENT_DATE':
      return (
        <span className={`${PILL_CLASS} bg-emerald-500/15 text-emerald-300`} title={value}>
          Match
        </span>
      )
    case 'NO_ROW_FOUND':
      return (
        <span className={`${PILL_CLASS} bg-gray-700 text-gray-300`} title={value}>
          No row
        </span>
      )
    case 'MISSING_KEYS':
      return (
        <span className={`${PILL_CLASS} bg-red-500/15 text-red-300`} title={value}>
          Missing keys
        </span>
      )
    case 'NO_TRACKER':
      return (
        <span className={`${PILL_CLASS} bg-red-500/15 text-red-300`} title={value}>
          No tracker
        </span>
      )
    default:
      return (
        <span className="text-gray-400 text-xs font-mono" title={value}>
          {value}
        </span>
      )
  }
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

function getCommitLabel(committed: boolean, commitReason?: string | null) {
  if (!committed) return '-'
  const reason = (commitReason || '').trim()
  if (!reason) return 'COMMITTED'
  if (reason === 'TRADE_COMPLETED') return 'TRADE DONE'
  if (reason === 'RANGE_INVALIDATED') return 'INVALIDATED'
  if (reason === 'RECONCILIATION_BROKER_FLAT') return 'BROKER FLAT'
  if (
    reason === 'TRADE_COMPLETED_BEFORE_FORCED_FLATTEN' ||
    reason === 'TRADE COMPLETED BEFORE FORCED FLATTEN'
  ) {
    return 'DONE PRE-FLAT'
  }
  return reason.replaceAll('_', ' ')
}

function getCommitBadgeClass(label: string) {
  if (label === '-' || label === 'COMMITTED') return 'bg-gray-700 text-gray-200'
  if (label === 'TRADE DONE') return 'bg-emerald-500/15 text-emerald-300'
  if (label === 'INVALIDATED') return 'bg-amber-500/15 text-amber-300'
  return 'bg-slate-700 text-slate-200'
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

export function StreamStatusTable({
  streams,
  onStreamClick,
  referenceTimeUtc,
  marketOpen,
  carriedActiveLifecycles = [],
  outOfTimetableActiveStreams = [],
  executionExpectationGaps = [],
  flattenLookupMetrics,
}: StreamStatusTableProps) {
  const sessionTradingDay = streams.find((s) => s.trading_date)?.trading_date ?? null
  const currentTradingDate = sessionTradingDay ?? new Date().toISOString().split('T')[0]
  const { pnl } = useStreamPnl(currentTradingDate, undefined, marketOpen)
  const carriedLifecycles = carriedActiveLifecycles ?? []
  const carryOver = outOfTimetableActiveStreams ?? []

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
              {streams.length} timetable stream{streams.length === 1 ? '' : 's'}
            </div>
          </div>
          <div className="text-[11px] uppercase tracking-[0.12em] text-gray-500">Click row for details</div>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full min-w-[1120px] text-sm">
            <thead className="border-b border-gray-700 bg-gray-800/95">
              <tr>
                <th className={`${TH_CLASS} min-w-[5rem]`}>Date</th>
                <th className={TH_CLASS}>Stream</th>
                <th className={TH_CLASS}>Instr</th>
                <th className={TH_CLASS}>State</th>
                <th className={TH_CLASS} title="Time in State">
                  TIS
                </th>
                <th className={TH_CLASS}>Slot</th>
                <th className={TH_CLASS}>Range</th>
                <th className={`${TH_CLASS} text-right`}>PnL</th>
                <th className={TH_CLASS} title="Target or Stop">
                  Outcome
                </th>
                <th className={`${TH_CLASS} text-right`} title="Entry / Exit price">
                  Entry/Exit
                </th>
                <th className={TH_CLASS}>Commit</th>
                <th className={TH_CLASS}>Flatten Trigger</th>
                <th className={TH_CLASS}>Flatten Status</th>
                <th className={TH_CLASS} title="Why flatten lookup succeeded or failed">
                  Flatten Info
                </th>
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
                const commitLabel = getCommitLabel(stream.committed, stream.commit_reason)
                const issueLabel =
                  stream.range_invalidated ||
                  (stream.committed && stream.commit_reason === 'RANGE_INVALIDATED')
                    ? 'Check'
                    : '-'

                return (
                  <tr
                    key={`${stream.trading_date}_${stream.stream}`}
                    onClick={() => onStreamClick(stream)}
                    className="cursor-pointer border-b border-gray-700/80 bg-gray-800/70 transition-colors duration-150 odd:bg-gray-800/95 hover:bg-slate-700/30"
                  >
                    <td className={`${TD_CLASS} whitespace-nowrap min-w-[5rem]`}>
                      <span
                        className={isCarryOver ? 'font-medium text-amber-300' : 'text-gray-300'}
                        title={stream.trading_date || undefined}
                      >
                        {dateLabel}
                      </span>
                    </td>
                    <td className={`${TD_CLASS} font-mono font-semibold text-gray-100`}>
                      {stream.stream}
                    </td>
                    <td className={`${TD_CLASS} whitespace-nowrap font-mono text-gray-200`}>
                      {stream.execution_instrument || stream.instrument || '-'}
                    </td>
                    <td className={TD_CLASS}>
                      {stream.state ? (
                        <span
                          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${getStateBadgeColor(stream.state)}`}
                        >
                          {stream.state}
                        </span>
                      ) : (
                        <span className="inline-flex items-center rounded-full bg-gray-700 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-gray-400">
                          -
                        </span>
                      )}
                    </td>
                    <td
                      className={`${TD_CLASS} whitespace-nowrap font-mono tabular-nums ${hasState ? getTimeInStateColor(timeInState) : 'text-gray-500'}`}
                    >
                      {hasState ? formatDuration(timeInState) : '-'}
                    </td>
                    <td className={`${TD_CLASS} font-mono text-gray-300`}>
                      {formatSlotTime(stream.slot_time_chicago)}
                    </td>
                    <td className={`${TD_CLASS} whitespace-nowrap font-mono tabular-nums text-gray-200`}>
                      {formatRange(stream)}
                    </td>
                    <td className={`${TD_CLASS} text-right font-mono tabular-nums`}>
                      {(() => {
                        if (!streamPnl) {
                          return <span className="text-gray-500">$0.00</span>
                        }
                        if (streamPnl.open_positions > 0) {
                          return <span className="text-amber-500">OPEN</span>
                        }
                        const realizedPnl = streamPnl.realized_pnl
                        if (realizedPnl === undefined || realizedPnl === null) {
                          return <span className="text-gray-500">$0.00</span>
                        }
                        return (
                          <span className={realizedPnl >= 0 ? 'text-green-500' : 'text-red-500'}>
                            ${realizedPnl.toFixed(2)}
                          </span>
                        )
                      })()}
                    </td>
                    <td
                      className={TD_CLASS}
                      title={streamPnl?.exit_type ?? stream.slot_reason ?? undefined}
                    >
                      <span className={`${PILL_CLASS} ${getOutcomeBadgeClass(outcomeLabel)}`}>
                        {outcomeLabel}
                      </span>
                    </td>
                    <td className={`${TD_CLASS} text-right font-mono text-xs tabular-nums text-gray-200 whitespace-nowrap`}>
                      {streamPnl?.entry_price != null || streamPnl?.exit_price != null
                        ? `${streamPnl?.entry_price?.toFixed(2) ?? '-'} / ${streamPnl?.exit_price?.toFixed(2) ?? '-'}`
                        : '-'}
                    </td>
                    <td className={TD_CLASS} title={stream.commit_reason ?? undefined}>
                      <span className={`${PILL_CLASS} ${getCommitBadgeClass(commitLabel)}`}>
                        {commitLabel}
                      </span>
                    </td>
                    <td className={`${TD_CLASS} font-mono text-sm tabular-nums text-gray-300`}>
                      {stream.flatten_trigger_ct ?? '-'}
                    </td>
                    <td className={TD_CLASS}>
                      <span
                        className={`inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${getFlattenStatusBadgeClass(stream.flatten_status)}`}
                      >
                        {stream.flatten_status ?? 'NOT_TRIGGERED'}
                      </span>
                    </td>
                    <td className={`${TD_CLASS} whitespace-nowrap`}>
                      {getFlattenInfoChip(stream.flatten_lookup_reason)}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {flattenLookupMetrics != null && (
            <div
              className="border-t border-gray-700 bg-gray-900/50 px-3 py-2 font-mono text-[11px] leading-relaxed text-gray-400"
              title="Cumulative counts since watchdog process start (stream_session_flatten_fields). Expect MATCH_CURRENT_DATE dominant; MISSING_KEYS and NO_TRACKER ideally zero."
            >
              <span className="mr-2 uppercase tracking-wide text-gray-500">Flatten lookup (lifetime)</span>
              {(['MATCH_CURRENT_DATE', 'NO_ROW_FOUND', 'MISSING_KEYS', 'NO_TRACKER'] as const).map((key) => (
                <span key={key} className="mr-3 whitespace-nowrap">
                  <span className="text-gray-500">{key}</span>
                  <span className="ml-0.5 text-gray-200">{flattenLookupMetrics[key] ?? 0}</span>
                </span>
              ))}
            </div>
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
