/**
 * Hook for polling watchdog events with cursor management
 * Polls every 1-2 seconds
 * Maintains cursor state and deduplicates events
 */
import { useState, useEffect, useRef, useCallback, useMemo } from 'react'
import { fetchWatchdogEvents } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import { sortEventsByTimestamp, deduplicateEvents, filterRepetitiveEvents } from '../utils/eventUtils'
import type { WatchdogEvent } from '../types/watchdog'

interface Cursor {
  runId: string | null
  lastEventSeq: number
}

const MAX_EVENTS = 200

export function useWatchdogEvents() {
  const [events, setEvents] = useState<WatchdogEvent[]>([])
  const [cursor, setCursor] = useState<Cursor>({ runId: null, lastEventSeq: 0 })
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const processedEventsRef = useRef<Set<string>>(new Set())
  
  const hasLoadedRef = useRef(false)
  
  const poll = useCallback(async () => {
    // Only show loading on initial load, not on subsequent polls
    if (!hasLoadedRef.current) {
      setLoading(true)
    }
    const { data, error: apiError } = await fetchWatchdogEvents(cursor.runId, cursor.lastEventSeq)
    
    if (apiError) {
      setError(apiError)
      setLoading(false)
      return
    }
    
    if (data) {
      // Update run_id if changed
      if (data.run_id && data.run_id !== cursor.runId) {
        // New run_id detected - reset cursor and clear events
        setCursor({ runId: data.run_id, lastEventSeq: 0 })
        processedEventsRef.current.clear()
        setEvents([])
        setLoading(false)
        return
      }
      
      // Deduplicate new events
      const deduplicated = deduplicateEvents(data.events)
      
      // Filter repetitive events (gate violations, etc.) to reduce noise
      const filtered = filterRepetitiveEvents(deduplicated, 60000) // 60 second window
      
      // Filter out already processed events
      const unseenEvents = filtered.filter(event => {
        const key = `${event.run_id}:${event.event_seq}`
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
      hasLoadedRef.current = true
    }
    
    setLoading(false)
  }, [cursor.runId, cursor.lastEventSeq])
  
  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 1500) // 1.5 seconds
  
  // Initial load
  useEffect(() => {
    poll()
  }, [])
  
  // Check for ENGINE_START events to reset cursor
  useEffect(() => {
    const engineStartEvent = events.find(e => e.event_type === 'ENGINE_START')
    if (engineStartEvent && engineStartEvent.run_id !== cursor.runId) {
      // Reset cursor for new run_id
      setCursor({ runId: engineStartEvent.run_id, lastEventSeq: 0 })
      processedEventsRef.current.clear()
      setEvents([])
    }
  }, [events, cursor.runId])
  
  // Memoize sorted events - sort by timestamp (event_seq resets on watchdog restart)
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
