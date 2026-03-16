/**
 * WatchdogPage - Primary Live Watchdog page
 */
import { useState, useEffect, useMemo } from 'react'
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
import { useWatchdogStatus } from './hooks/useWatchdogStatus'
import { useWatchdogEvents } from './hooks/useWatchdogEvents'
import { useRiskGates } from './hooks/useRiskGates'
import { useUnprotectedPositions } from './hooks/useUnprotectedPositions'
import { useStreamStates } from './hooks/useStreamStates'
import { useActiveIntents } from './hooks/useActiveIntents'
import { useWatchdogAlerts } from './hooks/useWatchdogAlerts'
import { useIncidents } from './hooks/useIncidents'
import { useActiveIncidents } from './hooks/useActiveIncidents'
import { useReliabilityMetrics } from './hooks/useReliabilityMetrics'
import { useInstrumentHealth } from './hooks/useInstrumentHealth'
import { useMetricsHistory } from './hooks/useMetricsHistory'
import { useSlotLifecycle } from './hooks/useSlotLifecycle'
import { useStreamPnl } from './hooks/useStreamPnl'
import { WatchdogNavigationBar } from './components/WatchdogNavigationBar'
import { ChicagoClock } from './components/watchdog/ChicagoClock'
import type { StreamState, WatchdogEvent } from './types/watchdog'

