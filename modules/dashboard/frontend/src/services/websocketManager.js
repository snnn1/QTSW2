/**
 * WebSocket manager for pipeline events - Phase-1 Always-On
 * 
 * SIMPLIFIED ARCHITECTURE:
 * - One connection per page load
 * - Reconnect only if socket actually closes or backend is unreachable
 * - No attemptId logic, no stale handler suppression, no multi-attempt state machine
 * - If socket is open, events flow
 * - No snapshot gating - events flow immediately
 */

class WebSocketManager {
  constructor() {
    this.ws = null
    this.runId = null
    this.onEventCallback = null
    this._reconnectTimer = null
    // ADDITION 4: Reconnect delay is now randomized 2-5 seconds in onclose handler
  }

  /**
   * Connect to WebSocket for a given run ID
   * Phase-1: Simple connection, no complex state machine
   * 
   * @param {string|null} runId - Pipeline run ID, or null for all events
   * @param {Function} onEvent - Callback function for events
   * @param {boolean} allowReconnect - Whether to allow reconnection on disconnect
   */
  connect(runId, onEvent, allowReconnect = true) {
    // Validation
    if (runId !== null && (typeof runId !== 'string' || runId.length === 0)) {
      console.warn(`[WS] Invalid runId: ${runId}`)
      return
    }

    if (onEvent !== undefined && typeof onEvent !== 'function') {
      console.warn(`[WS] Invalid onEvent callback: ${typeof onEvent}`)
        return
      }

    // If already connected to same runId, just update callback
    if (this.ws && this.ws.readyState === WebSocket.OPEN && this.runId === runId) {
      console.log(`[WS] Already connected to runId ${runId}, updating callback`)
      this.onEventCallback = onEvent
      this.allowReconnect = allowReconnect  // Update allowReconnect flag
      return
    }

    // Close existing connection if different runId (but preserve allowReconnect for new connection)
    if (this.ws && this.runId !== runId) {
      const wasReconnecting = this.allowReconnect
      this.disconnect()
      this.allowReconnect = wasReconnecting  // Restore allowReconnect for new connection
  }

    // Store configuration
    this.runId = runId
    this.onEventCallback = onEvent
    this.allowReconnect = allowReconnect

    // Start connection
    this._connect()
  }

  /**
   * Internal connection method
   * @private
   */
  _connect() {
    // Cancel any pending reconnect
    this._cancelReconnect()
    
    // Don't connect if already connected or connecting
    if (this.ws) {
      if (this.ws.readyState === WebSocket.OPEN) {
        console.log(`[WS] Already connected, skipping reconnect`)
      return
    }
      if (this.ws.readyState === WebSocket.CONNECTING) {
        console.log(`[WS] Already connecting, skipping reconnect`)
        return
    }
      // If socket is in CLOSING or CLOSED state, clear it so we can create a new one
      if (this.ws.readyState === WebSocket.CLOSING || this.ws.readyState === WebSocket.CLOSED) {
        console.log(`[WS] Clearing old socket in ${this.ws.readyState} state`)
        this.ws = null
      }
  }

    // Build WebSocket URL
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    const host = window.location.host
    const wsUrl = this.runId === null 
      ? `${protocol}//${host}/ws/events`
      : `${protocol}//${host}/ws/events/${this.runId}`

    console.log(`[WS] Connecting to ${wsUrl}`)
    
    try {
    const ws = new WebSocket(wsUrl)
      this.ws = ws

    ws.onopen = () => {
        console.log(`[WS] Connected (runId: ${this.runId || 'all'})`)
        // Log connection for debugging
        if (this.onEventCallback) {
          console.log('[WS] Event callback is set, ready to receive events')
        } else {
          console.warn('[WS] WARNING: No event callback set!')
        }
    }

    ws.onmessage = (event) => {
        // Phase-1: No snapshot gating - forward all messages immediately
      try {
        const data = JSON.parse(event.data)
        if (this.onEventCallback) {
          this.onEventCallback(data)
        }
      } catch (error) {
          console.error(`[WS] Failed to parse message:`, error, event.data)
      }
    }

    ws.onclose = (event) => {
        console.log(`[WS] Closed (code: ${event.code}, reason: ${event.reason || 'none'}, runId: ${this.runId || 'all'})`)
      this.ws = null

        // ADDITION 4: Frontend reconnect policy
        // On WebSocket close:
        // - show "Reconnecting..." (handled by UI via connection state)
        // - retry every 2-5 seconds
        // - Do NOT reset UI, clear events, disable buttons, or infer backend failure
        // Reconnect on ANY close code if allowReconnect is true (only skip if we explicitly disconnected)
        if (this.allowReconnect) {
          // Use random delay between 2-5 seconds for reconnect
          const reconnectDelay = 2000 + Math.random() * 3000  // 2000-5000ms
          console.log(`[WS] Scheduling reconnect in ${Math.round(reconnectDelay)}ms... (code: ${event.code})`)
          this._reconnectTimer = setTimeout(() => {
            this._reconnectTimer = null
            this._connect()
          }, reconnectDelay)
      } else {
          console.log(`[WS] Reconnect disabled, not reconnecting (code: ${event.code})`)
      }
    }

    ws.onerror = (error) => {
        console.error(`[WS] Error (runId: ${this.runId || 'all'}):`, error)
        // Error will be followed by onclose, so we don't change state here
      }
    } catch (error) {
      console.error(`[WS] Failed to create WebSocket:`, error)
      this.ws = null
      
      // Retry if allowed (use same randomized delay as onclose)
      if (this.allowReconnect) {
        const reconnectDelay = 2000 + Math.random() * 3000  // 2000-5000ms
        this._reconnectTimer = setTimeout(() => {
          this._reconnectTimer = null
          this._connect()
        }, reconnectDelay)
      }
    }
  }

  /**
   * Cancel reconnect timer
   * @private
   */
  _cancelReconnect() {
    if (this._reconnectTimer) {
      clearTimeout(this._reconnectTimer)
      this._reconnectTimer = null
    }
  }

  /**
   * Disconnect from WebSocket
   */
  disconnect() {
    console.log(`[WS] Disconnecting`)
    this.allowReconnect = false
    this._cancelReconnect()

    if (this.ws) {
      try {
        if (this.ws.readyState !== WebSocket.CLOSED) {
        this.ws.close(1000, 'Client disconnecting')
        }
      } catch (e) {
        // Ignore
      }
      this.ws = null
    }
    
    this.runId = null
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

  /**
   * Get current state (simplified)
   * @returns {string}
   */
  getState() {
    if (!this.ws) return 'idle'
    if (this.ws.readyState === WebSocket.CONNECTING) return 'connecting'
    if (this.ws.readyState === WebSocket.OPEN) return 'connected'
    if (this.ws.readyState === WebSocket.CLOSING) return 'closing'
    return 'closed'
  }
}

// Export singleton instance
export const websocketManager = new WebSocketManager()
