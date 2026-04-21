/**
 * WatchdogHeader component - Global sticky header
 */
import { ReactNode, useMemo } from 'react'
import { CopyableText } from '../ui/CopyableText'
import { formatChicagoTime } from '../../utils/timeUtils.ts'
import type { OverallExecutionDerived } from '../../utils/executionSeverity'
import { overallExecutionOperatorMessage } from '../../utils/executionSeverity'

interface WatchdogHeaderProps {
  runId: string | null
  viewMode?: 'live' | 'run'
  /** Engine activity only - not execution severity (see overallExecution). */
  engineStatus: 'ALIVE' | 'STALLED' | 'RECOVERY_IN_PROGRESS' | 'IDLE_MARKET_CLOSED'
  marketOpen: boolean | null
  connectionStatus: string | null
  /** LOST | RECOVERING | STABLE - when present, used for badge (disconnected / recovering / stable) */
  derivedConnectionState?: 'LOST' | 'RECOVERING' | 'STABLE' | null
  dataFlowStatus: 'FLOWING' | 'STALLED' | 'ACCEPTABLE_SILENCE' | 'MARKET_CLOSED' | 'UNKNOWN'
  chicagoTime: string
  /** When provided, renders this instead of chicagoTime (isolates 1s clock updates) */
  clockSlot?: ReactNode
  lastEngineTick: string | null
  lastSuccessfulPollTimestamp: number | null
  identityInvariantsPass: boolean | null
  identityViolations: string[]
  barsExpectedCount?: number
  worstLastBarAgeSeconds?: number | null
  /** Canonical execution severity (single source of truth). */
  overallExecution: OverallExecutionDerived
  /** Optional hash short compare for TIMETABLE_DRIFT (publisher identity vs robot heartbeat). */
  executionHashDetail?: {
    robot?: string | null
    publisher?: string | null
    content?: string | null
  }
}

const badgeClass =
  'inline-flex items-center rounded-full px-3 py-1.5 text-xs font-semibold leading-none whitespace-nowrap'

