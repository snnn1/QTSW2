/**
 * WatchdogPage - Primary Live Watchdog page
 */
import { useState, useEffect, useMemo } from 'react'
import { WatchdogHeader } from './components/watchdog/WatchdogHeader'
import { CriticalAlertBanner } from './components/watchdog/CriticalAlertBanner'
import { StreamStatusTable } from './components/watchdog/StreamStatusTable'
import { RiskGatesPanel } from './components/watchdog/RiskGatesPanel'
import { ActiveIntentPanel } from './components/watchdog/ActiveIntentPanel'
import { LiveEventFeed } from './components/watchdog/LiveEventFeed'
import { StreamDetailDrawer } from './components/watchdog/StreamDetailDrawer'
import { useWatchdogStatus } from './hooks/useWatchdogStatus'
import { useWatchdogEvents } from './hooks/useWatchdogEvents'
import { useRiskGates } from './hooks/useRiskGates'
import { useUnprotectedPositions } from './hooks/useUnprotectedPositions'
import { useStreamStates } from './hooks/useStreamStates'
import { useActiveIntents } from './hooks/useActiveIntents'
import { useStreamPnl } from './hooks/useStreamPnl'
import { getCurrentChicagoTime } from './utils/timeUtils.ts'
import { WatchdogNavigationBar } from './components/WatchdogNavigationBar'
import type { StreamState, WatchdogEvent } from './types/watchdog'

export function WatchdogPage() {
  const [chicagoTime, setChicagoTime] = useState(getCurrentChicagoTime())
  const [selectedStream, setSelectedStream] = useState<StreamState | null>(null)
  const [selectedEvent, setSelectedEvent] = useState<WatchdogEvent | null>(null)
  
  // Update Chicago time every second
  useEffect(() => {
    const interval = setInterval(() => {
      setChicagoTime(getCurrentChicagoTime())
    }, 1000)
    return () => clearInterval(interval)
  }, [])
  
  // Fetch data
  const { status, loading: statusLoading, error: statusError, lastSuccessfulPollTimestamp: statusPollTime } = useWatchdogStatus()
  const { events, cursor, loading: eventsLoading, error: eventsError, lastSuccessfulPollTimestamp: eventsPollTime } = useWatchdogEvents()
  const { gates, loading: gatesLoading, error: gatesError, lastSuccessfulPollTimestamp: gatesPollTime } = useRiskGates()
  const { positions: unprotectedPositions, loading: positionsLoading, error: positionsError, lastSuccessfulPollTimestamp: positionsPollTime } = useUnprotectedPositions()
  const { streams, loading: streamsLoading, error: streamsError, lastSuccessfulPollTimestamp: streamsPollTime } = useStreamStates()
  const { intents: activeIntents, loading: intentsLoading, error: intentsError, lastSuccessfulPollTimestamp: intentsPollTime } = useActiveIntents()
  
  // Get P&L data
  const currentTradingDate = status?.trading_date || streams[0]?.trading_date || new Date().toISOString().split('T')[0]
  const { pnl } = useStreamPnl(currentTradingDate)
  
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
  
  // Determine engine status - memoized to prevent unnecessary recalculations
  const engineStatus = useMemo(() => {
    if (!status) return 'STALLED'

    // Recovery overrides activity state
    if (status.recovery_state === 'DISCONNECT_FAIL_CLOSED') return 'FAIL_CLOSED'
    if (status.recovery_state === 'RECOVERY_RUNNING') return 'RECOVERY_IN_PROGRESS'

    // PATTERN 1: Handle bar-driven states
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
        // Default to IDLE if unknown (safer than STALLED)
        return 'IDLE_MARKET_CLOSED'
    }
  }, [status?.recovery_state, status?.engine_activity_state]) // Only depend on fields that affect result
  
  // Determine data flow status - memoized with stable dependencies
  const dataFlowStatus = useMemo(() => {
    if (!status) return 'UNKNOWN'
    
    const stalls = Object.values(status.data_stall_detected || {})
    
    // If no instruments tracked yet, check if bars are being received
    // If worst_last_bar_age_seconds is set and recent, data is flowing
    if (stalls.length === 0) {
      // If market is known to be closed, show acceptable silence
      if (status.market_open === false) {
        return 'ACCEPTABLE_SILENCE'
      }
      
      // If bars are being received (worst_last_bar_age_seconds is set and recent), show FLOWING
      // This handles the case where streams are in PRE_HYDRATION but bars are arriving
      if (status.worst_last_bar_age_seconds !== null && status.worst_last_bar_age_seconds !== undefined) {
        // Bars are being received - show FLOWING if recent (< 2 minutes), otherwise UNKNOWN
        if (status.worst_last_bar_age_seconds < 120) {
          return 'FLOWING'
        }
      }
      
      // Otherwise, show unknown (waiting for first bar or streams to transition)
      return 'UNKNOWN'
    }
    
    // Check for critical stalls (market open + stalled)
    const criticalStall = stalls.some(d => d.stall_detected && d.market_open)
    if (criticalStall) {
      return 'STALLED'
    }
    
    // Check for acceptable silence (market closed + stalled)
    const acceptableSilence = stalls.some(d => d.stall_detected && !d.market_open)
    if (acceptableSilence) {
      return 'ACCEPTABLE_SILENCE'
    }
    
    // No stalls detected = data flowing
    return 'FLOWING'
  }, [status?.data_stall_detected, status?.market_open, status?.worst_last_bar_age_seconds]) // Only depend on fields that affect result
  
  // Build critical alerts
  const alerts = useMemo(() => {
    const result: Array<{ type: 'critical' | 'degraded'; message: string; scrollTo?: string }> = []
    
    if (!status) return result
    
    if (unprotectedPositions.length > 0) {
      result.push({
        type: 'critical',
        message: `UNPROTECTED POSITION: ${unprotectedPositions.length} intent(s) without protective orders`,
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
    
    return result
  }, [status, unprotectedPositions])
  
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
        chicagoTime={chicagoTime}
        lastEngineTick={status?.last_engine_tick_chicago || null}
        lastSuccessfulPollTimestamp={lastSuccessfulPollTimestamp}
        barsExpectedCount={status?.bars_expected_count}
        worstLastBarAgeSeconds={status?.worst_last_bar_age_seconds}
      />
      
      <WatchdogNavigationBar />
      
      <CriticalAlertBanner alerts={alerts} />
      
      {/* Error Display */}
      {hasErrors && (
        <div className="container mx-auto px-4 py-4 mt-28">
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
        <div className="container mx-auto px-4 py-8 mt-24">
          <div className="text-center text-gray-400">
            <div className="text-lg mb-2">Loading watchdog data...</div>
            <div className="text-sm">Connecting to backend...</div>
          </div>
        </div>
      )}
      
      <div className="container mx-auto px-4 py-8 mt-24">
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
        isOpen={selectedStream !== null}
        onClose={() => setSelectedStream(null)}
      />
    </div>
  )
}
