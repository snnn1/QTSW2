/**
 * WatchdogPage - Primary Live Watchdog page
 */
import { useState, useMemo, useCallback, useEffect } from 'react'
import { WatchdogHeader } from './components/watchdog/WatchdogHeader'
import { CriticalAlertBanner } from './components/watchdog/CriticalAlertBanner'
import { StreamStatusTable } from './components/watchdog/StreamStatusTable'
import { RiskGatesPanel } from './components/watchdog/RiskGatesPanel'
import { ActiveIntentPanel } from './components/watchdog/ActiveIntentPanel'
import { FillHealthCard } from './components/watchdog/FillHealthCard'
import { ExecutionIntegrityPanel } from './components/watchdog/ExecutionIntegrityPanel'
import { ActiveAlertsCard } from './components/watchdog/ActiveAlertsCard'
import { SessionConnectivityCard } from './components/watchdog/SessionConnectivityCard'
import { DisconnectFeedCard } from './components/watchdog/DisconnectFeedCard'
import { SlotLifecyclePanel } from './components/watchdog/SlotLifecyclePanel'
import { AlertsHistoryCard } from './components/watchdog/AlertsHistoryCard'
import { LiveEventFeed } from './components/watchdog/LiveEventFeed'
import { IncidentTimeline } from './components/watchdog/IncidentTimeline'
import { ActiveIncidentsPanel } from './components/watchdog/ActiveIncidentsPanel'
import { ReliabilityPanel } from './components/watchdog/ReliabilityPanel'
import { InstrumentHealthPanel } from './components/watchdog/InstrumentHealthPanel'
import { MetricsHistoryPanel } from './components/watchdog/MetricsHistoryPanel'
import { StreamDetailDrawer } from './components/watchdog/StreamDetailDrawer'
import { SystemAuthorityStatusBar } from './components/watchdog/SystemAuthorityStatusBar'
import { useWatchdogLiveSnapshot } from './hooks/useWatchdogLiveSnapshot'
import { useWatchdogEvents } from './hooks/useWatchdogEvents'
import { useRiskGates } from './hooks/useRiskGates'
import { useUnprotectedPositions } from './hooks/useUnprotectedPositions'
import { useActiveIntents } from './hooks/useActiveIntents'
import { useWatchdogAlerts } from './hooks/useWatchdogAlerts'
import { useIncidents } from './hooks/useIncidents'
import { useActiveIncidents } from './hooks/useActiveIncidents'
import { useReliabilityMetrics } from './hooks/useReliabilityMetrics'
import { useInstrumentHealth } from './hooks/useInstrumentHealth'
import { useMetricsHistory } from './hooks/useMetricsHistory'
import { useStreamPnl } from './hooks/useStreamPnl'
import { WatchdogNavigationBar } from './components/WatchdogNavigationBar'
import { ChicagoClock } from './components/watchdog/ChicagoClock'
import { TimetableIdentityDebugCard } from './components/watchdog/TimetableIdentityDebugCard'
import { RunVerdictStrip } from './components/watchdog/RunVerdictStrip'
import { HardGatesStrip } from './components/watchdog/HardGatesStrip'
import { QuantHealthStrip } from './components/watchdog/QuantHealthStrip'
import { RunTimelinePanel } from './components/watchdog/RunTimelinePanel'
import { OperatorSafetyBar } from './components/watchdog/OperatorSafetyBar'
import { ActiveRiskPanel } from './components/watchdog/ActiveRiskPanel'
import { useRunArtifacts } from './hooks/useRunArtifacts'
import { isRunSummaryUnavailable } from './types/watchdog'
import type { StreamState, WatchdogEvent } from './types/watchdog'
import { deriveOverallExecutionStatus, executionSeverityAlert } from './utils/executionSeverity'

