/**
 * WebSocket manager for pipeline events
 * Handles connection lifecycle, reconnection logic, and event emission
 * Uses relative URLs so Vite proxy routes to backend on port 8000
 * 
 * STRICT INVARIANTS:
 * - One WebSocket per tab, zero orphaned connections
 * - Never create new WebSocket until previous one reaches CLOSED
 * - Reconnect flag captured at connect-time (immutable)
 * - All reconnect timers canceled on disconnect()
 */

class WebSocketManager {
  constructor() {
    this.ws = null
    this.runId = null
    this.onEventCallback = null
    this.reconnectTimeout = null
    this.isConnecting = false
    this.allowReconnect = false  // Fixed boolean flag captured at connect-time
    this.connectionCounter = 0   // Connection counter for backend correlation
  }

  /**
   * Connect to WebSocket for a given run ID
   * @param {string|null} runId - Pipeline run ID, or null for all events (scheduler events)
   * @param {Function} onEvent - Callback function for events
   * @param {boolean} allowReconnect - Whether to allow reconnection on disconnect (captured at connect-time)
   */
  connect(runId, onEvent, allowReconnect = false) {
    // Allow null runId for scheduler events (all events)
    if (runId === undefined) {
      console.warn('No run ID provided for WebSocket connection')
      return
    }

    this.onEventCallback = onEvent
    this.allowReconnect = allowReconnect  // Capture at connect-time (immutable)

    // If already connected to the same runId, skip
    if (this.ws && this.runId === runId) {
      const state = this.ws.readyState
      if (state === WebSocket.OPEN || state === WebSocket.CONNECTING) {
        console.log(`[WS] Already connected/connecting to runId ${runId}, skipping`)
        return
      }
    }

    // If there's an existing connection, wait for it to close before creating new one
    if (this.ws) {
      const state = this.ws.readyState
      if (state !== WebSocket.CLOSED) {
        console.log(`[WS] Existing connection not closed (state: ${state}), waiting for CLOSED before connecting to new runId`)
        // Disconnect explicitly and wait for CLOSED
        this._waitForClosedThenConnect(runId)
        return
      }
    }

    // Safe to connect - no existing connection or it's already CLOSED
    this._connectInternal(runId)
  }

  /**
   * Wait for existing connection to close, then connect to new runId
   * @private
   */
  _waitForClosedThenConnect(runId) {
    // Cancel any pending reconnect timers
    this._cancelReconnectTimer()

    if (!this.ws) {
      this._connectInternal(runId)
      return
    }

    const state = this.ws.readyState
    if (state === WebSocket.CLOSED) {
      this._connectInternal(runId)
      return
    }

    // Close existing connection explicitly
    if (state === WebSocket.OPEN || state === WebSocket.CONNECTING) {
      this.ws.close(1000, 'Switching to new runId')
    }

    // Wait for CLOSED state, then connect
    const checkClosed = setInterval(() => {
      if (!this.ws || this.ws.readyState === WebSocket.CLOSED) {
        clearInterval(checkClosed)
        this.ws = null
        this._connectInternal(runId)
      }
    }, 50)

    // Timeout after 5 seconds
    setTimeout(() => {
      clearInterval(checkClosed)
      if (this.ws && this.ws.readyState !== WebSocket.CLOSED) {
        console.warn('[WS] Timeout waiting for connection to close, forcing cleanup')
        this.ws = null
        this._connectInternal(runId)
      }
    }, 5000)
  }

