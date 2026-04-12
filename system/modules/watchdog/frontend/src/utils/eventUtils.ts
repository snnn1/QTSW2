/**
 * Event utility functions for Watchdog UI
 */
import type { WatchdogEvent } from '../types/watchdog'

/**
 * Get event level from event type
 */
export function getEventLevel(eventType: string): 'INFO' | 'WARN' | 'ERROR' {
  const upper = eventType.toUpperCase()
  if (upper.includes('ERROR') || upper.includes('FAIL') || upper.includes('STALL') || upper.includes('CRITICAL')) {
    return 'ERROR'
  }
  if (upper.includes('WARN') || upper.includes('BLOCKED') || upper.includes('REJECTED')) {
    return 'WARN'
  }
  return 'INFO'
}

/**
 * Extract short message from event data
 */
export function extractEventMessage(event: WatchdogEvent): string {
  const data = event.data || {}
  
  // Try common message fields
  if (typeof data.message === 'string') {
    return data.message
  }
  if (typeof data.msg === 'string') {
    return data.msg
  }
  if (typeof data.reason === 'string') {
    return data.reason
  }
  
  // Fallback to event type
  return event.event_type.replace(/_/g, ' ').toLowerCase()
}

/**
 * Sort events by event_seq ASC (never trust arrival order)
 */
export function sortEventsBySeq(events: WatchdogEvent[]): WatchdogEvent[] {
  return [...events].sort((a, b) => a.event_seq - b.event_seq)
}

/**
 * Get sort timestamp for an event. Collapsed events use last_timestamp so they appear
 * in correct chronological order (by when the group ended, not when it started).
 */
function getSortTimestamp(event: WatchdogEvent): string {
  const data = event.data as { _repetitive_last_timestamp?: string } | undefined
  const lastTs = data?._repetitive_last_timestamp
  if (lastTs && typeof lastTs === 'string') {
    return lastTs
  }
  return event.timestamp_utc || event.timestamp_chicago || ''
}

/**
 * Sort events by timestamp ASC - use for display when event_seq can wrap on watchdog restart.
 * event_seq resets when watchdog restarts, so old events can have higher seq than new ones.
 * Collapsed events are sorted by last_timestamp so they appear in correct chronological order.
 */
export function sortEventsByTimestamp(events: WatchdogEvent[]): WatchdogEvent[] {
  return [...events].sort((a, b) => {
    const tsA = getSortTimestamp(a)
    const tsB = getSortTimestamp(b)
    return tsA.localeCompare(tsB)
  })
}

/**
 * Deduplicate events by event_id (Phase 4: run_id:event_seq for REST/WS consistency)
 */
export function deduplicateEvents(events: WatchdogEvent[]): WatchdogEvent[] {
  const seen = new Set<string>()
  return events.filter(event => {
    const key = event.event_id ?? `${event.run_id}:${event.event_seq}`
    if (seen.has(key)) {
      return false
    }
    seen.add(key)
    return true
  })
}

/**
 * Filter/throttle repetitive events to reduce noise
 * Groups consecutive identical events within a time window
 * Shows only the first occurrence per time window, with a count badge
 */
export function filterRepetitiveEvents(events: WatchdogEvent[], timeWindowMs: number = 60000): WatchdogEvent[] {
  if (events.length === 0) return events
  
  // Events to filter/throttle (repetitive gate violations, bar events, etc.)
  const repetitiveEventTypes = [
    'EXECUTION_GATE_INVARIANT_VIOLATION',
    'IDENTITY_INVARIANTS_STATUS',  // Every 60s when no violations - throttle
    'TIMETABLE_VALIDATED',        // Periodic validation - throttle
    'CONNECTION_CONFIRMED',       // Multiple per connection - collapse to one
    'ENGINE_START',               // Multiple on startup - collapse
    'RECONCILIATION_PASS_SUMMARY', // Per-stream, often rapid - collapse
    'DATA_LOSS_DETECTED',        // Can fire multiple times - collapse
  ]
  
  const filtered: WatchdogEvent[] = []
  const lastSeen = new Map<string, { index: number; count: number; firstTime: number; lastTime: number }>()
  
  for (const event of events) {
    const eventType = event.event_type
    // Normalize stream: ENGINE, __engine__, null -> same group
    const streamRaw = event.stream || 'ENGINE'
    const streamNorm = streamRaw.toLowerCase().replace(/^_+|_+$/g, '') || 'engine'
    const message = extractEventMessage(event)
    
    // Create a key for grouping similar events
    const groupKey = `${eventType}:${streamNorm}:${message.substring(0, 50)}`
    
    // If this is a repetitive event type, apply throttling
    if (repetitiveEventTypes.includes(eventType)) {
      const eventTime = new Date(event.timestamp_chicago || event.timestamp_utc || Date.now()).getTime()
      const eventTsStr = event.timestamp_chicago || event.timestamp_utc || new Date(eventTime).toISOString()
      const last = lastSeen.get(groupKey)
      
      if (last) {
        const timeSinceLast = eventTime - last.lastTime
        
        // If within time window, increment count and update the existing event in filtered array
        if (timeSinceLast < timeWindowMs) {
          last.count++
          last.firstTime = Math.min(last.firstTime, eventTime)
          last.lastTime = Math.max(last.lastTime, eventTime)
          
          // Update the count and timestamps on the event we already added
          if (last.index < filtered.length) {
            const existing = filtered[last.index]
            const existingData = existing.data && typeof existing.data === 'object' ? existing.data : {}
            filtered[last.index] = {
              ...existing,
              data: { 
                ...existingData, 
                _repetitive_count: last.count,
                _repetitive_first_timestamp: existingData._repetitive_first_timestamp ?? new Date(last.firstTime).toISOString(),
                _repetitive_last_timestamp: new Date(last.lastTime).toISOString(),
              }
            }
          }
          continue // Skip adding this duplicate event
        }
      }
      
      // First occurrence in this time window - add it and track it
      const newIndex = filtered.length
      const eventData = event.data && typeof event.data === 'object' ? event.data : {}
      const eventWithMeta = {
        ...event,
        data: {
          ...eventData,
          _repetitive_count: 1,
          _repetitive_first_timestamp: eventTsStr,
          _repetitive_last_timestamp: eventTsStr,
        }
      }
      filtered.push(eventWithMeta)
      lastSeen.set(groupKey, { index: newIndex, count: 1, firstTime: eventTime, lastTime: eventTime })
    } else {
      // Not a repetitive event, add it normally
      filtered.push(event)
    }
  }
  
  return filtered
}
