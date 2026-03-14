/**
 * Hook for watchdog events with WebSocket-primary, REST-fallback.
 * When WebSocket connected: uses WS for live events, REST every 10s for catch-up.
 * When WebSocket disconnected: REST polling every 1.5s.
 */
import { useState, useEffect, useRef, useCallback, useMemo } from 'react'
import { fetchWatchdogEvents } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import { useWebSocket } from '../contexts/WebSocketContext'
import { sortEventsByTimestamp, deduplicateEvents, filterRepetitiveEvents } from '../utils/eventUtils'
import type { WatchdogEvent } from '../types/watchdog'

interface Cursor {
  runId: string | null
  lastEventSeq: number
}

const MAX_EVENTS = 200
const REST_POLL_MS_WHEN_WS_CONNECTED = 10000  // 10s catch-up when WS primary
const REST_POLL_MS_WHEN_WS_DISCONNECTED = 1500  // 1.5s when REST only

/** Convert WebSocket event format to WatchdogEvent. Phase 4: use event_id for dedupe. */
function wsEventToWatchdogEvent(ws: { seq?: number; event_id?: string; event_seq?: number; type?: string; ts_utc?: string; run_id?: string; stream_id?: string; data?: Record<string, unknown> }): WatchdogEvent {
  const tsUtc = ws.ts_utc || new Date().toISOString()
  const d = new Date(tsUtc)
  const chicago = d.toLocaleString('en-US', { timeZone: 'America/Chicago' })
  const runId = ws.run_id ?? ''
  const eventSeq = ws.event_seq ?? ws.seq ?? 0
  return {
    event_seq: eventSeq,
    event_id: ws.event_id ?? `${runId}:${eventSeq}`,
    run_id: runId,
    timestamp_utc: tsUtc,
    timestamp_chicago: chicago,
    event_type: ws.type ?? 'UNKNOWN',
    trading_date: null,
    stream: ws.stream_id ?? null,
    instrument: null,
    session: null,
    data: ws.data ?? {},
  }
}

export function useWatchdogEvents() {
  const [events, setEvents] = useState<WatchdogEvent[]>([])
  const [cursor, setCursor] = useState<Cursor>({ runId: null, lastEventSeq: 0 })
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const processedEventsRef = useRef<Set<string>>(new Set())
  const hasLoadedRef = useRef(false)
  const { isConnected: wsConnected, subscribe: wsSubscribe } = useWebSocket()

  const poll = useCallback(async () => {
    // Only show loading on initial load, not on subsequent polls
    if (!hasLoadedRef.current) {
      setLoading(true)
    }
    const { data, error: apiError } = await fetchWatchdogEvents(cursor.runId, cursor.lastEventSeq)
    
    if (apiError) {
      setError(apiError)
      hasLoadedRef.current = true // Mark as loaded even on error
      setLoading(false)
      return
    }
    
    if (data) {
      // Backend now returns mixed runs (most recent 500 by timestamp) - don't reset on run_id change
      // Just merge new events; dedupe by run_id:event_seq handles mixed runs
      if (data.run_id && data.run_id !== cursor.runId) {
        setCursor(prev => ({ ...prev, runId: data.run_id }))
      }
      
      // Deduplicate new events
      const deduplicated = deduplicateEvents(data.events)
      
      // Filter repetitive events (gate violations, etc.) to reduce noise
      const filtered = filterRepetitiveEvents(deduplicated, 60000) // 60 second window
      
      // Filter out already processed events (Phase 4: use event_id for REST/WS consistency)
      const unseenEvents = filtered.filter(event => {
        const key = event.event_id ?? `${event.run_id}:${event.event_seq}`
        if (processedEventsRef.current.has(key)) {
          return false
        }
        processedEventsRef.current.add(key)
        return true
      })
      
      if (unseenEvents.length > 0) {
        // Add new events and keep only last MAX_EVENTS
        // Use functional update to ensure we're working with latest state
        setEvents(prev => {
          // Quick check: if no previous events and we have new ones, just return sorted new events
          if (prev.length === 0) {
            const sorted = sortEventsByTimestamp(unseenEvents)
            return sorted.slice(-MAX_EVENTS)
          }
          
          // Check if we're just appending (new events have timestamps after last)
          const prevLastTs = prev.length > 0 ? (prev[prev.length - 1].timestamp_utc || prev[prev.length - 1].timestamp_chicago || '') : ''
          const newFirstTs = unseenEvents[0]?.timestamp_utc || unseenEvents[0]?.timestamp_chicago || ''
          
          // If new events come after the last event (by timestamp) and we have room, just append
          if (newFirstTs > prevLastTs && prev.length + unseenEvents.length <= MAX_EVENTS) {
            return [...prev, ...unseenEvents]
          }
          
          // Otherwise, combine and sort by timestamp
          const combined = [...prev, ...unseenEvents]
          const sorted = sortEventsByTimestamp(combined)
          return sorted.slice(-MAX_EVENTS)
        })
        
        // Update cursor
        const maxSeq = Math.max(...unseenEvents.map(e => e.event_seq))
        setCursor(prev => ({
          runId: data.run_id || prev.runId,
          lastEventSeq: Math.max(prev.lastEventSeq, maxSeq)
        }))
      } else if (data.next_seq > cursor.lastEventSeq) {
        // Update cursor even if no new events (to prevent re-fetching)
        setCursor(prev => ({
          runId: data.run_id || prev.runId,
          lastEventSeq: data.next_seq
        }))
      }
      
      setError(null)
    }
    
    // Always mark as loaded to prevent infinite loading state
    hasLoadedRef.current = true
    setLoading(false)
  }, [cursor.runId, cursor.lastEventSeq])
  
  const pollIntervalMs = wsConnected ? REST_POLL_MS_WHEN_WS_CONNECTED : REST_POLL_MS_WHEN_WS_DISCONNECTED
  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, pollIntervalMs)

  // WebSocket subscription: merge live events when WS connected
  useEffect(() => {
    if (!wsSubscribe) return
    const unsub = wsSubscribe((wsEvent: { type?: string; seq?: number; event_id?: string; event_seq?: number; ts_utc?: string; run_id?: string; stream_id?: string; data?: Record<string, unknown> }) => {
      if (wsEvent.type === 'heartbeat') return
      const key = wsEvent.event_id ?? `ws:${wsEvent.run_id}:${wsEvent.seq}`
      if (processedEventsRef.current.has(key)) return
      processedEventsRef.current.add(key)
      const ev = wsEventToWatchdogEvent(wsEvent)
      setEvents((prev) => {
        const combined = [...prev, ev]
        const sorted = sortEventsByTimestamp(combined)
        return sorted.slice(-MAX_EVENTS)
      })
    })
    return unsub
  }, [wsSubscribe])

  // Initial load
  useEffect(() => {
    poll()
  }, [])
  
  // Memoize sorted events - sort by timestamp (event_seq resets on watchdog restart,
  // so old events can have higher seq than new ones, causing stale display)
  const sortedEvents = useMemo(() => {
    if (events.length === 0) {
      return []
    }
    return sortEventsByTimestamp(events)
  }, [events])
  
  return {
    events: sortedEvents, // Memoized sorted events - stable reference prevents flickering
    cursor,
    loading,
    error,
    lastSuccessfulPollTimestamp
  }
}
