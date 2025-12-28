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

  // Generate deduplication key for an event
  const getEventKey = useCallback((e) => {
    const eventTime = e.timestamp ? new Date(e.timestamp).getTime() : 0
    const timeWindow = Math.floor(eventTime / 1000) // Round to nearest second
    return `${timeWindow}|${e.stage || 'null'}|${e.event || 'null'}|${e.run_id || 'null'}`
  }, [])

  // Handle incoming events
  const handleEvent = useCallback((event) => {
    // Format timestamp for display
    const formattedEvent = {
      ...event,
      formattedTimestamp: event.timestamp ? formatEventTimestamp(event.timestamp) : ''
    }

    // Handle streaming snapshot chunks
    if ((event.type === 'snapshot' || event.type === 'snapshot_chunk') && Array.isArray(event.events)) {
      const formattedChunk = event.events.map(e => ({
        ...e,
        formattedTimestamp: e.timestamp ? formatEventTimestamp(e.timestamp) : ''
      }))
      
      setEvents(prev => {
        // Filter duplicates using deduplication key
        const newEvents = formattedChunk.filter(e => {
          const eventKey = getEventKey(e)
          if (seenEventsRef.current.has(eventKey)) {
            return false
          }
          seenEventsRef.current.add(eventKey)
          return true
        })
        
        // Combine, sort, and limit to 100 events
        const combined = [...prev, ...newEvents]
        combined.sort((a, b) => {
          const timeA = a.timestamp ? new Date(a.timestamp).getTime() : 0
          const timeB = b.timestamp ? new Date(b.timestamp).getTime() : 0
          return timeA - timeB
        })
        
        if (combined.length > 100) {
          const removed = combined.slice(0, combined.length - 100)
          removed.forEach(rem => seenEventsRef.current.delete(getEventKey(rem)))
          return combined.slice(-100)
        }
        
        return combined
      })
      
      // Notify subscribers of snapshot chunk
      notifySubscribers({ type: 'snapshot_chunk', events: formattedChunk })
      
      // Extract run_id/state from the newest chunk for status inference
      // (lightweight, does not alter scroll)
      const recent = [...formattedChunk].reverse()
      let latestRunId = null
      let latestState = null
      for (const e of recent) {
        if (!latestRunId && e.run_id) latestRunId = e.run_id
        if (e.event === 'state_change' && e.data) {
          if (e.data.canonical_state) {
            latestState = e.data.canonical_state.state || e.data.canonical_state.canonical_state
            if (e.data.canonical_state.run_id) {
              latestRunId = e.data.canonical_state.run_id
            }
          } else if (e.data.new_state) {
            latestState = e.data.new_state
          }
        }
        if (latestRunId && latestState) break
      }
      if (latestRunId) {
        notifySubscribers({
          type: 'snapshot_chunk_status',
          run_id: latestRunId,
          state: latestState || 'idle',
        })
      }
      return
    }
    
    // Handle snapshot completion marker
    if (event.type === 'snapshot_done') {
      notifySubscribers({ type: 'snapshot_done', total_events: event.total_events, total_chunks: event.total_chunks })
      return
    }

    // Check for duplicates
    const eventKey = getEventKey(event)
    if (seenEventsRef.current.has(eventKey)) {
      return  // Duplicate, skip
    }
    seenEventsRef.current.add(eventKey)

    // Add event to list
    setEvents(prev => {
      const combined = [...prev, formattedEvent]
      
      // Sort by timestamp
      combined.sort((a, b) => {
        const timeA = a.timestamp ? new Date(a.timestamp).getTime() : 0
        const timeB = b.timestamp ? new Date(b.timestamp).getTime() : 0
        return timeA - timeB
      })

      // Limit to last 100 events
      if (combined.length > 100) {
        const removed = combined.slice(0, combined.length - 100)
        removed.forEach(e => seenEventsRef.current.delete(getEventKey(e)))
        return combined.slice(-100)
      }

      return combined
    })

    // Notify subscribers of new event
    notifySubscribers(formattedEvent)
  }, [notifySubscribers, getEventKey])

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

    // Build WebSocket URL - connect directly to backend (port 8001) instead of relying on Vite proxy
    // This fixes connection failures when proxy is unstable
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    // Use direct backend URL in development, fallback to proxy in production
    const isDev = window.location.hostname === 'localhost' && window.location.port === '5173'
    const backendHost = isDev ? 'localhost:8001' : window.location.host
    const wsUrl = `${protocol}//${backendHost}/ws/events`

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
          // Log all incoming messages for debugging (can be removed later)
          if (data.type === 'snapshot_chunk' || data.type === 'snapshot_done') {
            console.log('[WS] Raw snapshot message:', data.type, data.chunk_index !== undefined ? `chunk ${data.chunk_index}/${data.total_chunks}` : '', data.events ? `${data.events.length} events` : '')
          }
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






