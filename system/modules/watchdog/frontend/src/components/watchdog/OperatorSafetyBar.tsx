import type {
  IntentExposure,
  RunSummaryResult,
  StreamState,
  UnprotectedPosition,
  WatchdogStatus,
} from '../../types/watchdog'
import { isRunSummaryUnavailable } from '../../types/watchdog'
import type { OverallExecutionDerived } from '../../utils/executionSeverity'

type AlertLike = {
  type: 'critical' | 'degraded'
}

type Props = {
  status: WatchdogStatus | null
  summary: RunSummaryResult | null
  overallExecution: OverallExecutionDerived
  streams: StreamState[]
  activeIntents: IntentExposure[]
  unprotectedPositions: UnprotectedPosition[]
  alerts: AlertLike[]
  dataFlowStatus: string
  viewMode: 'live' | 'run'
}

type TileTone = 'safe' | 'warn' | 'danger' | 'neutral'

const toneClasses: Record<TileTone, string> = {
  safe: 'border-emerald-700/70 bg-emerald-950/20 text-emerald-100',
  warn: 'border-amber-600/70 bg-amber-950/20 text-amber-100',
  danger: 'border-red-700/75 bg-red-950/30 text-red-100',
  neutral: 'border-slate-700/70 bg-slate-900/65 text-slate-100',
}

function numberValue(value: unknown): number {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const parsed = Number(value)
    return Number.isFinite(parsed) ? parsed : 0
  }
  return 0
}

function summaryCount(summary: RunSummaryResult | null, key: string): number {
  if (!summary || isRunSummaryUnavailable(summary)) return 0
  return numberValue(summary.key_counts?.[key])
}

function shortId(value: string | null | undefined): string {
  if (!value) return '-'
  return value.length > 12 ? value.slice(0, 12) : value
}

function Tile({
  label,
  value,
  detail,
  tone,
}: {
  label: string
  value: string
  detail?: string
  tone: TileTone
}) {
  return (
    <div className={`rounded-lg border px-3 py-2 ${toneClasses[tone]}`}>
      <div className="text-[10px] font-semibold uppercase tracking-[0.12em] opacity-70">{label}</div>
      <div className="mt-1 truncate font-mono text-base font-semibold leading-tight" title={value}>
        {value}
      </div>
      {detail && <div className="mt-1 truncate text-[11px] opacity-70" title={detail}>{detail}</div>}
    </div>
  )
}

