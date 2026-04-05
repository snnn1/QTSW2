/**
 * WatchdogHeader component - Global sticky header
 */
import { useMemo, ReactNode } from 'react'
import { CopyableText } from '../ui/CopyableText'
import { formatChicagoTime } from '../../utils/timeUtils.ts'
import type { OverallExecutionDerived } from '../../utils/executionSeverity'
import { overallExecutionOperatorMessage } from '../../utils/executionSeverity'

interface WatchdogHeaderProps {
  runId: string | null
  /** Engine activity only — not execution severity (see overallExecution). */
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

export function WatchdogHeader({
  runId,
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

  const getEngineStatusBadge = () => {
    switch (engineStatus) {
      case 'ALIVE':
        return (
          <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
            ENGINE ALIVE
          </span>
        )
      case 'IDLE_MARKET_CLOSED': {
        const idleMessage =
          marketOpen === false
            ? 'ENGINE IDLE (MARKET CLOSED)'
            : barsExpectedCount === 0
              ? 'ENGINE IDLE (WAITING FOR RANGE WINDOWS)'
              : 'ENGINE IDLE'
        return (
          <span
            className="px-3 py-1.5 bg-gray-500 text-white rounded-full font-semibold text-sm whitespace-nowrap"
            title={idleMessage}
          >
            ENGINE IDLE
          </span>
        )
      }
      case 'STALLED': {
        const stallMessage =
          worstLastBarAgeSeconds !== null && worstLastBarAgeSeconds !== undefined
            ? `ENGINE STALLED (No bars for ${Math.round(worstLastBarAgeSeconds / 60)} min)`
            : barsExpectedCount !== undefined && barsExpectedCount > 0
              ? `ENGINE STALLED (${barsExpectedCount} instrument(s) expecting bars)`
              : 'ENGINE STALLED'
        return (
          <span
            className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap"
            title={stallMessage}
          >
            ENGINE STALLED
          </span>
        )
      }
      case 'RECOVERY_IN_PROGRESS':
        return (
          <span className="px-3 py-1.5 bg-amber-500 text-black rounded-full font-semibold text-sm whitespace-nowrap">
            RECOVERY IN PROGRESS
          </span>
        )
      default:
        return (
          <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
            UNKNOWN
          </span>
        )
    }
  }

  const shortHash = (h: string | null | undefined) => {
    if (h == null || h === '') return '—'
    return h.length > 12 ? `${h.slice(0, 8)}…` : h
  }

  const getExecutionSeverityBadge = () => {
    const msg = overallExecutionOperatorMessage(overallExecution)
    const sev = overallExecution.overall_execution_severity
    if (sev === 'SAFE') {
      return (
        <span
          data-testid="execution-severity-badge"
          className="px-3 py-1.5 bg-emerald-700 text-white rounded-full font-semibold text-sm whitespace-nowrap"
          title={msg}
        >
          EXECUTION SAFE
        </span>
      )
    }
    if (sev === 'WARNING') {
      return (
        <span
          data-testid="execution-severity-badge"
          className="px-3 py-1.5 bg-amber-500 text-black rounded-full font-semibold text-sm whitespace-nowrap"
          title={msg}
        >
          EXECUTION WARNING
        </span>
      )
    }
    if (sev === 'CRITICAL') {
      const isDrift = overallExecution.overall_execution_reason === 'TIMETABLE_DRIFT'
      const hashSuffix =
        isDrift && executionHashDetail
          ? ` Robot: ${shortHash(executionHashDetail.robot)} · Publisher: ${shortHash(executionHashDetail.publisher)}` +
            (executionHashDetail.content
              ? ` · Content hash: ${shortHash(executionHashDetail.content)}`
              : '')
          : ''
      return (
        <span
          data-testid="execution-severity-badge"
          className="px-3 py-1.5 bg-red-700 text-white rounded-full font-semibold text-sm whitespace-nowrap"
          title={msg + hashSuffix}
        >
          {isDrift ? 'EXECUTION CRITICAL (Timetable Drift)' : 'EXECUTION CRITICAL'}
        </span>
      )
    }
    return (
      <span
        data-testid="execution-severity-badge"
        className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap"
        title={msg}
      >
        EXECUTION UNKNOWN
      </span>
    )
  }

  const getBrokerStatusBadge = () => {
    if (derivedConnectionState === 'LOST') {
      return (
        <span className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
          BROKER DISCONNECTED
        </span>
      )
    }
    if (derivedConnectionState === 'RECOVERING') {
      return (
        <span
          className="px-3 py-1.5 bg-amber-500 text-black rounded-full font-semibold text-sm whitespace-nowrap"
          title="Recovering - not safe yet"
        >
          BROKER RECOVERING
        </span>
      )
    }
    if (derivedConnectionState === 'STABLE') {
      return (
        <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
          BROKER CONNECTED
        </span>
      )
    }
    if (connectionStatus === null) {
      return (
        <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
          BROKER UNKNOWN
        </span>
      )
    }
    if (connectionStatus === 'Connected') {
      return (
        <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
          BROKER CONNECTED
        </span>
      )
    }
    return (
      <span className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
        BROKER DISCONNECTED
      </span>
    )
  }

  const getDataFlowBadge = () => {
    switch (dataFlowStatus) {
      case 'FLOWING':
        return (
          <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
            DATA FLOWING
          </span>
        )
      case 'STALLED':
        return (
          <span className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
            DATA STALLED
          </span>
        )
      case 'MARKET_CLOSED':
        return (
          <span className="px-3 py-1.5 bg-gray-500 text-white rounded-full font-semibold text-sm whitespace-nowrap">
            MARKET CLOSED
          </span>
        )
      case 'ACCEPTABLE_SILENCE':
        return (
          <span className="px-3 py-1.5 bg-gray-500 text-white rounded-full font-semibold text-sm whitespace-nowrap">
            DATA SILENT (OK)
          </span>
        )
      default:
        return (
          <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
            DATA UNKNOWN
          </span>
        )
    }
  }

  const getMarketStatusBadge = () => {
    if (marketOpen === null) {
      return (
        <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
          MARKET UNKNOWN
        </span>
      )
    }
    if (marketOpen) {
      return (
        <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">
          MARKET OPEN
        </span>
      )
    }
    return (
      <span className="px-3 py-1.5 bg-gray-500 text-white rounded-full font-semibold text-sm whitespace-nowrap">
        MARKET CLOSED
      </span>
    )
  }

  const getIdentityBadge = () => {
    if (identityInvariantsPass === null) {
      return (
        <span
          className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap"
          title="Identity status not yet checked"
        >
          IDENTITY UNKNOWN
        </span>
      )
    }
    if (identityInvariantsPass) {
      return (
        <span
          className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap"
          title="All identity invariants passed"
        >
          IDENTITY OK
        </span>
      )
    }
    const violationsText =
      identityViolations.length > 0 ? identityViolations.join('; ') : 'Unknown violation'
    return (
      <span
        className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap cursor-help"
        title={`Identity violations: ${violationsText}`}
      >
        IDENTITY VIOLATION
      </span>
    )
  }

  return (
    <header className="fixed top-6 left-0 right-0 h-10 bg-gray-900 border-b border-gray-700 z-40 flex items-center px-3">
      <div className="flex items-center gap-4 flex-1">
        <h1 className="text-base font-bold">QTSW2 Execution Watchdog</h1>
        {runId && <CopyableText text={runId} />}
      </div>

      <div className="flex-1 flex justify-center items-center gap-2">
        {getEngineStatusBadge()}
        {getExecutionSeverityBadge()}
        {getBrokerStatusBadge()}
        {getDataFlowBadge()}
        {getMarketStatusBadge()}
        {getIdentityBadge()}
      </div>

      <div className="flex items-center gap-4 flex-1 justify-end">
        <div className={`text-sm ${dataFreshness === 'OK' ? 'text-green-500' : 'text-red-500'}`}>
          Data Freshness: {dataFreshness}
        </div>
        <div className="text-sm font-mono">{clockSlot ?? chicagoTime}</div>
        {lastEngineTick && (
          <div className="text-xs text-gray-400">Last Tick: {formatChicagoTime(lastEngineTick)}</div>
        )}
      </div>
    </header>
  )
}
