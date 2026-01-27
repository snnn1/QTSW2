import React, { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react'
import { formatEventTimestamp } from '../utils/timeUtils.ts'
import { WS_RECONNECT_BASE_DELAY, WS_RECONNECT_MAX_DELAY, WS_RECONNECT_MAX_ATTEMPTS, WS_STABLE_CONNECTION_DURATION, MAX_EVENTS_IN_UI } from '../config/constants'

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
  const seenEventsRef = useRef(new Set())  // Keep for WS live event spam prevention only
  const subscribersRef = useRef(new Set())  // Set of callback functions
  
  // Fix B.2: Guard against reentrant connect()
  const connectingRef = useRef(false)
  
  // Exponential backoff state
  const reconnectAttemptsRef = useRef(0)
  const stableConnectionTimeoutRef = useRef(null)
  
  // Tab visibility tracking - prevents reconnection attempts when tab is hidden
  const isTabVisibleRef = useRef(!document.hidden)

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
  // Prefer event_id (if present) as part of the deduplication key
  // Use timestamp-based deduplication only as a fallback when no ID exists
  // Use millisecond-level granularity for timestamp-based deduplication
  const getEventKey = useCallback((e) => {
    // Prefer event_id if present (most reliable)
    if (e.event_id) {
      return `id:${e.event_id}`
    }
    
    // Fallback to timestamp-based deduplication with millisecond-level granularity
    const eventTime = e.timestamp ? new Date(e.timestamp).getTime() : 0
    // Use millisecond-level granularity instead of second-level
    return `${eventTime}|${e.stage || 'null'}|${e.event || 'null'}|${e.run_id || 'null'}`
  }, [])

  // Handle incoming events
  const handleEvent = useCallback((event) => {
    // Ignore heartbeat messages (they're just keepalive)
    if (event.type === 'heartbeat') {
      return
    }

    // Format timestamp for display
    const formattedEvent = {
      ...event,
      formattedTimestamp: event.ts_utc ? formatEventTimestamp(event.ts_utc) : (event.timestamp ? formatEventTimestamp(event.timestamp) : '')
    }

    // Check for duplicates (prevent WS live event spam)
    const eventKey = getEventKey(formattedEvent)
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

      // Limit to max events in UI
      if (combined.length > MAX_EVENTS_IN_UI) {
        const removed = combined.slice(0, combined.length - MAX_EVENTS_IN_UI)
        removed.forEach(e => seenEventsRef.current.delete(getEventKey(e)))
        return combined.slice(-MAX_EVENTS_IN_UI)
      }

      return combined
    })

    // Notify subscribers of new event
    notifySubscribers(formattedEvent)
  }, [notifySubscribers, getEventKey])

  // Connect WebSocket (singleton - created once)
  const connect = useCallback(() => {
    // LIFECYCLE INVARIANT 1: Prevent reentrant connect()
    if (connectingRef.current) {
      console.log('[WS] Connection already in progress, skipping duplicate connect()')
      return
    }
    
    // LIFECYCLE INVARIANT 2: Never create new WebSocket if one exists and is CONNECTING or OPEN
    if (wsRef.current) {
      const state = wsRef.current.readyState
      if (state === WebSocket.CONNECTING) {
        console.log('[WS] Socket exists and is CONNECTING, skipping duplicate connect()')
        return
      }
      if (state === WebSocket.OPEN) {
        console.log('[WS] Socket exists and is OPEN, skipping duplicate connect()')
        return
      }
      // If socket exists but is CLOSING or CLOSED, clean it up first
      if (state === WebSocket.CLOSING || state === WebSocket.CLOSED) {
        console.log(`[WS] Cleaning up existing socket in ${state === WebSocket.CLOSING ? 'CLOSING' : 'CLOSED'} state`)
        try {
          wsRef.current.close()
        } catch (e) {
          // Ignore errors during cleanup
        }
        wsRef.current = null
      }
    }
    
    // Don't attempt connection if tab is hidden
    if (!isTabVisibleRef.current) {
      console.log('[WS] Tab is hidden, skipping connection attempt')
      return
    }
    
    // Clear any pending reconnect
    if (reconnectTimerRef.current) {
      clearTimeout(reconnectTimerRef.current)
      reconnectTimerRef.current = null
    }
    
    // Fix B.2: Set connecting flag
    connectingRef.current = true

    // Build WebSocket URL - connect directly to backend instead of relying on Vite proxy
    // This fixes connection failures when proxy is unstable
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    // Detect which frontend app is running and connect to corresponding backend
    // Dashboard (5173) -> Dashboard backend (8001)
    // Watchdog (5175) -> Watchdog backend (8002)
    // Matrix (5174) -> Matrix backend (8000)
    const isWatchdog = window.location.hostname === 'localhost' && window.location.port === '5175'
    const isDashboard = window.location.hostname === 'localhost' && window.location.port === '5173'
    const isDev = isWatchdog || isDashboard || (window.location.hostname === 'localhost' && window.location.port === '5174')
    
    let backendPort = window.location.port
    if (isWatchdog) {
      backendPort = '8002'  // Watchdog backend
    } else if (isDashboard) {
      backendPort = '8001'  // Dashboard backend
    } else if (isDev && window.location.port === '5174') {
      backendPort = '8000'  // Matrix backend
    }
    
    const backendHost = isDev ? `localhost:${backendPort}` : window.location.host
    
    // SUBSCRIPTION SCOPE:
    // - Dashboard app: Could filter by run_id if needed: /ws/events?run_id=<run_id>
    // - Watchdog app: Subscribe to all events: /ws/events
    // Currently both use /ws/events (all events), but intent is explicit above
    const wsUrl = `${protocol}//${backendHost}/ws/events`

    console.log(`[WS] Connecting to ${wsUrl}`)

    try {
      const ws = new WebSocket(wsUrl)
      wsRef.current = ws

      ws.onopen = () => {
        console.log('[WS] Connected')
        setIsConnected(true)
        
        // Fix B.2: Clear connecting flag on successful open
        connectingRef.current = false
        
        // Fix B.3: Clear any pending reconnect timers on successful open
        if (reconnectTimerRef.current) {
          clearTimeout(reconnectTimerRef.current)
          reconnectTimerRef.current = null
        }
        
        // Start stable connection timer - connection must remain open long enough to receive data
        // This prevents flapping connections from prematurely resetting backoff
        if (stableConnectionTimeoutRef.current) {
          clearTimeout(stableConnectionTimeoutRef.current)
        }
        stableConnectionTimeoutRef.current = setTimeout(() => {
          // Connection has been stable - reset retry counter
          console.log('[WS] Connection stable, resetting retry counter')
          reconnectAttemptsRef.current = 0
          stableConnectionTimeoutRef.current = null
        }, WS_STABLE_CONNECTION_DURATION)
      }

      ws.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data)
          // Handle live events (no snapshot handling)
          handleEvent(data)
          
          // Receiving data indicates connection is working - reset retry counter if stable timer hasn't fired yet
          // This helps reset backoff faster when connection is clearly working
          if (stableConnectionTimeoutRef.current && reconnectAttemptsRef.current > 0) {
            // Connection is receiving data, consider it stable
            clearTimeout(stableConnectionTimeoutRef.current)
            console.log('[WS] Connection receiving data, resetting retry counter')
            reconnectAttemptsRef.current = 0
            stableConnectionTimeoutRef.current = null
          }
        } catch (error) {
          console.error('[WS] Failed to parse message:', error, event.data)
        }
      }

      ws.onclose = (event) => {
        console.log(`[WS] Closed (code: ${event.code}, reason: ${event.reason || 'none'})`)
        wsRef.current = null
        setIsConnected(false)
        
        // Fix B.2: Clear connecting flag on close
        connectingRef.current = false
        
        // Clear stable connection timer if connection closed before becoming stable
        if (stableConnectionTimeoutRef.current) {
          clearTimeout(stableConnectionTimeoutRef.current)
          stableConnectionTimeoutRef.current = null
        }

        // Exponential backoff reconnection logic
        // Skip reconnection if tab is hidden
        if (!isTabVisibleRef.current) {
          console.log('[WS] Tab is hidden, skipping reconnection attempt')
          return
        }
        
        if (reconnectAttemptsRef.current >= WS_RECONNECT_MAX_ATTEMPTS) {
          console.error(`[WS] Max reconnection attempts (${WS_RECONNECT_MAX_ATTEMPTS}) reached. Stopping reconnection.`)
          return
        }
        
        // Calculate exponential backoff delay: start at base delay, double each retry, max delay
        const delayMs = Math.min(WS_RECONNECT_BASE_DELAY * Math.pow(2, reconnectAttemptsRef.current), WS_RECONNECT_MAX_DELAY)
        reconnectAttemptsRef.current += 1
        
        console.log(`[WS] Scheduling reconnect attempt ${reconnectAttemptsRef.current}/${WS_RECONNECT_MAX_ATTEMPTS} in ${Math.round(delayMs)}ms...`)
        reconnectTimerRef.current = setTimeout(() => {
          reconnectTimerRef.current = null
          // Check visibility again before attempting connection
          if (isTabVisibleRef.current) {
            connect()
          } else {
            console.log('[WS] Tab still hidden, skipping scheduled reconnect')
          }
        }, delayMs)
      }

      ws.onerror = (error) => {
        console.error('[WS] Error:', error)
        // Error will be followed by onclose, so we don't change state here
        // Fix B.2: Clear connecting flag on error (onclose will also clear it, but be safe)
        connectingRef.current = false
      }
    } catch (error) {
      console.error('[WS] Failed to create WebSocket:', error)
      wsRef.current = null
      setIsConnected(false)
      // Fix B.2: Clear connecting flag on error
      connectingRef.current = false

      // Exponential backoff retry logic (same as onclose)
      // Skip reconnection if tab is hidden
      if (!isTabVisibleRef.current) {
        console.log('[WS] Tab is hidden, skipping reconnection attempt after error')
        return
      }
      
      if (reconnectAttemptsRef.current >= WS_RECONNECT_MAX_ATTEMPTS) {
        console.error(`[WS] Max reconnection attempts (${WS_RECONNECT_MAX_ATTEMPTS}) reached. Stopping reconnection.`)
        return
      }
      
      const delayMs = Math.min(WS_RECONNECT_BASE_DELAY * Math.pow(2, reconnectAttemptsRef.current), WS_RECONNECT_MAX_DELAY)
      reconnectAttemptsRef.current += 1
      
      console.log(`[WS] Scheduling reconnect attempt ${reconnectAttemptsRef.current}/${WS_RECONNECT_MAX_ATTEMPTS} in ${Math.round(delayMs)}ms...`)
      reconnectTimerRef.current = setTimeout(() => {
        reconnectTimerRef.current = null
        // Check visibility again before attempting connection
        if (isTabVisibleRef.current) {
          connect()
        } else {
          console.log('[WS] Tab still hidden, skipping scheduled reconnect')
        }
      }, delayMs)
    }
  }, [handleEvent])

  // Handle tab visibility changes
  useEffect(() => {
    const handleVisibilityChange = () => {
      const isVisible = !document.hidden
      isTabVisibleRef.current = isVisible
      
      if (isVisible) {
        console.log('[WS] Tab became visible, checking connection status')
        // Fix B.3: Only reconnect if no socket exists OR readyState is CLOSED
        // Also check connectingRef to prevent duplicate connects
        if (!connectingRef.current && (!wsRef.current || wsRef.current.readyState === WebSocket.CLOSED)) {
          console.log('[WS] Connection not open, attempting to reconnect')
          // Reset reconnection attempts when tab becomes visible
          reconnectAttemptsRef.current = 0
          connect()
        } else {
          const state = wsRef.current ? wsRef.current.readyState : 'no socket'
          console.log(`[WS] Connection already ${connectingRef.current ? 'connecting' : `open (state: ${state})`}`)
        }
      } else {
        console.log('[WS] Tab became hidden, pausing reconnection attempts')
        // Fix B.3: Clear any pending reconnection timers when tab becomes hidden
        if (reconnectTimerRef.current) {
          clearTimeout(reconnectTimerRef.current)
          reconnectTimerRef.current = null
        }
        // Fix B.2: Clear connecting flag when tab hidden
        connectingRef.current = false
        // Don't close the connection - let it stay open in case tab becomes visible again quickly
      }
    }
    
    // Listen for visibility changes
    document.addEventListener('visibilitychange', handleVisibilityChange)
    
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange)
    }
  }, [connect])

  // Handle page unload - ensure clean WebSocket closure
  useEffect(() => {
    const handleBeforeUnload = () => {
      console.log('[WS] Page unloading, closing WebSocket connection')
      // Clear reconnection timers
      if (reconnectTimerRef.current) {
        clearTimeout(reconnectTimerRef.current)
        reconnectTimerRef.current = null
      }
      
      if (stableConnectionTimeoutRef.current) {
        clearTimeout(stableConnectionTimeoutRef.current)
        stableConnectionTimeoutRef.current = null
      }
      
      // Close WebSocket connection cleanly
      if (wsRef.current) {
        try {
          if (wsRef.current.readyState !== WebSocket.CLOSED) {
            wsRef.current.close(1000, 'Page unloading')
          }
        } catch (e) {
          // Ignore errors during unload
        }
        wsRef.current = null
      }
    }
    
    window.addEventListener('beforeunload', handleBeforeUnload)
    
    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload)
    }
  }, [])

  // Initialize WebSocket on mount (singleton - created once)
  useEffect(() => {
    // Fix B.1: Handle React StrictMode double-mount
    // In StrictMode, effects run twice, so we need to guard against duplicate connections
    if (wsRef.current && (wsRef.current.readyState === WebSocket.CONNECTING || wsRef.current.readyState === WebSocket.OPEN)) {
      console.log('[WS] StrictMode double-mount detected, skipping duplicate connect()')
      return
    }
    
    if (connectingRef.current) {
      console.log('[WS] Already connecting, skipping duplicate connect()')
      return
    }
    
    connect()

    return () => {
      // LIFECYCLE INVARIANT 3: Cleanup always clears timers, closes socket, nulls refs
      
      // Clear all reconnect timers
      if (reconnectTimerRef.current) {
        clearTimeout(reconnectTimerRef.current)
        reconnectTimerRef.current = null
      }
      if (stableConnectionTimeoutRef.current) {
        clearTimeout(stableConnectionTimeoutRef.current)
        stableConnectionTimeoutRef.current = null
      }
      
      // Clear connecting flag
      connectingRef.current = false

      // Close WebSocket connection if it exists and is not already closed
      if (wsRef.current) {
        const state = wsRef.current.readyState
        if (state === WebSocket.CONNECTING || state === WebSocket.OPEN) {
          try {
            wsRef.current.close(1000, 'Component unmounting')
          } catch (e) {
            // Ignore errors during cleanup
          }
        }
        // Always null the ref after cleanup attempt
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