export function WatchdogPage() {
  const [selectedStream, setSelectedStream] = useState<StreamState | null>(null)
  const [selectedEvent, setSelectedEvent] = useState<WatchdogEvent | null>(null)
  const [peekRunRoot, setPeekRunRoot] = useState<string | null>(null)
  const [autoFollowPlayback, setAutoFollowPlayback] = useState(true)
  const fullRunMode = Boolean(peekRunRoot)

  const {
    summary: runSummary,
    summaryError: runSummaryError,
    keyEvents: keyEventsPayload,
    keyEventsError: keyEventsError,
    recentRuns: recentRunsPayload,
    recentError: recentRunsError,
    activeSummary,
    activePersistenceRoot,
  } = useRunArtifacts(peekRunRoot)

  const isPlaybackRunRoot = useCallback((runRoot: string | null | undefined) => {
    if (!runRoot) return false
    const normalized = runRoot.replace(/\\/g, '/').toLowerCase()
    return normalized.includes('/runs/') || normalized.includes('/data/playback/')
  }, [])

  const activePlaybackRunRoot = useMemo(() => {
    return isPlaybackRunRoot(activePersistenceRoot) ? activePersistenceRoot : null
  }, [activePersistenceRoot, isPlaybackRunRoot])

  const activePlaybackRunId = useMemo(() => {
    if (!activePlaybackRunRoot || !activeSummary || isRunSummaryUnavailable(activeSummary)) {
      return null
    }
    return activeSummary.run_id ?? null
  }, [activePlaybackRunRoot, activeSummary])

  const clearPeek = useCallback(() => {
    setAutoFollowPlayback(false)
    setPeekRunRoot(null)
  }, [])

  const handleSelectRunRoot = useCallback((dir: string) => {
    setAutoFollowPlayback(false)
    setPeekRunRoot(dir)
  }, [])

  const enableAutoFollowPlayback = useCallback(() => {
    if (!activePlaybackRunRoot) return
    setAutoFollowPlayback(true)
    setPeekRunRoot(activePlaybackRunRoot)
  }, [activePlaybackRunRoot])

  useEffect(() => {
    if (!autoFollowPlayback || !activePlaybackRunRoot) return
    if (peekRunRoot === activePlaybackRunRoot) return
    setPeekRunRoot(activePlaybackRunRoot)
  }, [autoFollowPlayback, activePlaybackRunRoot, peekRunRoot])
  
  // Fetch data - /status + /stream-states + /slot-lifecycle in one tick (useWatchdogLiveSnapshot)
  const {
    status,
    streams,
    carriedActiveLifecycles,
    timetableUnavailable,
    outOfTimetableActiveStreams,
    executionExpectationGaps,
    flattenLookupMetrics,
    streamStateReferenceUtc,
    slotLifecycle,
    statusError,
    streamsError,
    slotLifecycleError,
    loading: liveSnapshotLoading,
    lastSuccessfulPollTimestamp: liveSnapshotPollTime,
  } = useWatchdogLiveSnapshot(peekRunRoot)
  const { events, cursor, loading: eventsLoading, error: eventsError, lastSuccessfulPollTimestamp: eventsPollTime } = useWatchdogEvents(peekRunRoot)
  const { gates, loading: gatesLoading, error: gatesError, lastSuccessfulPollTimestamp: gatesPollTime } = useRiskGates(peekRunRoot)
  const { positions: unprotectedPositions, loading: positionsLoading, error: positionsError, lastSuccessfulPollTimestamp: positionsPollTime } = useUnprotectedPositions(peekRunRoot)
  const { intents: activeIntents, loading: intentsLoading, error: intentsError, lastSuccessfulPollTimestamp: intentsPollTime } = useActiveIntents(peekRunRoot)
  const { recentAlerts, loading: alertsHistoryLoading } = useWatchdogAlerts(24, 30)
  const { incidents, loading: incidentsLoading } = useIncidents(50)
  const { active: activeIncidents, loading: activeIncidentsLoading } = useActiveIncidents()
  const { metrics, loading: metricsLoading } = useReliabilityMetrics(24)
  const { instruments: instrumentHealth, loading: instrumentHealthLoading } = useInstrumentHealth()
  const { byPeriod: metricsHistory, loading: metricsHistoryLoading } = useMetricsHistory('week', 12)
  
  // Get P&L data
  const currentTradingDate = status?.trading_date || streams[0]?.trading_date || new Date().toISOString().split('T')[0]
  const { pnl } = useStreamPnl(currentTradingDate, undefined, status?.market_open ?? null, peekRunRoot)

  useEffect(() => {
    setSelectedStream(null)
    setSelectedEvent(null)
  }, [peekRunRoot])
  
  // Calculate total P&L
  const totalPnl = useMemo(() => {
    return Object.values(pnl).reduce((sum, s) => {
      return sum + (s.realized_pnl || 0)
    }, 0)
  }, [pnl])

  const instrumentHealthDisplay = useMemo(() => {
    if (!fullRunMode) {
      return instrumentHealth
    }
    return Object.entries(status?.data_stall_detected || {}).map(([instrument, info]) => ({
      instrument,
      status: info.stall_detected ? 'DATA_STALLED' : 'OK',
      last_bar_chicago: info.last_bar_chicago ?? null,
      elapsed_seconds: info.elapsed_seconds ?? null,
    }))
  }, [fullRunMode, instrumentHealth, status?.data_stall_detected])
  
  const apiErrorItems = useMemo(
    () =>
      [
        { label: 'Status', message: statusError, blocking: !status },
        { label: 'Streams', message: streamsError, blocking: streams.length === 0 },
        { label: 'Risk Gates', message: gatesError, blocking: !gates },
        { label: 'Positions', message: positionsError, blocking: false },
        { label: 'Intents', message: intentsError, blocking: false },
        { label: 'Events', message: eventsError, blocking: false },
        { label: 'Slot lifecycle', message: slotLifecycleError, blocking: false },
      ].filter((item): item is { label: string; message: string; blocking: boolean } =>
        Boolean(item.message)
      ),
    [
      statusError,
      streamsError,
      gatesError,
      positionsError,
      intentsError,
      eventsError,
      slotLifecycleError,
      status,
      streams.length,
      gates,
    ]
  )
  const blockingApiErrors = useMemo(
    () => apiErrorItems.filter(item => item.blocking),
    [apiErrorItems]
  )
  const auxiliaryApiErrors = useMemo(
    () => apiErrorItems.filter(item => !item.blocking),
    [apiErrorItems]
  )
  const hasBlockingErrors = blockingApiErrors.length > 0
  const isLoading = liveSnapshotLoading && !status

  // Get most recent poll timestamp for data freshness
  const lastSuccessfulPollTimestamp = useMemo(() => {
    const timestamps = [
      liveSnapshotPollTime,
      eventsPollTime,
      gatesPollTime,
      positionsPollTime,
      intentsPollTime,
    ].filter((t): t is number => t !== null)
    return timestamps.length > 0 ? Math.max(...timestamps) : null
  }, [liveSnapshotPollTime, eventsPollTime, gatesPollTime, positionsPollTime, intentsPollTime])

  const overallExecution = useMemo(
    () => deriveOverallExecutionStatus(status ?? null, gates ?? null),
    [status, gates]
  )

  const engineStatus = useMemo((): 'ALIVE' | 'STALLED' | 'RECOVERY_IN_PROGRESS' | 'IDLE_MARKET_CLOSED' => {
    if (!status) return 'STALLED'

    if (status.recovery_state === 'RECOVERY_RUNNING') return 'RECOVERY_IN_PROGRESS'

    const classification = status.engine_activity_classification
    if (classification === 'RUNNING') return 'ALIVE'
    if (classification === 'IDLE') return 'IDLE_MARKET_CLOSED'
    if (classification === 'STALLED') return 'STALLED'

    switch (status.engine_activity_state) {
      case 'ACTIVE':
      case 'ENGINE_ACTIVE_PROCESSING':
        return 'ALIVE'
      case 'IDLE_MARKET_CLOSED':
      case 'ENGINE_MARKET_CLOSED':
      case 'ENGINE_IDLE_WAITING_FOR_DATA':
        return 'IDLE_MARKET_CLOSED'
      case 'STALLED':
      case 'ENGINE_STALLED':
        return 'STALLED'
      default:
        return 'IDLE_MARKET_CLOSED'
    }
  }, [status?.recovery_state, status?.engine_activity_state, status?.engine_activity_classification])
  
  // Determine data flow status - prefer backend feed_health_classification when available
  const dataFlowStatus = useMemo(() => {
    if (!status) return 'UNKNOWN'

    // Prefer backend feed_health_classification (DATA_FLOWING | DATA_STALLED | MARKET_CLOSED)
    const classification = status.feed_health_classification
    if (classification === 'DATA_FLOWING') return 'FLOWING'
    if (classification === 'DATA_STALLED') return 'STALLED'
    if (classification === 'MARKET_CLOSED') return 'MARKET_CLOSED'

    // Fallback: legacy frontend derivation
    const stalls = Object.values(status.data_stall_detected || {})
    const DATA_STALL_THRESHOLD = 300 // Match backend threshold (seconds)
    
    // Helper: compute engine tick age in seconds (same source as engine liveness)
    const getEngineTickAgeSeconds = (): number | null => {
      const tickStr = status?.last_engine_tick_chicago
      if (!tickStr) return null
      try {
        const tickMs = new Date(tickStr).getTime()
        if (isNaN(tickMs)) return null
        return (Date.now() - tickMs) / 1000
      } catch {
        return null
      }
    }
    
    // CRITICAL: When ticks are flowing (ENGINE_TICK_CALLSITE recent), treat as FLOWING even if bar age is stale.
    // Bar age can lag due to ONBARUPDATE_CALLED rate limit (1/min) or bar type (e.g. 5-min bars).
    // Ticks = OnMarketData firing = data feed alive.
    const tickAge = getEngineTickAgeSeconds()
    const ticksFlowing = tickAge !== null && tickAge < DATA_STALL_THRESHOLD
    
    // If no instruments tracked yet, check bars (worst_last_bar_age) or engine tick (fallback)
    if (stalls.length === 0) {
      // If market is known to be closed, show acceptable silence
      if (status.market_open === false) {
        return 'ACCEPTABLE_SILENCE'
      }
      
      // Primary: bars are being received (worst_last_bar_age_seconds set and recent)
      if (status.worst_last_bar_age_seconds !== null && status.worst_last_bar_age_seconds !== undefined) {
        if (status.worst_last_bar_age_seconds < DATA_STALL_THRESHOLD) {
          return 'FLOWING'
        } else if (status.worst_last_bar_age_seconds < 150) {
          return 'FLOWING'
        } else {
          // Bar age stale - but if ticks flowing, data is flowing (fix for false DATA STALLED)
          if (ticksFlowing) return 'FLOWING'
          return status.market_open ? 'STALLED' : 'ACCEPTABLE_SILENCE'
        }
      }
      
      // Fallback: use engine tick (same as engine liveness - ENGINE_TICK_CALLSITE)
      // Engine tick = bars being processed; if tick is recent, data is flowing
      if (tickAge !== null && tickAge < DATA_STALL_THRESHOLD) {
        return 'FLOWING'
      }
      if (tickAge !== null && tickAge < 150) {
        return 'FLOWING'
      }
      if (tickAge !== null && tickAge >= DATA_STALL_THRESHOLD) {
        return status.market_open ? 'STALLED' : 'ACCEPTABLE_SILENCE'
      }
      
      return 'UNKNOWN'
    }
    
    // Check for critical stalls (market open + stalled)
    // If ticks flowing, override - bar age can lag; ticks indicate data feed alive
    const criticalStall = stalls.some(d => d.stall_detected && d.market_open)
    if (criticalStall) {
      return ticksFlowing ? 'FLOWING' : 'STALLED'
    }
    
    // Check for acceptable silence (market closed + stalled)
    const acceptableSilence = stalls.some(d => d.stall_detected && !d.market_open)
    if (acceptableSilence) {
      return 'ACCEPTABLE_SILENCE'
    }
    
    // No stalls detected = data flowing
    return 'FLOWING'
  }, [status?.feed_health_classification, status?.data_stall_detected, status?.market_open, status?.worst_last_bar_age_seconds, status?.last_engine_tick_chicago])
  
  // Build critical alerts (includes Phase 1 push alerts)
  const alerts = useMemo(() => {
    const result: Array<{ type: 'critical' | 'degraded'; message: string; scrollTo?: string }> = []

    if (!status) return result

    const execAlert = executionSeverityAlert(overallExecution)
    if (execAlert) result.push(execAlert)

    // Phase 1: Active push alerts (process stopped, heartbeat lost, etc.)
    const activeAlerts = status.active_alerts ?? []
    for (const a of activeAlerts) {
      const label = a.alert_type.replace(/_/g, ' ')
      result.push({
        type: (a.severity === 'critical' ? 'critical' : 'degraded') as 'critical' | 'degraded',
        message: `[Watchdog] ${label}`,
        scrollTo: 'active-alerts-panel',
      })
    }

    const activeRiskLatches = status.active_risk_latches ?? []
    for (const latch of activeRiskLatches) {
      result.push({
        type: 'critical',
        message: `RISK LATCH: ${latch.instrument} ${latch.reason || 'instrument blocked'}`,
        scrollTo: 'risk-latches-panel',
      })
    }
    
    if (unprotectedPositions.length > 0) {
      result.push({
        type: 'critical',
        message: `UNPROTECTED: ${unprotectedPositions.length}`,
        scrollTo: 'active-intent-panel'
      })
    }
    
    // PATTERN 1: Only show stall alert if bars are expected but absent
    if (status.engine_activity_state === 'STALLED' || status.engine_activity_state === 'ENGINE_STALLED') {
      const barsExpected = status.bars_expected_count || 0
      if (barsExpected > 0) {
        result.push({
          type: 'critical',
          message: `ENGINE STALLED: ${barsExpected} instrument(s) expecting bars but none received`,
          scrollTo: 'watchdog-header'
        })
      } else {
        // No bars expected - this shouldn't happen, but handle gracefully
        result.push({
          type: 'critical',
          message: 'ENGINE STALLED (No bars expected - check configuration)',
          scrollTo: 'watchdog-header'
        })
      }
    }
    
    const stalls = Object.values(status.data_stall_detected || {})
    
    const criticalStall = stalls.some(
      d => d.stall_detected && d.market_open
    )
    
    const closedMarketSilence = stalls.some(
      d => !d.market_open
    )
    
    if (criticalStall) {
      result.push({
        type: 'critical',
        message: 'DATA STALL DETECTED (Market Open)',
        scrollTo: 'stream-table'
      })
    } else if (closedMarketSilence && stalls.some(d => d.stall_detected)) {
      result.push({
        type: 'degraded',
        message: 'No data flow (Market Closed)',
        scrollTo: 'stream-table'
      })
    }
    
    if (status.recovery_state === 'RECOVERY_RUNNING') {
      result.push({
        type: 'degraded',
        message: 'RECOVERY IN PROGRESS',
        scrollTo: 'risk-gates-panel'
      })
    }

    const streamsUnknown = Boolean(status.enabled_streams_unknown ?? status.timetable_unavailable ?? timetableUnavailable)
    if (streamsUnknown) {
      result.push({
        type: 'critical',
        message: 'NO VALID TIMETABLE - TRADING DISABLED',
        scrollTo: 'stream-table'
      })
    }

    return result
  }, [status, unprotectedPositions, timetableUnavailable, overallExecution])
  
  return (
    <div className="watchdog-page-shell text-white">
      <WatchdogHeader
        identityInvariantsPass={status?.last_identity_invariants_pass ?? null}
        identityViolations={status?.last_identity_violations ?? []}
        runId={cursor.runId}
        viewMode={fullRunMode ? 'run' : 'live'}
        engineStatus={engineStatus}
        marketOpen={status?.market_open !== undefined ? status.market_open : null}
        connectionStatus={status?.connection_status ?? null}
        derivedConnectionState={status?.derived_connection_state ?? null}
        dataFlowStatus={dataFlowStatus}
        chicagoTime=""
        clockSlot={<ChicagoClock />}
        lastEngineTick={status?.last_engine_tick_chicago || null}
        lastSuccessfulPollTimestamp={lastSuccessfulPollTimestamp}
        barsExpectedCount={status?.bars_expected_count}
        worstLastBarAgeSeconds={status?.worst_last_bar_age_seconds}
        overallExecution={overallExecution}
        executionHashDetail={{
          robot: status?.robot_timetable_hash ?? null,
          publisher:
            status?.timetable_publisher_hash ?? status?.current_timetable_hash ?? null,
          content: status?.timetable_content_hash ?? null,
        }}
      />
      
      <WatchdogNavigationBar />
      
      <CriticalAlertBanner alerts={alerts} />
      
      {/* Error Display */}
      {hasBlockingErrors && (
        <div className="watchdog-content mt-32 px-1 py-4">
          <div className="watchdog-card rounded-2xl border-red-700/70 bg-red-950/55 p-4">
            <h2 className="text-lg font-semibold mb-2">Watchdog Data Unavailable</h2>
            <div className="space-y-1 text-sm">
              {blockingApiErrors.map(item => (
                <div key={item.label}>{item.label}: {item.message}</div>
              ))}
            </div>
            <div className="mt-2 text-xs text-gray-400">
              Make sure the watchdog backend is running on http://localhost:8002
            </div>
          </div>
        </div>
      )}
      
      {/* Loading State */}
      {isLoading && !hasBlockingErrors && (
        <div className="watchdog-content mt-32 px-1 py-8">
          <div className="watchdog-card rounded-2xl p-8 text-center text-gray-400">
            <div className="text-lg mb-2">Loading watchdog data...</div>
            <div className="text-sm">Connecting to backend...</div>
          </div>
        </div>
      )}
      
      <div className="watchdog-content space-y-5 px-1 pb-10 pt-32">
        <OperatorSafetyBar
          status={status}
          summary={runSummary}
          overallExecution={overallExecution}
          streams={streams}
          activeIntents={activeIntents}
          unprotectedPositions={unprotectedPositions}
          alerts={alerts}
          dataFlowStatus={dataFlowStatus}
          viewMode={fullRunMode ? 'run' : 'live'}
        />
        <ActiveRiskPanel
          status={status}
          streams={streams}
          activeIntents={activeIntents}
          unprotectedPositions={unprotectedPositions}
          carriedActiveLifecycles={carriedActiveLifecycles}
          outOfTimetableActiveStreams={outOfTimetableActiveStreams}
          overallExecution={overallExecution}
        />
        <div className="grid min-w-0 grid-cols-1 gap-5 xl:grid-cols-12">
          <div className="min-w-0 space-y-5 xl:col-span-8 2xl:col-span-9">
            <RunVerdictStrip
              summary={runSummary}
              clientError={runSummaryError}
              peekActive={Boolean(peekRunRoot)}
              onClearPeek={peekRunRoot ? clearPeek : undefined}
              autoFollowPlayback={autoFollowPlayback}
              activePlaybackRunId={activePlaybackRunId}
              activePlaybackRunRoot={activePlaybackRunRoot}
              onEnableAutoFollow={activePlaybackRunRoot ? enableAutoFollowPlayback : undefined}
            />
            <HardGatesStrip status={status} slotLifecycle={slotLifecycle} />
            <QuantHealthStrip
              recent={recentRunsPayload}
              error={recentRunsError}
              selectedRunRoot={peekRunRoot}
              onSelectRunRoot={handleSelectRunRoot}
            />
            <div id="stream-table">
              <StreamStatusTable
                streams={streams}
                onStreamClick={setSelectedStream}
                referenceTimeUtc={streamStateReferenceUtc ?? status?.snapshot_utc ?? null}
                marketOpen={status?.market_open ?? null}
                carriedActiveLifecycles={carriedActiveLifecycles}
                outOfTimetableActiveStreams={outOfTimetableActiveStreams}
                executionExpectationGaps={executionExpectationGaps}
                flattenLookupMetrics={flattenLookupMetrics ?? undefined}
                activeIntents={activeIntents}
                unprotectedPositions={unprotectedPositions}
                runRoot={peekRunRoot}
              />
            </div>
            <LiveEventFeed
              events={events}
              onEventClick={setSelectedEvent}
            />
            <RunTimelinePanel
              events={keyEventsPayload?.events ?? []}
              persistenceRoot={keyEventsPayload?.persistence_root}
              error={keyEventsError}
              loading={keyEventsPayload === null && !keyEventsError}
              artifactReason={
                keyEventsPayload && keyEventsPayload.available === false
                  ? keyEventsPayload.reason ?? 'UNAVAILABLE'
                  : null
              }
            />
          </div>
          
          <div className="min-w-0 space-y-4 xl:col-span-4 2xl:col-span-3">
            <SystemAuthorityStatusBar
              byInstrument={status?.position_authority_by_instrument}
              overallExecution={overallExecution}
              reconciliationGateState={status?.reconciliation_gate_state}
            />
            {auxiliaryApiErrors.length > 0 && (
              <div className="watchdog-card rounded-2xl border-yellow-700/50 bg-yellow-950/20 p-4">
                <div className="mb-2 text-[11px] font-semibold uppercase tracking-[0.14em] text-yellow-400">
                  Data Refresh Warnings
                </div>
                <div className="space-y-1 text-xs text-yellow-100/90">
                  {auxiliaryApiErrors.map(item => (
                    <div key={item.label}>{item.label}: {item.message}</div>
                  ))}
                </div>
                <div className="mt-2 text-[11px] text-yellow-100/60">
                  Main watchdog status is still available; affected panels may be showing last successful data.
                </div>
              </div>
            )}
            <ReliabilityPanel metrics={metrics} loading={metricsLoading} />
            <InstrumentHealthPanel instruments={instrumentHealthDisplay} loading={fullRunMode ? liveSnapshotLoading : instrumentHealthLoading} />
            <ExecutionIntegrityPanel counts={status?.execution_integrity ?? null} />
            <FillHealthCard fillHealth={status?.fill_health} />
            <div id="risk-gates-panel">
              <RiskGatesPanel
              gates={gates}
              loading={!gates}
              overallExecution={overallExecution}
              tradable={status?.execution_safe}
            />
            </div>
            
            <ActiveIntentPanel
              intents={activeIntents}
              unprotectedPositions={unprotectedPositions}
            />
          </div>
        </div>
        <details className="rounded-lg border border-slate-800/80 bg-slate-950/35 p-3">
          <summary className="flex cursor-pointer list-none items-center justify-between gap-3">
            <span>
              <span className="block text-sm font-semibold text-slate-200">Secondary diagnostics</span>
              <span className="block text-xs text-slate-500">
                Historical reliability, connectivity history, debug identity, and lower-priority diagnostics.
              </span>
            </span>
            <span className="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-slate-300">
              collapsed by default
            </span>
          </summary>
          <div className="mt-4 grid min-w-0 grid-cols-1 gap-4 md:grid-cols-2 2xl:grid-cols-4">
            <div className="watchdog-card rounded-lg p-4">
              <div className="mb-1 text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-500">P&L Snapshot</div>
              <div className="mb-2 text-sm text-gray-400">Total Realized P&L</div>
              <div className={`text-2xl font-semibold tracking-tight ${totalPnl >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                ${totalPnl.toFixed(2)}
              </div>
              <div className="mt-2 text-xs text-gray-500">{Object.keys(pnl).length} stream(s)</div>
            </div>

            <TimetableIdentityDebugCard status={status} />
            <ActiveAlertsCard alerts={status?.active_alerts ?? []} />
            <SessionConnectivityCard
              sessionConnectivity={status?.session_connectivity}
              dailySummary={status?.last_connectivity_daily_summary}
            />
            <DisconnectFeedCard events={events} tradingDate={status?.trading_date ?? null} />
            <SlotLifecyclePanel slots={slotLifecycle} loading={liveSnapshotLoading} />

            {fullRunMode ? (
              <div className="watchdog-card rounded-lg p-4">
                <div className="mb-2 text-sm font-semibold text-gray-200">Run-Scoped Mode</div>
                <div className="text-xs leading-relaxed text-gray-400">
                  Incident history, alert history, reliability trends, and process-wide watchdog history are hidden while viewing a specific run.
                </div>
              </div>
            ) : (
              <>
                <AlertsHistoryCard recent={recentAlerts} loading={alertsHistoryLoading} />
                <ActiveIncidentsPanel active={activeIncidents} loading={activeIncidentsLoading} />
                <IncidentTimeline incidents={incidents} loading={incidentsLoading} />
                <MetricsHistoryPanel byPeriod={metricsHistory} loading={metricsHistoryLoading} granularity="week" />
              </>
            )}
          </div>
        </details>
      </div>
      
      <StreamDetailDrawer
        stream={selectedStream}
        events={events}
        isOpen={selectedStream !== null}
        onClose={() => setSelectedStream(null)}
      />
    </div>
  )
}
