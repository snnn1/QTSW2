/**
 * WebSocket manager for pipeline events
 * Handles connection lifecycle, reconnection logic, and event emission
 */

const WS_BASE = 'ws://localhost:8001/ws'

class WebSocketManager {
  constructor() {
    this.ws = null
    this.runId = null
    this.onEventCallback = null
    this.reconnectTimeout = null
    this.reconnectAttempts = 0
    this.isReconnecting = false
    this.isConnecting = false
    this.shouldReconnect = false
  }

  /**
   * Connect to WebSocket for a given run ID
   * @param {string} runId - Pipeline run ID
   * @param {Function} onEvent - Callback function for events
   * @param {Function} shouldReconnectFn - Function that returns whether to reconnect
   */
  connect(runId, onEvent, shouldReconnectFn = () => false) {
    if (!runId) {
      console.warn('No run ID provided for WebSocket connection')
      return
    }

    this.onEventCallback = onEvent
    this.shouldReconnect = shouldReconnectFn

    // Prevent multiple simultaneous connection attempts
    if (this.isConnecting || this.isReconnecting) {
      console.log('Connection/reconnection already in progress, skipping...')
      return
    }

    // Check if we're already connected to this exact run_id
    if (this.ws) {
      const currentState = this.ws.readyState
      const isConnected = currentState === WebSocket.OPEN || currentState === WebSocket.CONNECTING
      const isSameRunId = this.ws.url.includes(runId)

      if (isConnected && isSameRunId) {
        console.log('Already connected/connecting to this run_id, skipping...')
        return
      }

      // If connected to a different run_id, disconnect first
      if (isConnected && !isSameRunId) {
        console.log('Disconnecting from different run_id before connecting to new one')
        this.disconnect()
        // Wait a moment for the disconnect to complete
        setTimeout(() => this._connectInternal(runId), 100)
        return
      }
    }

    this._connectInternal(runId)
  }

  /**
   * Internal connection logic
   * @private
   */
  _connectInternal(runId) {
    this.runId = runId
    this.isConnecting = true

    // Disconnect any existing connection
    this.disconnect()

    const wsUrl = `${WS_BASE}/events/${runId}`
    console.log('Creating new WebSocket connection to:', wsUrl)
    const ws = new WebSocket(wsUrl)

    ws.onopen = () => {
      console.log('WebSocket connected to:', wsUrl)
      // Reset reconnection attempts on successful connection
      this.reconnectAttempts = 0
      this.isReconnecting = false
      this.isConnecting = false
      // Send a ping to keep the connection alive
      try {
        ws.send('ping')
      } catch (e) {
        console.error('Error sending ping:', e)
      }
    }

    ws.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data)
        console.log('WebSocket message received:', data)
        if (this.onEventCallback) {
          this.onEventCallback(data)
        }
      } catch (error) {
        console.error('Failed to parse WebSocket message:', error, event.data)
      }
    }

    ws.onclose = (event) => {
      console.log('WebSocket disconnected:', event.code, event.reason)
      this.ws = null
      this.isConnecting = false

      // Only reconnect if we should
      if (!this.shouldReconnect()) {
        console.log('Should not reconnect WebSocket')
        return
      }

      // Normal closure codes - don't reconnect
      if (event.code === 1000 || event.code === 1001) {
        console.log('WebSocket closed normally, not reconnecting')
        return
      }

      // Prevent multiple simultaneous reconnection attempts
      if (this.isReconnecting) {
        console.log('Reconnection already in progress, skipping...')
        return
      }

      this._scheduleReconnect()
    }

    ws.onerror = (error) => {
      console.error('WebSocket error:', error)
      this.isConnecting = false
    }

    this.ws = ws
  }

  /**
   * Schedule a reconnection attempt with exponential backoff
   * @private
   */
  _scheduleReconnect() {
    this.isReconnecting = true
    this.reconnectAttempts += 1

    // Exponential backoff: 2s, 4s, 8s, 16s, max 30s
    const baseDelay = 2000
    const maxDelay = 30000
    const delay = Math.min(baseDelay * Math.pow(2, this.reconnectAttempts - 1), maxDelay)

    console.log(`Attempting to reconnect WebSocket (attempt ${this.reconnectAttempts}) in ${delay}ms...`)

    this.reconnectTimeout = setTimeout(() => {
      this.isReconnecting = false
      // Check if still should reconnect
      if (this.shouldReconnect() && this.runId) {
        this._connectInternal(this.runId)
      } else {
        this.reconnectAttempts = 0
      }
    }, delay)
  }

  /**
   * Disconnect from WebSocket
   */
  disconnect() {
    // Clear any pending reconnection attempts
    if (this.reconnectTimeout) {
      clearTimeout(this.reconnectTimeout)
      this.reconnectTimeout = null
    }
    this.isReconnecting = false
    this.reconnectAttempts = 0
    this.shouldReconnect = () => false

    if (this.ws) {
      // Only close if not already closing/closed
      if (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING) {
        this.ws.close(1000, 'Client disconnecting')
      }
      this.ws = null
    }
    this.runId = null
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