export function OperatorSafetyBar({
  status,
  summary,
  overallExecution,
  streams,
  activeIntents,
  unprotectedPositions,
  alerts,
  dataFlowStatus,
  viewMode,
}: Props) {
  const authorityEntries = Object.values(status?.position_authority_by_instrument ?? {})
  const liveBrokerQty = authorityEntries.reduce((sum, row) => sum + Math.abs(numberValue(row.broker_qty)), 0)
  const liveJournalQty = authorityEntries.reduce((sum, row) => sum + Math.abs(numberValue(row.journal_open_qty)), 0)
  const liveWorkingKnown = authorityEntries.some((row) => row.broker_working_count !== null && row.broker_working_count !== undefined)
  const liveWorkingOrders = authorityEntries.reduce((sum, row) => sum + Math.abs(numberValue(row.broker_working_count)), 0)
  const liveIeaWorkingOrders = authorityEntries.reduce((sum, row) => sum + Math.abs(numberValue(row.iea_trusted_working_count)), 0)
  const activeIntentQty = activeIntents.reduce((sum, intent) => sum + Math.abs(numberValue(intent.remaining_exposure)), 0)
  const shutdownBrokerQty = summaryCount(summary, 'broker_position_qty_at_shutdown')
  const shutdownWorkingOrders = summaryCount(summary, 'broker_working_orders_at_shutdown')
  const shutdownOpenStreams = summaryCount(summary, 'open_position_at_shutdown')
  const criticalCount = alerts.filter((alert) => alert.type === 'critical').length
  const activeLatchCount = status?.active_risk_latch_count ?? status?.active_risk_latches?.length ?? 0
  const flattenRiskCount = streams.filter((stream) => {
    const statusText = String(stream.flatten_status ?? 'NOT_TRIGGERED').toUpperCase()
    return statusText.includes('FAILED') || statusText.includes('TIMEOUT') || statusText.includes('REMAIN')
  }).length

  const summaryAvailable = Boolean(summary && !isRunSummaryUnavailable(summary))
  const engineSummary = summaryAvailable ? summary as Exclude<RunSummaryResult, { available: false }> : null
  const platformCrash =
    engineSummary?.watchdog_platform_diagnostics?.had_platform_crash_or_freeze_signal === true ||
    engineSummary?.flags?.had_crash_or_freeze_signal === true
  const runFailed = engineSummary ? String(engineSummary.status).toUpperCase() === 'FAIL' : false
  const proofLevel =
    engineSummary?.watchdog_overlay?.proof_level ??
    (engineSummary ? `${engineSummary.mode}/runtime artifact` : 'status only')
  const deployedRuntime = engineSummary?.deployed_runtime ?? null
  const deployedHash =
    deployedRuntime?.sha256 ??
    deployedRuntime?.robot_build_signature_event?.data?.assembly_hash ??
    null
  const deployedPath =
    deployedRuntime?.path ??
    deployedRuntime?.robot_build_signature_event?.data?.assembly_location ??
    null

  const executionTone: TileTone =
    overallExecution.overall_execution_severity === 'CRITICAL' || overallExecution.execution_blocked
      ? 'danger'
      : overallExecution.overall_execution_severity === 'WARNING'
        ? 'warn'
        : 'safe'
  const exposureTone: TileTone =
    liveBrokerQty > 0 || liveJournalQty > 0 || activeIntentQty > 0 || shutdownBrokerQty > 0
      ? 'danger'
      : 'safe'
  const orderTone: TileTone =
    liveWorkingOrders > 0 || shutdownWorkingOrders > 0
      ? 'danger'
      : liveWorkingKnown
        ? 'safe'
        : 'warn'
  const criticalTone: TileTone =
    criticalCount > 0 || activeLatchCount > 0 || unprotectedPositions.length > 0 || platformCrash
      ? 'danger'
      : flattenRiskCount > 0
        ? 'warn'
        : 'safe'
  const proofTone: TileTone =
    !engineSummary
      ? 'warn'
      : runFailed || platformCrash
        ? 'danger'
        : deployedRuntime && deployedRuntime.exists === false
          ? 'danger'
          : 'safe'

  const runDetail = engineSummary
    ? `${engineSummary.status} ${engineSummary.status_reason}`
    : summary && isRunSummaryUnavailable(summary)
      ? summary.reason
      : 'summary loading'

  return (
    <section className="sticky top-[6.6rem] z-30 rounded-lg border border-slate-700/70 bg-slate-950/95 p-3 shadow-[0_16px_34px_rgba(0,0,0,0.28)] backdrop-blur">
      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-400">
            System safety bar
          </div>
          <div className="text-xs text-slate-500">
            Broker truth, protection, proof, and mode stay visible before diagnostics.
          </div>
        </div>
        <div className="font-mono text-[11px] text-slate-400">
          {viewMode.toUpperCase()} | session {status?.session_trading_date ?? status?.trading_date ?? engineSummary?.date ?? '-'}
        </div>
      </div>
      <div className="grid grid-cols-2 gap-2 md:grid-cols-3 xl:grid-cols-6">
        <Tile
          label="Execution"
          value={overallExecution.tradable ? 'TRADABLE' : 'BLOCKED'}
          detail={overallExecution.overlay_blocker_hint ?? overallExecution.overall_execution_reason}
          tone={executionTone}
        />
        <Tile
          label="Broker exposure"
          value={`${liveBrokerQty} live / ${shutdownBrokerQty} run`}
          detail={`journal ${liveJournalQty}; intents ${activeIntentQty}`}
          tone={exposureTone}
        />
        <Tile
          label="Working orders"
          value={liveWorkingKnown ? `${liveWorkingOrders} live / ${shutdownWorkingOrders} run` : `unknown / ${shutdownWorkingOrders} run`}
          detail={
            liveWorkingKnown
              ? `IEA trusted ${liveIeaWorkingOrders}; ${shutdownOpenStreams} open stream(s) at shutdown`
              : 'live count not in status payload'
          }
          tone={orderTone}
        />
        <Tile
          label="Protection"
          value={unprotectedPositions.length > 0 ? `${unprotectedPositions.length} UNPROTECTED` : 'covered/none'}
          detail={`${activeIntents.length} active intent(s)`}
          tone={unprotectedPositions.length > 0 ? 'danger' : 'safe'}
        />
        <Tile
          label="Criticals"
          value={String(criticalCount + activeLatchCount)}
          detail={`${activeLatchCount} latch; ${flattenRiskCount} flatten risk`}
          tone={criticalTone}
        />
        <Tile
          label="DLL proof"
          value={deployedHash ? `DLL ${shortId(deployedHash)}` : proofLevel}
          detail={
            deployedHash
              ? `${deployedRuntime?.exists === false ? 'missing' : 'loaded'} | ${shortId(engineSummary?.run_id)} | ${deployedPath ?? proofLevel}`
              : engineSummary ? `${shortId(engineSummary.run_id)} | ${runDetail}` : runDetail
          }
          tone={proofTone}
        />
      </div>
      <div className="mt-2 grid grid-cols-2 gap-2 text-[11px] text-slate-400 md:grid-cols-4">
        <div>Data: <span className="font-mono text-slate-200">{dataFlowStatus}</span></div>
        <div>Broker: <span className="font-mono text-slate-200">{status?.derived_connection_state ?? status?.connection_status ?? '-'}</span></div>
        <div>Timetable: <span className="font-mono text-slate-200">{status?.timetable_source ?? '-'}</span></div>
        <div>Mode: <span className="font-mono text-slate-200">{engineSummary?.mode ?? viewMode.toUpperCase()}</span></div>
      </div>
    </section>
  )
}
