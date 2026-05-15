import type {
  CarriedActiveLifecycle,
  IntentExposure,
  OutOfTimetableActiveStream,
  StreamState,
  UnprotectedPosition,
  WatchdogStatus,
} from '../../types/watchdog'
import type { OverallExecutionDerived } from '../../utils/executionSeverity'
import { formatDuration } from '../../utils/timeUtils'

type Props = {
  status: WatchdogStatus | null
  streams: StreamState[]
  activeIntents: IntentExposure[]
  unprotectedPositions: UnprotectedPosition[]
  carriedActiveLifecycles: CarriedActiveLifecycle[]
  outOfTimetableActiveStreams: OutOfTimetableActiveStream[]
  overallExecution: OverallExecutionDerived
}

function numberValue(value: unknown): number {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const parsed = Number(value)
    return Number.isFinite(parsed) ? parsed : 0
  }
  return 0
}

function normalize(value: unknown): string {
  return String(value ?? '').trim().toUpperCase()
}

function chipClass(active: boolean, danger = false): string {
  if (danger) return 'border-red-700/70 bg-red-950/30 text-red-100'
  if (active) return 'border-amber-600/70 bg-amber-950/25 text-amber-100'
  return 'border-slate-700/70 bg-slate-900/60 text-slate-300'
}

function CompactMetric({ label, value, active, danger }: { label: string; value: string; active?: boolean; danger?: boolean }) {
  return (
    <div className={`rounded-lg border px-3 py-2 ${chipClass(Boolean(active), Boolean(danger))}`}>
      <div className="text-[10px] font-semibold uppercase tracking-[0.12em] opacity-70">{label}</div>
      <div className="mt-1 font-mono text-sm font-semibold">{value}</div>
    </div>
  )
}

