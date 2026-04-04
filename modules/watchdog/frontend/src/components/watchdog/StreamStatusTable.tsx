/**
 * StreamStatusTable component
 * Main stream status table. TIS updates when streams data changes (every 5s poll).
 * Rows use API / stream-states order (timetable order); no client-side reordering.
 */
import { formatDuration, computeTimeInState } from '../../utils/timeUtils.ts'
import { useStreamPnl } from '../../hooks/useStreamPnl'
import type {
  ExecutionExpectationGap,
  OutOfTimetableActiveStream,
  StreamState,
} from '../../types/watchdog'

interface StreamStatusTableProps {
  streams: StreamState[]
  onStreamClick: (stream: StreamState) => void
  marketOpen?: boolean | null
  /** Active intents not on today's timetable — separate from timetable-only ``streams`` */
  outOfTimetableActiveStreams?: OutOfTimetableActiveStream[]
  /** Robot-reported slot end without trade (timetable streams only) */
  executionExpectationGaps?: ExecutionExpectationGap[]
  /** When true, robot heartbeat timetable ≠ system file (global for all rows). */
  timetableDrift?: boolean | null
}

export function StreamStatusTable({
  streams,
  onStreamClick,
  marketOpen,
  outOfTimetableActiveStreams = [],
  executionExpectationGaps = [],
  timetableDrift = null,
}: StreamStatusTableProps) {
  // Session day from API (CME label); never use UTC calendar for carry-over — wrong on Fri/Sat/Sun CT.
  const sessionTradingDay =
    streams.find((s) => s.trading_date)?.trading_date ?? null
  const currentTradingDate =
    sessionTradingDay ?? new Date().toISOString().split('T')[0]
  const { pnl } = useStreamPnl(currentTradingDate, undefined, marketOpen)

  const carryOver = outOfTimetableActiveStreams ?? []

  const mainTableEmptyState = () => {
    if (marketOpen === false) {
      return (
        <div className="bg-gray-800 rounded-lg p-8 text-center">
          <div className="text-gray-400 text-lg mb-2">No timetable streams</div>
          <div className="text-gray-500 text-sm">Market is closed — this is expected</div>
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

  const getStateBadgeColor = (state: string) => {
    if (!state || state === '') {
      return 'bg-gray-700 text-gray-400'
    }
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
        return 'bg-emerald-600 text-white'  // Position open
      case 'DONE':
        return 'bg-gray-500 text-gray-300'
      default:
        return 'bg-gray-600 text-white'
    }
  }
  
  const getTimeInStateColor = (seconds: number) => {
    if (seconds > 600) return 'text-red-500' // >10 min
    if (seconds > 300) return 'text-amber-500' // >5 min
    return 'text-white'
  }

  return (
    <>
    <div className="bg-gray-800 rounded-lg overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead className="bg-gray-700">
            <tr>
              <th className="px-2 py-1 text-left whitespace-nowrap min-w-[5rem]">Date</th>
              <th className="px-2 py-1 text-left">Stream</th>
              <th className="px-2 py-1 text-left">Instr</th>
              <th className="px-2 py-1 text-left">Session</th>
              <th
                className="px-2 py-1 text-left"
                title="System tradable now (matches /status execution_safe)"
              >
                Tradable
              </th>
              <th
                className="px-2 py-1 text-left whitespace-nowrap"
                title="Drift compares publisher identity (file JSON timetable_hash vs robot heartbeat), not content hash"
              >
                Timetable
              </th>
              <th className="px-2 py-1 text-left">State</th>
              <th className="px-2 py-1 text-left" title="Time in State">TIS</th>
              <th className="px-2 py-1 text-left">Slot</th>
              <th className="px-2 py-1 text-left">Range</th>
              <th className="px-2 py-1 text-left">PnL</th>
              <th className="px-2 py-1 text-left" title="Target or Stop">Outcome</th>
              <th className="px-2 py-1 text-left" title="Entry / Exit price">Entry/Exit</th>
              <th className="px-2 py-1 text-left">Commit</th>
              <th className="px-2 py-1 text-left">Issues</th>
            </tr>
          </thead>
          <tbody>
            {streams.map((stream) => {
              const timeInState = computeTimeInState(stream.state_entry_time_utc)
              const issues: string[] = []
              
              // Determine issues (simplified - would need more data)
              if (stream.committed && stream.commit_reason === 'RANGE_INVALIDATED') {
                issues.push('⚠️ Range Invalidated')
              }
              
              // Get P&L for this stream
              const streamPnl = pnl[stream.stream]
              const isCarryOver =
                sessionTradingDay != null &&
                stream.trading_date !== sessionTradingDay
              const dateLabel = stream.trading_date
                ? stream.trading_date
                : '-'
              
              return (
                <tr
                  key={`${stream.trading_date}_${stream.stream}`}
                  onClick={() => onStreamClick(stream)}
                  className="border-b border-gray-700 hover:bg-gray-700 cursor-pointer"
                >
                  <td className="px-2 py-1 whitespace-nowrap min-w-[5rem]" title={stream.trading_date || undefined}>
                    <span className={isCarryOver ? 'text-amber-400 font-medium' : 'text-gray-300'}>
                      {dateLabel}
                    </span>
                  </td>
                  <td className="px-2 py-1 font-mono">{stream.stream}</td>
                  <td className="px-2 py-1">{stream.execution_instrument || stream.instrument || '-'}</td>
                  <td className="px-2 py-1">{stream.session || '-'}</td>
                  <td className="px-2 py-1">
                    {stream.system_tradable_now === true ? (
                      <span
                        className="inline-block w-2.5 h-2.5 rounded-full bg-green-500 shrink-0"
                        title="Tradable (execution_safe)"
                        aria-label="Tradable"
                      />
                    ) : stream.system_tradable_now === false ? (
                      <span
                        className="inline-block w-2.5 h-2.5 rounded-full bg-red-500 shrink-0"
                        title="Not tradable"
                        aria-label="Not tradable"
                      />
                    ) : (
                      <span className="text-gray-500 text-xs" title="Unknown">
                        —
                      </span>
                    )}
                  </td>
                  <td className="px-2 py-1 text-xs">
                    {timetableDrift === true ? (
                      <span title="Timetable drift: robot heartbeat ≠ file publisher identity" className="text-amber-400">
                        Drift ⚠
                      </span>
                    ) : timetableDrift === false ? (
                      <span title="Publisher identity matches robot heartbeat" className="text-green-500">
                        Synced ✓
                      </span>
                    ) : (
                      <span title="Unknown (no robot hash or system snapshot)" className="text-gray-500">
                        —
                      </span>
                    )}
                  </td>
                  <td className="px-2 py-1">
                    {stream.state ? (
                      <span className={`px-2 py-0.5 rounded text-xs ${getStateBadgeColor(stream.state)}`}>
                        {stream.state}
                      </span>
                    ) : (
                      <span className="px-2 py-0.5 rounded text-xs bg-gray-700 text-gray-400">
                        -
                      </span>
                    )}
                  </td>
                  <td className={`px-2 py-1 font-mono whitespace-nowrap ${getTimeInStateColor(timeInState)}`}>
                    {formatDuration(timeInState)}
                  </td>
                  <td className="px-2 py-1 font-mono">
                    {(() => {
                      const slotTime = stream.slot_time_chicago
                      if (!slotTime || slotTime === '' || slotTime === '-') return '-'
                      // Backend may already format to "HH:MM", or it might be ISO format
                      // Handle both cases
                      if (slotTime.includes('T')) {
                        // ISO format: extract time portion
                        try {
                          const match = slotTime.match(/T(\d{2}):(\d{2})/)
                          if (match) return `${match[1]}:${match[2]}`
                        } catch {}
                      }
                      // Already formatted as "HH:MM" or similar
                      return slotTime
                    })()}
                  </td>
                  <td className="px-2 py-1 font-mono whitespace-nowrap">
                    {(() => {
                      const rangeHigh = stream.range_high
                      const rangeLow = stream.range_low
                      // Check for null, undefined, or NaN
                      if (rangeHigh != null && rangeLow != null && 
                          !isNaN(rangeHigh) && !isNaN(rangeLow) &&
                          isFinite(rangeHigh) && isFinite(rangeLow)) {
                        return `${rangeHigh.toFixed(2)} / ${rangeLow.toFixed(2)}`
                      }
                      return '-'
                    })()}
                  </td>
                  <td className="px-2 py-1 font-mono">
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
                  <td className="px-2 py-1 font-mono text-xs">
                    {streamPnl?.exit_type ?? stream.slot_reason ?? '-'}
                  </td>
                  <td className="px-2 py-1 font-mono text-xs whitespace-nowrap">
                    {streamPnl?.entry_price != null || streamPnl?.exit_price != null
                      ? `${streamPnl?.entry_price?.toFixed(2) ?? '-'} / ${streamPnl?.exit_price?.toFixed(2) ?? '-'}`
                      : '-'}
                  </td>
                  <td className="px-2 py-1">
                    {stream.committed && stream.commit_reason 
                      ? stream.commit_reason 
                      : stream.committed 
                        ? 'COMMITTED' 
                        : '-'}
                  </td>
                  <td className="px-2 py-1">
                    {(() => {
                      const issueList: string[] = []
                      
                      // Range invalidated
                      if (stream.range_invalidated) {
                        issueList.push('⚠️ Range Invalidated')
                      }
                      
                      // Committed with specific reason
                      if (stream.committed && stream.commit_reason === 'RANGE_INVALIDATED') {
                        issueList.push('⚠️ Range Invalidated')
                      }
                      
                      // Note: Removed "Stuck in state" indicator - time in state is already visible in "Time in State" column
                      
                      return issueList.length > 0 ? (
                        <span className="text-amber-500" title={issueList.join(', ')}>
                          {issueList[0]}
                        </span>
                      ) : (
                        '-'
                      )
                    })()}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>

    {executionExpectationGaps.length > 0 && (
      <div className="rounded-lg border border-cyan-700/50 bg-cyan-950/20 overflow-hidden">
        <div className="px-3 py-2 bg-cyan-900/30 border-b border-cyan-700/40">
          <h3 className="text-sm font-semibold text-cyan-100 tracking-tight">
            Expected vs actual — slot ended, no trade
          </h3>
          <p className="text-xs text-cyan-200/80 mt-1">
            <span className="font-mono">slot_end_no_trade</span>: robot{' '}
            <span className="font-mono">SLOT_END_SUMMARY</span> with{' '}
            <span className="font-mono">trade_executed=false</span>.{' '}
            <span className="font-mono">slot_missing_summary</span>: Chicago time past timetable{' '}
            <span className="font-mono">slot_time</span> and <span className="font-mono">trade_executed</span> still
            unset for today&apos;s state key. Timetable streams only.
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
              {executionExpectationGaps.map((g) => (
                <tr
                  key={`gap-${g.stream_id}-${g.gap_type}-${g.trading_date}-${g.timetable_slot_time ?? ''}`}
                  className="border-b border-cyan-900/35 text-gray-200"
                >
                  <td
                    className="px-2 py-1.5 font-mono text-[10px] text-cyan-200/90 whitespace-nowrap"
                    title={g.detail}
                  >
                    {g.gap_type === 'slot_missing_summary'
                      ? 'missing summary'
                      : g.gap_type === 'slot_end_no_trade'
                        ? 'no trade'
                        : g.gap_type}
                  </td>
                  <td className="px-2 py-1.5 font-mono font-medium text-cyan-100">{g.stream_id}</td>
                  <td className="px-2 py-1.5">{g.instrument || '—'}</td>
                  <td className="px-2 py-1.5">{g.session || '—'}</td>
                  <td className="px-2 py-1.5 font-mono text-xs">{g.watchdog_state || '—'}</td>
                  <td className="px-2 py-1.5 text-emerald-300/90">
                    {g.expected === 'trade_in_slot'
                      ? 'Trade in slot'
                      : g.expected === 'slot_end_summary'
                        ? 'SLOT_END summary'
                        : g.expected}
                  </td>
                  <td className="px-2 py-1.5 text-amber-300/95">
                    {g.actual === 'none_executed'
                      ? 'None executed'
                      : g.actual === 'not_received'
                        ? 'Not received'
                        : g.actual}
                  </td>
                  <td className="px-2 py-1.5 text-xs text-gray-400" title={g.detail}>
                    {g.gap_type === 'slot_missing_summary'
                      ? g.timetable_slot_time || g.slot_boundary_chicago || '—'
                      : g.slot_reason || '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    )}

    {carryOver.length > 0 && (
      <div className="rounded-lg border border-amber-600/60 bg-amber-950/25 overflow-hidden">
        <div className="px-3 py-2 bg-amber-900/40 border-b border-amber-600/40">
          <h3 className="text-sm font-semibold text-amber-200 tracking-tight">
            Open Trades Not In Today&apos;s Timetable
          </h3>
          <p className="text-xs text-amber-400/90 mt-1">
            ⚠ Carry-over position (not in today&apos;s timetable)
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
                  <td className="px-2 py-1.5">{row.instrument || '—'}</td>
                  <td className="px-2 py-1.5 capitalize">{row.direction?.toLowerCase() || '—'}</td>
                  <td className="px-2 py-1.5 text-right font-mono tabular-nums">{row.remaining_exposure}</td>
                  <td className="px-2 py-1.5 text-right font-mono tabular-nums">{row.entry_filled_qty}</td>
                  <td className="px-2 py-1.5 text-right font-mono tabular-nums">{row.exit_filled_qty}</td>
                  <td className="px-2 py-1.5 whitespace-nowrap text-amber-100/90">{row.trading_date || '—'}</td>
                  <td className="px-2 py-1.5 font-mono text-xs text-gray-400">{row.intent_id}</td>
                  <td className="px-2 py-1.5">
                    <span className="inline-block px-2 py-0.5 rounded text-xs bg-emerald-700/80 text-white">
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
