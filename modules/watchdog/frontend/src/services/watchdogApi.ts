/**
 * Watchdog API Service
 * 
 * All functions return { data, error } tuples, never throw.
 */
import type {
  WatchdogStatus,
  RiskGateStatus,
  UnprotectedPosition,
  ExecutionJournalEntry,
  StreamJournal,
  ExecutionSummary,
  WatchdogEvent,
  StreamState,
  IntentExposure,
  ApiResponse
} from '../types/watchdog'

const API_BASE = '/api/watchdog'
const FETCH_TIMEOUT_MS = 10000 // 10 second timeout

/**
 * Fetch with timeout
 */
async function fetchWithTimeout(url: string, options: RequestInit = {}, timeoutMs: number = FETCH_TIMEOUT_MS): Promise<Response> {
  const controller = new AbortController()
  const timeoutId = setTimeout(() => controller.abort(), timeoutMs)
  
  try {
    const response = await fetch(url, { ...options, signal: controller.signal })
    clearTimeout(timeoutId)
    return response
  } catch (error) {
    clearTimeout(timeoutId)
    if (error instanceof Error && error.name === 'AbortError') {
      throw new Error(`Request timeout after ${timeoutMs}ms`)
    }
    throw error
  }
}

/**
 * Fetch watchdog status
 */
export async function fetchWatchdogStatus(): Promise<ApiResponse<WatchdogStatus>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/status`)
    if (!response.ok) {
      // Try to get error detail from response
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) {
          errorDetail = errorData.detail
        }
      } catch {
        // Ignore JSON parse errors
      }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch watchdog events since event_seq
 */
export async function fetchWatchdogEvents(
  runId: string | null,
  sinceSeq: number
): Promise<ApiResponse<{ run_id: string | null; events: WatchdogEvent[]; next_seq: number }>> {
  try {
    if (!runId) {
      // If no runId, try to get current run_id from backend
      const response = await fetchWithTimeout(`${API_BASE}/events?since_seq=${sinceSeq}`)
      if (!response.ok) {
        let errorDetail = response.statusText
        try {
          const errorData = await response.json()
          if (errorData.detail) {
            errorDetail = errorData.detail
          }
        } catch {
          // Ignore JSON parse errors
        }
        return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
      }
      const data = await response.json()
      return { data, error: null }
    }
    
    const response = await fetchWithTimeout(`${API_BASE}/events?run_id=${encodeURIComponent(runId)}&since_seq=${sinceSeq}`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) {
          errorDetail = errorData.detail
        }
      } catch {
        // Ignore JSON parse errors
      }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch risk gate status
 */
export async function fetchRiskGates(): Promise<ApiResponse<RiskGateStatus>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/risk-gates`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) {
          errorDetail = errorData.detail
        }
      } catch {
        // Ignore JSON parse errors
      }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch unprotected positions
 */
export async function fetchUnprotectedPositions(): Promise<ApiResponse<{ timestamp_chicago: string; unprotected_positions: UnprotectedPosition[] }>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/unprotected-positions`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) {
          errorDetail = errorData.detail
        }
      } catch {
        // Ignore JSON parse errors
      }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch execution journal entries
 */
export async function fetchExecutionJournal(
  tradingDate: string,
  stream?: string,
  intentId?: string
): Promise<ApiResponse<{ entries: ExecutionJournalEntry[] }>> {
  try {
    const params = new URLSearchParams({ trading_date: tradingDate })
    if (stream) params.append('stream', stream)
    if (intentId) params.append('intent_id', intentId)
    
    const response = await fetch(`${API_BASE}/journal/execution?${params.toString()}`)
    if (!response.ok) {
      return { data: null, error: `HTTP ${response.status}: ${response.statusText}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch stream journal
 */
export async function fetchStreamJournal(tradingDate: string): Promise<ApiResponse<{ trading_date: string; streams: StreamJournal[] }>> {
  try {
    const response = await fetch(`${API_BASE}/journal/streams?trading_date=${encodeURIComponent(tradingDate)}`)
    if (!response.ok) {
      return { data: null, error: `HTTP ${response.status}: ${response.statusText}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch execution summary
 */
export async function fetchExecutionSummary(tradingDate: string): Promise<ApiResponse<ExecutionSummary>> {
  try {
    const response = await fetch(`${API_BASE}/journal/summary?trading_date=${encodeURIComponent(tradingDate)}`)
    if (!response.ok) {
      return { data: null, error: `HTTP ${response.status}: ${response.statusText}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch current stream states
 */
export async function fetchStreamStates(): Promise<ApiResponse<{ timestamp_chicago: string; streams: StreamState[] }>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/stream-states`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) {
          errorDetail = errorData.detail
        }
      } catch {
        // Ignore JSON parse errors
      }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch active intents
 */
export async function fetchActiveIntents(): Promise<ApiResponse<{ timestamp_chicago: string; intents: IntentExposure[] }>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/active-intents`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) {
          errorDetail = errorData.detail
        }
      } catch {
        // Ignore JSON parse errors
      }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Stream P&L types
 */
export interface StreamPnl {
  stream: string
  realized_pnl: number
  open_positions: number
  total_costs_realized: number
  intent_count: number
  closed_count: number
  partial_count: number
  open_count: number
  pnl_confidence: 'HIGH' | 'MEDIUM' | 'LOW'
}

export interface StreamPnlResponse {
  trading_date: string
  stream?: string
  streams?: StreamPnl[]
  realized_pnl?: number
  open_positions?: number
  total_costs_realized?: number
  intent_count?: number
  closed_count?: number
  partial_count?: number
  open_count?: number
  pnl_confidence?: 'HIGH' | 'MEDIUM' | 'LOW'
}

/**
 * Fetch stream P&L
 */
export async function fetchStreamPnl(
  tradingDate: string,
  stream?: string
): Promise<ApiResponse<StreamPnlResponse>> {
  try {
    const params = new URLSearchParams({ trading_date: tradingDate })
    if (stream) params.append('stream', stream)
    
    const response = await fetch(`${API_BASE}/stream-pnl?${params.toString()}`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) {
          errorDetail = errorData.detail
        }
      } catch {
        // Ignore JSON parse errors
      }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}