export function ActiveRiskPanel({
  status,
  streams,
  activeIntents,
  unprotectedPositions,
  carriedActiveLifecycles,
  outOfTimetableActiveStreams,
  overallExecution,
}: Props) {
  const authorityRows = Object.entries(status?.position_authority_by_instrument ?? {})
    .map(([key, row]) => ({
      key,
      brokerQty: numberValue(row.broker_qty),
      journalQty: numberValue(row.journal_open_qty),
      realQty: numberValue(row.real_open_qty),
      brokerWorking: numberValue(row.broker_working_count),
      ieaWorking: numberValue(row.iea_trusted_working_count),
      authority: row.authority_state ?? '-',
    }))
    .filter((row) => Math.abs(row.brokerQty) > 0 || Math.abs(row.journalQty) > 0 || Math.abs(row.realQty) > 0 || Math.abs(row.brokerWorking) > 0)

  const activeLatches = status?.active_risk_latches ?? []
  const flattenRows = streams.filter((stream) => {
    const s = normalize(stream.flatten_status ?? 'NOT_TRIGGERED')
    return s !== '' && s !== 'NOT_TRIGGERED'
  })
  const flattenRiskRows = flattenRows.filter((stream) => {
    const s = normalize(stream.flatten_status)
    return s.includes('FAILED') || s.includes('TIMEOUT') || s.includes('REMAIN')
  })
  const blockedStreams = streams.filter((stream) => stream.instrument_blocked)
  const stuckStreams = status?.stuck_streams ?? []
  const mismatchActive = normalize(status?.reconciliation_gate_state) !== '' && normalize(status?.reconciliation_gate_state) !== 'OK'
  const recoveryActive = normalize(status?.recovery_state) === 'RECOVERY_RUNNING'
  const brokerGross = authorityRows.reduce((sum, row) => sum + Math.abs(row.brokerQty), 0)
  const journalGross = authorityRows.reduce((sum, row) => sum + Math.abs(row.journalQty), 0)
  const brokerWorkingOrders = authorityRows.reduce((sum, row) => sum + Math.abs(row.brokerWorking), 0)
  const activeIntentQty = activeIntents.reduce((sum, intent) => sum + Math.abs(numberValue(intent.remaining_exposure)), 0)
  const hasRisk =
    authorityRows.length > 0 ||
    activeIntentQty > 0 ||
    unprotectedPositions.length > 0 ||
    activeLatches.length > 0 ||
    flattenRows.length > 0 ||
    carriedActiveLifecycles.length > 0 ||
    outOfTimetableActiveStreams.length > 0 ||
    blockedStreams.length > 0 ||
    mismatchActive ||
    recoveryActive ||
    overallExecution.execution_blocked

  return (
    <section id="risk-latches-panel" className={`rounded-lg border p-3 ${
      hasRisk
        ? 'border-amber-700/70 bg-slate-950/72'
        : 'border-emerald-800/50 bg-emerald-950/10'
    }`}>
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-400">
            Active risk / exposure
          </div>
          <div className="text-xs text-slate-500">
            Current broker, journal, protection, latch, mismatch, flatten, and carryover state.
          </div>
        </div>
        <div className={`rounded-full px-2.5 py-1 text-xs font-semibold ${
          hasRisk ? 'bg-amber-700/70 text-amber-50' : 'bg-emerald-800/70 text-emerald-50'
        }`}>
          {hasRisk ? 'Review active risk' : 'No active risk observed'}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2 md:grid-cols-3 xl:grid-cols-6">
        <CompactMetric label="Broker gross" value={String(brokerGross)} danger={brokerGross > 0} />
        <CompactMetric label="Working orders" value={String(brokerWorkingOrders)} danger={brokerWorkingOrders > 0} />
        <CompactMetric label="Journal gross" value={String(journalGross)} danger={journalGross > 0} />
        <CompactMetric label="Intent gross" value={String(activeIntentQty)} danger={activeIntentQty > 0} />
        <CompactMetric label="Unprotected" value={String(unprotectedPositions.length)} danger={unprotectedPositions.length > 0} />
        <CompactMetric label="Latches" value={String(activeLatches.length)} danger={activeLatches.length > 0} />
        <CompactMetric
          label="Mismatch/recovery"
          value={mismatchActive ? status?.reconciliation_gate_state ?? 'ACTIVE' : recoveryActive ? 'RECOVERY' : 'OK'}
          active={mismatchActive || recoveryActive}
          danger={normalize(status?.reconciliation_gate_state) === 'FAIL_CLOSED'}
        />
      </div>

      {authorityRows.length > 0 && (
        <div className="mt-3 overflow-x-auto rounded-lg border border-slate-700/70">
          <table className="w-full text-xs">
            <thead className="bg-slate-900/80 text-slate-400">
              <tr>
                <th className="px-2 py-1.5 text-left">Instrument</th>
                <th className="px-2 py-1.5 text-right">Broker</th>
                <th className="px-2 py-1.5 text-right">Working</th>
                <th className="px-2 py-1.5 text-right">IEA</th>
                <th className="px-2 py-1.5 text-right">Journal</th>
                <th className="px-2 py-1.5 text-right">Real</th>
                <th className="px-2 py-1.5 text-left">Authority</th>
              </tr>
            </thead>
            <tbody>
              {authorityRows.map((row) => (
                <tr key={row.key} className="border-t border-slate-800/80 text-slate-200">
                  <td className="px-2 py-1.5 font-mono">{row.key}</td>
                  <td className="px-2 py-1.5 text-right font-mono">{row.brokerQty}</td>
                  <td className="px-2 py-1.5 text-right font-mono">{row.brokerWorking}</td>
                  <td className="px-2 py-1.5 text-right font-mono">{row.ieaWorking}</td>
                  <td className="px-2 py-1.5 text-right font-mono">{row.journalQty}</td>
                  <td className="px-2 py-1.5 text-right font-mono">{row.realQty}</td>
                  <td className="px-2 py-1.5 font-mono">{row.authority}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {(activeLatches.length > 0 || unprotectedPositions.length > 0 || flattenRiskRows.length > 0 || carriedActiveLifecycles.length > 0 || outOfTimetableActiveStreams.length > 0 || blockedStreams.length > 0 || stuckStreams.length > 0) && (
        <div className="mt-3 grid gap-2 lg:grid-cols-2">
          {activeLatches.map((latch) => (
            <div key={`${latch.instrument}-${latch.reason}-${latch.blocked_at_utc ?? ''}`} className="rounded-lg border border-red-800/60 bg-red-950/25 p-2 text-xs text-red-100">
              <div className="font-mono font-semibold">{latch.instrument} latch</div>
              <div className="mt-1 break-words text-red-100/80">{latch.reason || 'reason unavailable'}</div>
              <div className="mt-1 text-red-100/65">Readiness: {latch.clear_readiness?.status ?? latch.clear_policy ?? 'unknown'}</div>
            </div>
          ))}
          {unprotectedPositions.map((position) => (
            <div key={`${position.intent_id}-${position.instrument}`} className="rounded-lg border border-red-800/60 bg-red-950/25 p-2 text-xs text-red-100">
              <div className="font-mono font-semibold">{position.instrument} unprotected</div>
              <div className="mt-1">Stream {position.stream} intent {position.intent_id}</div>
              <div className="mt-1 text-red-100/65">{Math.round(position.unprotected_duration_seconds)}s since entry</div>
            </div>
          ))}
          {flattenRiskRows.map((stream) => (
            <div key={`${stream.trading_date}-${stream.stream}-flatten-risk`} className="rounded-lg border border-red-800/60 bg-red-950/25 p-2 text-xs text-red-100">
              <div className="font-mono font-semibold">{stream.stream} flatten risk</div>
              <div className="mt-1">{stream.execution_instrument || stream.instrument}: {stream.flatten_status}</div>
              <div className="mt-1 text-red-100/65">Trigger {stream.flatten_trigger_ct ?? '-'}</div>
            </div>
          ))}
          {carriedActiveLifecycles.map((row) => (
            <div key={`${row.trading_date}-${row.stream}-carry`} className="rounded-lg border border-sky-700/60 bg-sky-950/25 p-2 text-xs text-sky-100">
              <div className="font-mono font-semibold">{row.stream} carried lifecycle</div>
              <div className="mt-1">{row.instrument} {row.state} from {row.trading_date}</div>
              <div className="mt-1 text-sky-100/65">{row.same_stream_deferred_reason ?? row.note ?? 'retained until terminal'}</div>
            </div>
          ))}
          {outOfTimetableActiveStreams.map((row) => (
            <div key={`${row.intent_id}-oot`} className="rounded-lg border border-amber-700/60 bg-amber-950/25 p-2 text-xs text-amber-100">
              <div className="font-mono font-semibold">{row.stream_id} not in timetable</div>
              <div className="mt-1">{row.instrument} remaining {row.remaining_exposure}</div>
              <div className="mt-1 text-amber-100/65">{row.trading_date} intent {row.intent_id}</div>
            </div>
          ))}
          {blockedStreams.map((stream) => (
            <div key={`${stream.trading_date}-${stream.stream}-blocked`} className="rounded-lg border border-amber-700/60 bg-amber-950/25 p-2 text-xs text-amber-100">
              <div className="font-mono font-semibold">{stream.stream} blocked</div>
              <div className="mt-1">{stream.instrument_block_reason ?? 'instrument block active'}</div>
            </div>
          ))}
          {stuckStreams.map((stream) => (
            <div key={`${stream.instrument}-${stream.stream}-stuck`} className="rounded-lg border border-slate-700/70 bg-slate-900/55 p-2 text-xs text-slate-200">
              <div className="font-mono font-semibold">{stream.stream} long state</div>
              <div className="mt-1">{stream.instrument} {stream.state}</div>
              <div className="mt-1 text-slate-400">{formatDuration(Math.round(stream.stuck_duration_seconds))} in state</div>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