export function WatchdogPage() {
  const [selectedStream, setSelectedStream] = useState<StreamState | null>(null)
  const [selectedEvent, setSelectedEvent] = useState<WatchdogEvent | null>(null)
  
  // Fetch data
  const { status, loading: statusLoading, error: statusError, lastSuccessfulPollTimestamp: statusPollTime } = useWatchdogStatus()
  const { events, cursor, loading: eventsLoading, error: eventsError, lastSuccessfulPollTimestamp: eventsPollTime } = useWatchdogEvents()
  const { gates, loading: gatesLoading, error: gatesError, lastSuccessfulPollTimestamp: gatesPollTime } = useRiskGates()
  const { positions: unprotectedPositions, loading: positionsLoading, error: positionsError, lastSuccessfulPollTimestamp: positionsPollTime } = useUnprotectedPositions()
  const { streams, timetableUnavailable, loading: streamsLoading, error: streamsError, lastSuccessfulPollTimestamp: streamsPollTime } = useStreamStates()
  const { intents: activeIntents, loading: intentsLoading, error: intentsError, lastSuccessfulPollTimestamp: intentsPollTime } = useActiveIntents()
  const { recentAlerts, loading: alertsHistoryLoading } = useWatchdogAlerts(24, 30)
  const { incidents, loading: incidentsLoading } = useIncidents(50)
  const { active: activeIncidents, loading: activeIncidentsLoading } = useActiveIncidents()
  const { metrics, loading: metricsLoading } = useReliabilityMetrics(24)
  const { instruments: instrumentHealth, loading: instrumentHealthLoading } = useInstrumentHealth()
  const { byPeriod: metricsHistory, loading: metricsHistoryLoading } = useMetricsHistory('week', 12)
  const { slots: slotLifecycle, loading: slotLifecycleLoading } = useSlotLifecycle()
  
  // Get P&L data
  const currentTradingDate = status?.trading_date || streams[0]?.trading_date || new Date().toISOString().split('T')[0]
  const { pnl } = useStreamPnl(currentTradingDate, undefined, status?.market_open ?? null)
  
  // Calculate total P&L
  const totalPnl = useMemo(() => {
    return Object.values(pnl).reduce((sum, s) => {
      return sum + (s.realized_pnl || 0)
    }, 0)
  }, [pnl])
  
  // Check for any errors
  const hasErrors = statusError || eventsError || gatesError || positionsError || streamsError || intentsError
  // Show loading only if critical data (status) is still loading, not all data
  // This allows UI to show incrementally as data arrives
  const isLoading = statusLoading && !status // Only show loading if status hasn't loaded yet
  
  // Get most recent poll timestamp for data freshness
  const lastSuccessfulPollTimestamp = useMemo(() => {
    const timestamps = [statusPollTime, eventsPollTime, gatesPollTime, positionsPollTime, streamsPollTime, intentsPollTime].filter(
      (t): t is number => t !== null
    )
    return timestamps.length > 0 ? Math.max(...timestamps) : null
  }, [statusPollTime, eventsPollTime, gatesPollTime, positionsPollTime, streamsPollTime, intentsPollTime])
  
  // Determine engine status - prefer backend engine_activity_classification when available
  const engineStatus = useMemo(() => {
    if (!status) return 'STALLED'

    // Recovery overrides activity state
    if (status.recovery_state === 'DISCONNECT_FAIL_CLOSED') return 'FAIL_CLOSED'
    if (status.recovery_state === 'RECOVERY_RUNNING') return 'RECOVERY_IN_PROGRESS'

    // Prefer backend engine_activity_classification (RUNNING | IDLE | STALLED)
    const classification = status.engine_activity_classification
    if (classification === 'RUNNING') return 'ALIVE'
    if (classification === 'IDLE') return 'IDLE_MARKET_CLOSED'
    if (classification === 'STALLED') return 'STALLED'

    // Fallback: legacy engine_activity_state
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
    const DATA_STALL_THRESHOLD = 120 // Match backend threshold (seconds)
    
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
    
    if (timetableUnavailable) {
      result.push({
        type: 'degraded',
        message: 'Timetable unavailable (using fallback)',
        scrollTo: 'stream-table'
      })
    }
    
    return result
  }, [status, unprotectedPositions, timetableUnavailable])
  
  return (
    <div className="min-h-screen bg-black text-white">
      <WatchdogHeader
        identityInvariantsPass={status?.last_identity_invariants_pass ?? null}
        identityViolations={status?.last_identity_violations ?? []}
        runId={cursor.runId}
        engineStatus={engineStatus}
        marketOpen={status?.market_open !== undefined ? status.market_open : null}
        connectionStatus={status?.connection_status ?? null}
        dataFlowStatus={dataFlowStatus}
        chicagoTime=""
        clockSlot={<ChicagoClock />}
        lastEngineTick={status?.last_engine_tick_chicago || null}
        lastSuccessfulPollTimestamp={lastSuccessfulPollTimestamp}
        barsExpectedCount={status?.bars_expected_count}
        worstLastBarAgeSeconds={status?.worst_last_bar_age_seconds}
      />
      
      <WatchdogNavigationBar />
      
      <CriticalAlertBanner alerts={alerts} />
      
      {/* Error Display */}
      {hasErrors && (
        <div className="container mx-auto px-4 py-4 mt-16">
          <div className="bg-red-900 border border-red-700 rounded-lg p-4">
            <h2 className="text-lg font-semibold mb-2">API Errors</h2>
            <div className="space-y-1 text-sm">
              {statusError && <div>Status: {statusError}</div>}
              {eventsError && <div>Events: {eventsError}</div>}
              {gatesError && <div>Risk Gates: {gatesError}</div>}
              {positionsError && <div>Positions: {positionsError}</div>}
              {streamsError && <div>Streams: {streamsError}</div>}
              {intentsError && <div>Intents: {intentsError}</div>}
            </div>
            <div className="mt-2 text-xs text-gray-400">
              Make sure the watchdog backend is running on http://localhost:8002
            </div>
          </div>
        </div>
      )}
      
      {/* Loading State */}
      {isLoading && !hasErrors && (
        <div className="container mx-auto px-4 py-8 mt-16">
          <div className="text-center text-gray-400">
            <div className="text-lg mb-2">Loading watchdog data...</div>
            <div className="text-sm">Connecting to backend...</div>
          </div>
        </div>
      )}
      
      <div className="container mx-auto px-4 pt-4 pb-8 mt-16">
        <div className="grid grid-cols-10 gap-4">
          {/* Left Column (70%) */}
          <div className="col-span-7 space-y-4">
            <div id="stream-table">
              <StreamStatusTable
                streams={streams}
                onStreamClick={setSelectedStream}
                marketOpen={status?.market_open ?? null}
              />
            </div>
            
            <LiveEventFeed
              events={events}
              onEventClick={setSelectedEvent}
            />
          </div>
          
          {/* Right Column (30%) */}
          <div className="col-span-3 space-y-4">
            {/* P&L Summary Card */}
            <div className="bg-gray-800 rounded-lg p-4">
              <div className="text-sm text-gray-400 mb-1">Total Realized P&L</div>
              <div className={`text-2xl font-bold ${totalPnl >= 0 ? 'text-green-500' : 'text-red-500'}`}>
                ${totalPnl.toFixed(2)}
              </div>
              <div className="text-xs text-gray-500 mt-1">
                {Object.keys(pnl).length} stream(s)
              </div>
            </div>

            {/* Phase 1: Active push alerts */}
            <ActiveAlertsCard alerts={status?.active_alerts ?? []} />

            {/* Session-based disconnect metrics (real-time) */}
            <SessionConnectivityCard
              sessionConnectivity={status?.session_connectivity}
              dailySummary={status?.last_connectivity_daily_summary}
            />

            {/* Disconnect feed - all CONNECTION_* events for current session */}
            <DisconnectFeedCard
              events={events}
              tradingDate={status?.trading_date ?? null}
            />

            {/* Slot lifecycle - forced flatten, reentry, slot expiry */}
            <SlotLifecyclePanel slots={slotLifecycle} loading={slotLifecycleLoading} />

            {/* Phase 1: Alert history (24h) */}
            <AlertsHistoryCard recent={recentAlerts} loading={alertsHistoryLoading} />

            {/* Active incidents (ongoing) */}
            <ActiveIncidentsPanel active={activeIncidents} loading={activeIncidentsLoading} />

            {/* Phase 6: Incident timeline (historical) */}
            <IncidentTimeline incidents={incidents} loading={incidentsLoading} />

            {/* Phase 6: System reliability */}
            <ReliabilityPanel metrics={metrics} loading={metricsLoading} />

            {/* Phase 6: Instrument health */}
            <InstrumentHealthPanel instruments={instrumentHealth} loading={instrumentHealthLoading} />

            {/* Phase 8: Reliability trends */}
            <MetricsHistoryPanel byPeriod={metricsHistory} loading={metricsHistoryLoading} granularity="week" />

            {/* Execution Integrity - anomaly counts */}
            <ExecutionIntegrityPanel counts={status?.execution_integrity ?? null} />

            {/* Fill Health (execution logging hygiene) */}
            <FillHealthCard fillHealth={status?.fill_health} />

            <div id="risk-gates-panel">
              <RiskGatesPanel gates={gates} loading={!gates} />
            </div>
            
            <ActiveIntentPanel
              intents={activeIntents}
              unprotectedPositions={unprotectedPositions}
            />
          </div>
        </div>
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
