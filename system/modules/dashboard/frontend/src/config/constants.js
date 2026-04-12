/**
 * Frontend configuration constants
 * Centralized constants for timeouts, limits, and intervals
 */

// API timeout constants (milliseconds)
export const API_TIMEOUT_SHORT = 2000   // 2 seconds - for fast-failing endpoints
export const API_TIMEOUT_DEFAULT = 8000  // 8 seconds - default timeout
export const API_TIMEOUT_LONG = 15000   // 15 seconds - for long-running operations

// Event limits
export const MAX_EVENTS_IN_UI = 100     // Maximum events displayed in UI

// Polling intervals (milliseconds)
export const POLL_INTERVAL_IDLE = 60000      // 60 seconds when idle and WebSocket connected
export const POLL_INTERVAL_RUNNING = 10000   // 10 seconds when running or WebSocket disconnected

// WebSocket reconnection constants
export const WS_RECONNECT_BASE_DELAY = 1000   // Base delay in milliseconds (1 second)
export const WS_RECONNECT_MAX_DELAY = 30000   // Maximum delay in milliseconds (30 seconds)
export const WS_RECONNECT_MAX_ATTEMPTS = 10   // Maximum reconnection attempts
export const WS_STABLE_CONNECTION_DURATION = 5000  // Connection must stay open 5 seconds to be considered stable

// Health check interval (milliseconds)
export const HEALTH_CHECK_INTERVAL = 10000    // 10 seconds
