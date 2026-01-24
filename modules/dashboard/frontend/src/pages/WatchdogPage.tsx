/**
 * WatchdogPage - Primary Live Watchdog page
 */
import { useState, useEffect, useMemo } from 'react'
import { WatchdogHeader } from '../components/watchdog/WatchdogHeader'
import { CriticalAlertBanner } from '../components/watchdog/CriticalAlertBanner'
import { StreamStatusTable } from '../components/watchdog/StreamStatusTable'
import { RiskGatesPanel } from '../components/watchdog/RiskGatesPanel'
import { ActiveIntentPanel } from '../components/watchdog/ActiveIntentPanel'
import { LiveEventFeed } from '../components/watchdog/LiveEventFeed'
import { StreamDetailDrawer } from '../components/watchdog/StreamDetailDrawer'
import { useWatchdogStatus } from '../hooks/useWatchdogStatus'
import { useWatchdogEvents } from '../hooks/useWatchdogEvents'
import { useRiskGates } from '../hooks/useRiskGates'
import { useUnprotectedPositions } from '../hooks/useUnprotectedPositions'
import { useStreamStates } from '../hooks/useStreamStates'
import { useActiveIntents } from '../hooks/useActiveIntents'
import { getCurrentChicagoTime } from '../utils/timeUtils'
import type { StreamState, WatchdogEvent } from '../types/watchdog'

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
  
  // Check for any errors
  const hasErrors = statusError || eventsError || gatesError || positionsError || streamsError || intentsError
  const isLoading = statusLoading || eventsLoading || gatesLoading || positionsLoading || streamsLoading || intentsLoading
  
  // Get most recent poll timestamp for data freshness
  const lastSuccessfulPollTimestamp = useMemo(() => {
    const timestamps = [statusPollTime, eventsPollTime, gatesPollTime, positionsPollTime, streamsPollTime, intentsPollTime].filter(
      (t): t is number => t !== null
    )
    return timestamps.length > 0 ? Math.max(...timestamps) : null
  }, [statusPollTime, eventsPollTime, gatesPollTime, positionsPollTime, streamsPollTime, intentsPollTime])
  
  // Determine engine status
  const engineStatus = useMemo(() => {
    if (!status) return 'STALLED'
    if (!status.engine_alive) return 'STALLED'
    if (status.recovery_state === 'DISCONNECT_FAIL_CLOSED') return 'FAIL_CLOSED'
    if (status.recovery_state === 'RECOVERY_RUNNING') return 'RECOVERY_IN_PROGRESS'
    return 'ALIVE'
  }, [status])
  
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
    
    if (status.engine_tick_stall_detected) {
      result.push({
        type: 'critical',
        message: 'ENGINE TICK STALL DETECTED',
        scrollTo: 'watchdog-header'
      })
    }
    
    if (Object.values(status.data_stall_detected).some(d => d.stall_detected)) {
      result.push({
        type: 'critical',
        message: 'DATA STALL DETECTED',
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
        runId={cursor.runId}
        engineStatus={engineStatus}
        chicagoTime={chicagoTime}
        lastEngineTick={status?.last_engine_tick_chicago || null}
        lastSuccessfulPollTimestamp={lastSuccessfulPollTimestamp}
      />
      
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
              Make sure the backend is running on http://localhost:8001
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
      
      <div className="container mx-auto px-4 py-8 mt-16">
        <div className="grid grid-cols-10 gap-4">
          {/* Left Column (70%) */}
          <div className="col-span-7 space-y-4">
            <div id="stream-table">
              <StreamStatusTable
                streams={streams}
                onStreamClick={setSelectedStream}
              />
            </div>
            
            <LiveEventFeed
              events={events}
              onEventClick={setSelectedEvent}
            />
          </div>
          
          {/* Right Column (30%) */}
          <div className="col-span-3 space-y-4">
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