export function WatchdogHeader({
  runId,
  viewMode = 'live',
  engineStatus,
  marketOpen,
  connectionStatus,
  derivedConnectionState,
  dataFlowStatus,
  chicagoTime,
  lastEngineTick,
  lastSuccessfulPollTimestamp,
  identityInvariantsPass,
  identityViolations,
  barsExpectedCount,
  worstLastBarAgeSeconds,
  clockSlot,
  overallExecution,
  executionHashDetail,
}: WatchdogHeaderProps) {
  const dataFreshness = useMemo(() => {
    if (!lastSuccessfulPollTimestamp) return 'STALE'
    const elapsed = Date.now() - lastSuccessfulPollTimestamp
    return elapsed > 10000 ? 'STALE' : 'OK'
  }, [lastSuccessfulPollTimestamp])

  const shortHash = (value: string | null | undefined) => {
    if (!value) return '-'
    return value.length > 12 ? `${value.slice(0, 8)}...` : value
  }

  const getEngineStatusBadge = () => {
    switch (engineStatus) {
      case 'ALIVE':
        return (
          <span className={`${badgeClass} bg-green-600 text-white`} title="Engine alive">
            Engine Alive
          </span>
        )
      case 'IDLE_MARKET_CLOSED': {
        const idleMessage =
          marketOpen === false
            ? 'Engine idle (market closed)'
            : barsExpectedCount === 0
              ? 'Engine idle (waiting for range windows)'
              : 'Engine idle'
        return (
          <span className={`${badgeClass} bg-gray-500 text-white`} title={idleMessage}>
            Engine Idle
          </span>
        )
      }
      case 'STALLED': {
        const stallMessage =
          worstLastBarAgeSeconds !== null && worstLastBarAgeSeconds !== undefined
            ? `Engine stalled (no bars for ${Math.round(worstLastBarAgeSeconds / 60)} min)`
            : barsExpectedCount !== undefined && barsExpectedCount > 0
              ? `Engine stalled (${barsExpectedCount} instrument(s) expecting bars)`
              : 'Engine stalled'
        return (
          <span className={`${badgeClass} bg-red-600 text-white`} title={stallMessage}>
            Engine Stalled
          </span>
        )
      }
      case 'RECOVERY_IN_PROGRESS':
        return (
          <span className={`${badgeClass} bg-amber-500 text-black`} title="Recovery in progress">
            Recovery
          </span>
        )
      default:
        return <span className={`${badgeClass} bg-gray-600 text-white`}>Unknown</span>
    }
  }

  const getExecutionSeverityBadge = () => {
    const message = overallExecutionOperatorMessage(overallExecution)
    const severity = overallExecution.overall_execution_severity
    const tradableNotBlockedWarning =
      severity === 'WARNING' && overallExecution.execution_blocked === false

    if (severity === 'SAFE' || tradableNotBlockedWarning) {
      const title =
        tradableNotBlockedWarning &&
        overallExecution.overall_execution_reason === 'RECONCILIATION_GATE_ENGAGED'
          ? `Overlay tradable. Reconciliation gate engaged - see system status diagnostic. ${message || ''}`
          : message
      return (
        <span
          data-testid="execution-severity-badge"
          className={`${badgeClass} bg-emerald-700 text-white`}
          title={title}
        >
          Overlay OK
        </span>
      )
    }

    if (severity === 'WARNING') {
      return (
        <span
          data-testid="execution-severity-badge"
          className={`${badgeClass} bg-amber-500 text-black`}
          title={message}
        >
          Overlay Blocked
        </span>
      )
    }

    if (severity === 'CRITICAL') {
      const isDrift = overallExecution.overall_execution_reason === 'TIMETABLE_DRIFT'
      const hashSuffix =
        isDrift && executionHashDetail
          ? ` Robot: ${shortHash(executionHashDetail.robot)} | Publisher: ${shortHash(executionHashDetail.publisher)}${
              executionHashDetail.content ? ` | Content: ${shortHash(executionHashDetail.content)}` : ''
            }`
          : ''
      return (
        <span
          data-testid="execution-severity-badge"
          className={`${badgeClass} bg-red-700 text-white`}
          title={`${message}${hashSuffix}`}
        >
          {isDrift ? 'Execution Drift' : 'Execution Critical'}
        </span>
      )
    }

    return (
      <span
        data-testid="execution-severity-badge"
        className={`${badgeClass} bg-gray-600 text-white`}
        title={message}
      >
        Execution Unknown
      </span>
    )
  }

  const getBrokerStatusBadge = () => {
    if (derivedConnectionState === 'LOST') {
      return (
        <span className={`${badgeClass} bg-red-600 text-white`} title="Broker disconnected">
          Broker Lost
        </span>
      )
    }
    if (derivedConnectionState === 'RECOVERING') {
      return (
        <span className={`${badgeClass} bg-amber-500 text-black`} title="Broker recovering">
          Broker Recovering
        </span>
      )
    }
    if (derivedConnectionState === 'STABLE' || connectionStatus === 'Connected') {
      return (
        <span className={`${badgeClass} bg-green-600 text-white`} title="Broker connected">
          Broker Connected
        </span>
      )
    }
    if (connectionStatus === null) {
      return (
        <span className={`${badgeClass} bg-gray-600 text-white`} title="Broker status unknown">
          Broker Unknown
        </span>
      )
    }
    return (
      <span className={`${badgeClass} bg-red-600 text-white`} title="Broker disconnected">
        Broker Lost
      </span>
    )
  }

  const getDataFlowBadge = () => {
    switch (dataFlowStatus) {
      case 'FLOWING':
        return (
          <span className={`${badgeClass} bg-green-600 text-white`} title="Data flowing">
            Data Flowing
          </span>
        )
      case 'STALLED':
        return <span className={`${badgeClass} bg-red-600 text-white`}>Data Stalled</span>
      case 'MARKET_CLOSED':
        return (
          <span className={`${badgeClass} bg-gray-500 text-white`} title="Market closed">
            Data Closed
          </span>
        )
      case 'ACCEPTABLE_SILENCE':
        return (
          <span className={`${badgeClass} bg-gray-500 text-white`} title="Acceptable silence">
            Data Silent
          </span>
        )
      default:
        return (
          <span className={`${badgeClass} bg-gray-600 text-white`} title="Data status unknown">
            Data Unknown
          </span>
        )
    }
  }

  const getMarketStatusBadge = () => {
    if (marketOpen === null) {
      return (
        <span className={`${badgeClass} bg-gray-600 text-white`} title="Market status unknown">
          Market Unknown
        </span>
      )
    }
    if (marketOpen) {
      return <span className={`${badgeClass} bg-green-600 text-white`}>Market Open</span>
    }
    return <span className={`${badgeClass} bg-gray-500 text-white`}>Market Closed</span>
  }

  const getIdentityBadge = () => {
    if (identityInvariantsPass === null) {
      return (
        <span
          className={`${badgeClass} bg-gray-600 text-white`}
          title="Identity status not yet checked"
        >
          Identity Unknown
        </span>
      )
    }
    if (identityInvariantsPass) {
      return (
        <span
          className={`${badgeClass} bg-green-600 text-white`}
          title="All identity invariants passed"
        >
          Identity OK
        </span>
      )
    }

    const violationsText =
      identityViolations.length > 0 ? identityViolations.join('; ') : 'Unknown violation'
    return (
      <span
        className={`${badgeClass} cursor-help bg-red-600 text-white`}
        title={`Identity violations: ${violationsText}`}
      >
        Identity Fail
      </span>
    )
  }

  const getViewModeBadge = () => {
    if (viewMode === 'run') {
      return (
        <span
          className={`${badgeClass} bg-sky-700 text-white`}
          title="Dashboard is scoped to the selected run"
        >
          Run View
        </span>
      )
    }
    return (
      <span
        className={`${badgeClass} bg-gray-700 text-gray-100`}
        title="Dashboard is following the live watchdog state"
      >
        Live View
      </span>
    )
  }

  return (
    <header className="fixed left-0 right-0 top-11 z-40 border-b border-slate-700/70 bg-slate-950/85 backdrop-blur-md">
      <div className="watchdog-content flex min-h-[56px] items-center gap-3 px-2 py-2">
        <div className="flex min-w-0 max-w-[26%] flex-[0.75] items-center gap-2">
          <div className="hidden min-w-0 shrink xl:block">
            <h1 className="truncate text-sm font-semibold tracking-[0.02em] text-slate-100">
              QTSW2 Watchdog
            </h1>
          </div>
          {getViewModeBadge()}
          {runId && (
            <CopyableText
              text={runId}
              className="min-w-0 shrink"
              textClassName="block max-w-[8rem] truncate rounded-md bg-slate-800/70 px-2 py-1 text-xs text-slate-200 xl:max-w-[10rem]"
              buttonClassName="shrink-0 bg-slate-700/80 hover:bg-slate-600/90"
            />
          )}
        </div>

        <div className="flex min-w-0 flex-1 flex-nowrap items-center justify-center gap-2 overflow-hidden">
          {getEngineStatusBadge()}
          {getExecutionSeverityBadge()}
          {getBrokerStatusBadge()}
          {getDataFlowBadge()}
          {getMarketStatusBadge()}
          {getIdentityBadge()}
        </div>

        <div className="flex shrink-0 items-center justify-end gap-2 whitespace-nowrap">
          <div className={`text-xs ${dataFreshness === 'OK' ? 'text-emerald-400' : 'text-rose-400'}`}>
            {dataFreshness}
          </div>
          <div className="rounded-full border border-slate-700/70 bg-slate-900/80 px-2.5 py-1 text-xs font-mono text-slate-100">
            {clockSlot ?? chicagoTime}
          </div>
          {lastEngineTick && (
            <div className="hidden text-xs text-slate-400 xl:block">
              Last Tick: {formatChicagoTime(lastEngineTick)}
            </div>
          )}
        </div>
      </div>
    </header>
  )
}
