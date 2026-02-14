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
 * Sort events by timestamp ASC - use for display when event_seq can wrap on watchdog restart.
 */
export function sortEventsByTimestamp(events: WatchdogEvent[]): WatchdogEvent[] {
  return [...events].sort((a, b) => {
    const tsA = a.timestamp_utc || a.timestamp_chicago || ''
    const tsB = b.timestamp_utc || b.timestamp_chicago || ''
    return tsA.localeCompare(tsB)
  })
}

/**
 * Deduplicate events by (run_id, event_seq) tuple
 */
export function deduplicateEvents(events: WatchdogEvent[]): WatchdogEvent[] {
  const seen = new Set<string>()
  return events.filter(event => {
    const key = `${event.run_id}:${event.event_seq}`
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
  
  // Events to filter/throttle (repetitive gate violations, etc.)
  const repetitiveEventTypes = [
    'EXECUTION_GATE_INVARIANT_VIOLATION',
    // Add more event types here if needed
  ]
  
  const filtered: WatchdogEvent[] = []
  const lastSeen = new Map<string, { index: number; count: number; lastTime: number }>()
  
  for (const event of events) {
    const eventType = event.event_type
    const stream = event.stream || 'ENGINE'
    const message = extractEventMessage(event)
    
    // Create a key for grouping similar events
    const groupKey = `${eventType}:${stream}:${message.substring(0, 50)}`
    
    // If this is a repetitive event type, apply throttling
    if (repetitiveEventTypes.includes(eventType)) {
      const eventTime = new Date(event.timestamp_chicago || event.timestamp_utc || Date.now()).getTime()
      const last = lastSeen.get(groupKey)
      
      if (last) {
        const timeSinceLast = eventTime - last.lastTime
        
        // If within time window, increment count and update the existing event in filtered array
        if (timeSinceLast < timeWindowMs) {
          last.count++
          last.lastTime = eventTime
          
          // Update the count on the event we already added
          if (last.index < filtered.length) {
            filtered[last.index] = {
              ...filtered[last.index],
              data: { 
                ...filtered[last.index].data, 
                _repetitive_count: last.count 
              }
            }
          }
          continue // Skip adding this duplicate event
        }
      }
      
      // First occurrence in this time window - add it and track it
      const newIndex = filtered.length
      filtered.push(event)
      lastSeen.set(groupKey, { index: newIndex, count: 1, lastTime: eventTime })
    } else {
      // Not a repetitive event, add it normally
      filtered.push(event)
    }
  }
  
  return filtered
}
