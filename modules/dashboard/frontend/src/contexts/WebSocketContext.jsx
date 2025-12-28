/**
 * WebSocket Context Provider - Phase-1 Always-On
 * 
 * SINGLETON OWNERSHIP:
 * - Exactly one WebSocket per page
 * - Created once at App level
 * - Lives for the lifetime of the dashboard
 * - Never recreated unless actually closed
 * - All components consume events via context
 */

import { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react'
import { formatEventTimestamp } from '../utils/timeUtils'

const WebSocketContext = createContext({
  events: [],
  isConnected: false,
  subscribe: null,  // Function to subscribe to events
})

export const useWebSocket = () => {
  const context = useContext(WebSocketContext)
  if (!context) {
    throw new Error('useWebSocket must be used within WebSocketProvider')
  }
  return context
}

export function WebSocketProvider({ children }) {
  const [events, setEvents] = useState([])
  const [isConnected, setIsConnected] = useState(false)
  const wsRef = useRef(null)
  const reconnectTimerRef = useRef(null)
  const seenEventsRef = useRef(new Set())
  const subscribersRef = useRef(new Set())  // Set of callback functions

  // Subscribe to events (components can register callbacks)
  const subscribe = useCallback((callback) => {
    subscribersRef.current.add(callback)
    // Return unsubscribe function
    return () => {
      subscribersRef.current.delete(callback)
    }
  }, [])

  // Notify all subscribers of a new event
  const notifySubscribers = useCallback((event) => {
    subscribersRef.current.forEach(callback => {
      try {
        callback(event)
      } catch (error) {
        console.error('[WebSocket] Subscriber callback error:', error)
      }
    })
  }, [])

  // Handle incoming events
  const handleEvent = useCallback((event) => {
    // Phase-1 always-on: Simple event handling - show all events, no complex filtering
    // Reduced logging for performance - only log important events
    if (!event.event || event.event === 'state_change' || event.event === 'error') {
      console.log('[Events] Received event:', event.stage, event.event, event.run_id)
    }

    // Format timestamp for display
    const formattedEvent = {
      ...event,
      formattedTimestamp: event.timestamp ? formatEventTimestamp(event.timestamp) : ''
    }

    // Handle snapshot events (replace existing events)
    if (event.type === 'snapshot' && Array.isArray(event.events)) {
      seenEventsRef.current.clear()  // Clear seen events on snapshot
      const formattedSnapshot = event.events.map(e => {
        const key = `${e.timestamp || 'null'}|${e.stage || 'null'}|${e.event || 'null'}|${e.run_id || 'null'}`
        seenEventsRef.current.add(key)
        return {
          ...e,
          formattedTimestamp: e.timestamp ? formatEventTimestamp(e.timestamp) : ''
        }
      })
      // Sort by timestamp (only once for snapshot)
      formattedSnapshot.sort((a, b) => {
        const tsA = a.timestamp ? new Date(a.timestamp).getTime() : 0
        const tsB = b.timestamp ? new Date(b.timestamp).getTime() : 0
        return tsA - tsB
      })
      // Limit snapshot to last 1000 events
      const limitedSnapshot = formattedSnapshot.slice(-1000)
      // Keep only keys for limited events
      const limitedKeys = new Set(limitedSnapshot.map(e => `${e.timestamp || 'null'}|${e.stage || 'null'}|${e.event || 'null'}|${e.run_id || 'null'}`))
      seenEventsRef.current.clear()
      limitedKeys.forEach(k => seenEventsRef.current.add(k))
      setEvents(limitedSnapshot)
      console.log(`[Events] Snapshot loaded: ${limitedSnapshot.length} events (from ${formattedSnapshot.length} total)`)
      
      // Notify subscribers of snapshot
      notifySubscribers({ type: 'snapshot', events: limitedSnapshot })
      return
    }

    // Fast duplicate check using Set (O(1))
    const eventKey = `${event.timestamp || 'null'}|${event.stage || 'null'}|${event.event || 'null'}|${event.run_id || 'null'}`
    if (seenEventsRef.current.has(eventKey)) {
      return  // Duplicate, skip
    }
    seenEventsRef.current.add(eventKey)

    // Add event to list (optimized - binary search insertion O(log n) instead of O(n log n))
    setEvents(prev => {
      // Insert event in sorted position using binary search
      const eventTime = event.timestamp ? new Date(event.timestamp).getTime() : 0
      const updated = [...prev]

      // Binary search for insertion point
      let left = 0
      let right = updated.length
      while (left < right) {
        const mid = Math.floor((left + right) / 2)
        const midTime = updated[mid].timestamp ? new Date(updated[mid].timestamp).getTime() : 0
        if (midTime < eventTime) {
          left = mid + 1
        } else {
          right = mid
        }
      }

      updated.splice(left, 0, formattedEvent)

      // Limit to last 1000 events to prevent memory issues
      if (updated.length > 1000) {
        // Remove oldest event keys from seenEvents
        const removed = updated.slice(0, updated.length - 1000)
        removed.forEach(e => {
          const key = `${e.timestamp || 'null'}|${e.stage || 'null'}|${e.event || 'null'}|${e.run_id || 'null'}`
          seenEventsRef.current.delete(key)
        })
        return updated.slice(-1000)
      }

      return updated
    })

    // Notify subscribers of new event
    notifySubscribers(formattedEvent)
  }, [notifySubscribers])

  // Connect WebSocket (singleton - created once)
  const connect = useCallback(() => {
    // Clear any pending reconnect
    if (reconnectTimerRef.current) {
      clearTimeout(reconnectTimerRef.current)
      reconnectTimerRef.current = null
    }

    // Don't connect if already connected
    if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN) {
      return
    }

    // Build WebSocket URL - always connect to all events (null runId)
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    const host = window.location.host
    const wsUrl = `${protocol}//${host}/ws/events`

    console.log(`[WS] Connecting to ${wsUrl}`)

    try {
      const ws = new WebSocket(wsUrl)
      wsRef.current = ws

      ws.onopen = () => {
        console.log('[WS] Connected')
        setIsConnected(true)
      }

      ws.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data)
          handleEvent(data)
        } catch (error) {
          console.error('[WS] Failed to parse message:', error, event.data)
        }
      }

      ws.onclose = (event) => {
        console.log(`[WS] Closed (code: ${event.code}, reason: ${event.reason || 'none'})`)
        wsRef.current = null
        setIsConnected(false)

        // Phase-1: Simple reconnect - retry every 2-5 seconds
        const reconnectDelay = 2000 + Math.random() * 3000  // 2000-5000ms
        console.log(`[WS] Scheduling reconnect in ${Math.round(reconnectDelay)}ms...`)
        reconnectTimerRef.current = setTimeout(() => {
          reconnectTimerRef.current = null
          connect()
        }, reconnectDelay)
      }

      ws.onerror = (error) => {
        console.error('[WS] Error:', error)
        // Error will be followed by onclose, so we don't change state here
      }
    } catch (error) {
      console.error('[WS] Failed to create WebSocket:', error)
      wsRef.current = null
      setIsConnected(false)

      // Retry if allowed
      const reconnectDelay = 2000 + Math.random() * 3000
      reconnectTimerRef.current = setTimeout(() => {
        reconnectTimerRef.current = null
        connect()
      }, reconnectDelay)
    }
  }, [handleEvent])

  // Initialize WebSocket on mount (singleton - created once)
  useEffect(() => {
    connect()

    return () => {
      // GUARANTEED CLEANUP: Always clean up WebSocket and subscribers on unmount
      if (reconnectTimerRef.current) {
        clearTimeout(reconnectTimerRef.current)
        reconnectTimerRef.current = null
      }

      // Close WebSocket connection
      if (wsRef.current) {
        try {
          if (wsRef.current.readyState !== WebSocket.CLOSED) {
            wsRef.current.close(1000, 'Component unmounting')
          }
        } catch (e) {
          // Ignore errors during cleanup
        }
        wsRef.current = null
      }
      
      // Clear all subscribers (prevent memory leak)
      subscribersRef.current.clear()
      
      // Clear seen events set (free memory)
      seenEventsRef.current.clear()
    }
  }, [connect])  // connect is stable, so this only runs once

  const value = {
    events,
    isConnected,
    subscribe,
  }

  return (
    <WebSocketContext.Provider value={value}>
      {children}
    </WebSocketContext.Provider>
  )
}