  /**
   * Internal connection logic - NEVER calls disconnect()
   * @private
   */
  _connectInternal(runId) {
    // STRICT: Never create new WebSocket if previous one isn't CLOSED
    if (this.ws && this.ws.readyState !== WebSocket.CLOSED) {
      console.error('[WS] CRITICAL: Attempted to create new WebSocket while previous one is not CLOSED!')
      return
    }

    // Cancel any pending reconnect timers
    this._cancelReconnectTimer()

    this.runId = runId
    this.isConnecting = true
    this.connectionCounter += 1
    const connId = this.connectionCounter

    // Use relative URL so Vite proxy handles it
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    const host = window.location.host
    // If runId is null, connect to /ws/events (all events), otherwise /ws/events/{runId}
    const wsUrl = runId === null 
      ? `${protocol}//${host}/ws/events`
      : `${protocol}//${host}/ws/events/${runId}`
    
    console.log(`[WS] [${connId}] CONNECT to ${wsUrl} (runId: ${runId}, allowReconnect: ${this.allowReconnect})`)
    const ws = new WebSocket(wsUrl)

    ws.onopen = () => {
      console.log(`[WS] [${connId}] OPEN - Connected to ${wsUrl}`)
      this.isConnecting = false
      // NO ping - backend handles keepalive
    }

    ws.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data)
        if (this.onEventCallback) {
          this.onEventCallback(data)
        }
      } catch (error) {
        console.error(`[WS] [${connId}] Failed to parse message:`, error, event.data)
      }
    }

    ws.onclose = (event) => {
      console.log(`[WS] [${connId}] CLOSE - Code: ${event.code}, Reason: ${event.reason || 'none'}`)
      this.ws = null
      this.isConnecting = false

      // Only reconnect if allowed (captured at connect-time) and not normal closure
      if (this.allowReconnect && event.code !== 1000 && event.code !== 1001) {
        // Single reconnection attempt only
        if (!this.reconnectTimeout) {
          console.log(`[WS] [${connId}] Scheduling single reconnection attempt...`)
          this._scheduleSingleReconnect(runId, connId)
        }
      } else {
        console.log(`[WS] [${connId}] Not reconnecting (allowReconnect: ${this.allowReconnect}, code: ${event.code})`)
      }
    }

    ws.onerror = (error) => {
      console.error(`[WS] [${connId}] ERROR:`, error)
      this.isConnecting = false
    }

    this.ws = ws
  }

  /**
   * Schedule a single reconnection attempt (one retry only)
   * @private
   */
  _scheduleSingleReconnect(runId, connId) {
    // Cancel any existing reconnect timer
    this._cancelReconnectTimer()

    const delay = 2000  // Fixed 2 second delay
    console.log(`[WS] [${connId}] Reconnecting in ${delay}ms...`)

    this.reconnectTimeout = setTimeout(() => {
      this.reconnectTimeout = null
      
      // Only reconnect if still allowed and runId matches
      if (this.allowReconnect && this.runId === runId && (!this.ws || this.ws.readyState === WebSocket.CLOSED)) {
        this._connectInternal(runId)
      } else {
        console.log(`[WS] [${connId}] Reconnection canceled (allowReconnect: ${this.allowReconnect}, runId match: ${this.runId === runId})`)
      }
    }, delay)
  }

  /**
   * Cancel reconnect timer and ensure it cannot fire
   * @private
   */
  _cancelReconnectTimer() {
    if (this.reconnectTimeout) {
      clearTimeout(this.reconnectTimeout)
      this.reconnectTimeout = null
    }
  }

  /**
   * Disconnect from WebSocket - explicit teardown
   */
  disconnect() {
    console.log(`[WS] DISCONNECT called - tearing down connection`)
    
    // Cancel reconnect timer (prevents any reconnection after disconnect)
    this._cancelReconnectTimer()
    
    // Disable reconnection permanently
    this.allowReconnect = false

    if (this.ws) {
      const state = this.ws.readyState
      if (state === WebSocket.OPEN || state === WebSocket.CONNECTING) {
        this.ws.close(1000, 'Client disconnecting')
      }
      this.ws = null
    }
    
    this.runId = null
    this.isConnecting = false
    this.onEventCallback = null
  }

  /**
   * Check if WebSocket is currently connected
   * @returns {boolean}
   */
  isConnected() {
    return this.ws && this.ws.readyState === WebSocket.OPEN
  }

  /**
   * Get current run ID
   * @returns {string|null}
   */
  getRunId() {
    return this.runId
  }
}

// Export singleton instance
export const websocketManager = new WebSocketManager()




