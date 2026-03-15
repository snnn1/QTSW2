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
  DailyJournal,
  WatchdogEvent,
  StreamState,
  IntentExposure,
  ApiResponse
} from '../types/watchdog'

const API_BASE = '/api/watchdog'
const FETCH_TIMEOUT_MS = 30000 // 30 seconds - backend aggregator can be slow to start

/**
 * Fetch with timeout
 */
async function fetchWithTimeout(url: string, options: RequestInit = {}, timeoutMs: number = FETCH_TIMEOUT_MS): Promise<Response> {
  const controller = new AbortController()
  const timeoutId = setTimeout(() => controller.abort(), timeoutMs)
  
  try {
    const response = await fetch(url, {
      ...options,
      signal: controller.signal,
      cache: 'no-store', // Prevent stale Live Events from cache
    })
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
 * Fetch unified daily journal (streams, trades, total PnL, summary)
 */
export async function fetchDailyJournal(tradingDate: string): Promise<ApiResponse<DailyJournal>> {
  try {
    const response = await fetch(`${API_BASE}/journal/daily?trading_date=${encodeURIComponent(tradingDate)}`)
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
export async function fetchStreamStates(): Promise<ApiResponse<{ timestamp_chicago: string; streams: StreamState[]; timetable_unavailable?: boolean }>> {
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
  exit_type?: string | null
  entry_price?: number | null
  exit_price?: number | null
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
 * Alert record from Phase 1 ledger
 */
export interface AlertRecord {
  alert_id?: string
  alert_type?: string
  severity?: string
  first_seen_utc?: string
  last_seen_utc?: string
  dedupe_key?: string
  context?: Record<string, unknown>
  event?: string
  resolved_utc?: string
}

export interface AlertsResponse {
  active_alerts: AlertRecord[]
  recent: AlertRecord[]
}

/**
 * Fetch alerts (active + recent history)
 */
export async function fetchWatchdogAlerts(
  activeOnly = false,
  sinceHours?: number,
  limit = 50
): Promise<ApiResponse<AlertsResponse>> {
  try {
    const params = new URLSearchParams()
    params.append('active_only', String(activeOnly))
    if (sinceHours != null) params.append('since_hours', String(sinceHours))
    params.append('limit', String(limit))
    const response = await fetchWithTimeout(`${API_BASE}/alerts?${params.toString()}`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) errorDetail = errorData.detail
      } catch {
        /* ignore */
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
 * Incident record from incidents.jsonl (Phase 6)
 */
export interface IncidentRecord {
  incident_id?: string
  type?: string
  start_ts?: string
  end_ts?: string
  duration_sec?: number
  instruments?: string[]
}

export interface IncidentsResponse {
  incidents: IncidentRecord[]
  count: number
}

/**
 * Reliability metrics (Phase 6)
 */
export interface ReliabilityMetrics {
  connection: {
    disconnect_incidents: number
    avg_disconnect_duration: number
    max_disconnect_duration: number
    uptime_percent: number
  }
  engine: {
    engine_stalls: number
    avg_stall_duration: number
    max_stall_duration: number
  }
  data: {
    data_stalls: number
    avg_data_stall_duration: number
  }
  forced_flatten: { forced_flatten_count: number }
  reconciliation: { reconciliation_mismatch_count: number }
  window_hours: number
  window_start?: string
  window_end?: string
}

export interface InstrumentHealth {
  instrument: string
  status: 'OK' | 'DATA_STALLED'
  last_bar_chicago?: string | null
  elapsed_seconds?: number | null
}

export interface InstrumentHealthResponse {
  instruments: InstrumentHealth[]
  count: number
}

/**
 * Active incident (ongoing, not yet resolved)
 */
export interface ActiveIncident {
  type: string
  incident_id?: string
  started: string
  started_iso?: string
  duration_sec: number
  instruments: string[]
}

export interface ActiveIncidentsResponse {
  active: ActiveIncident[]
  count: number
}

/**
 * Fetch active incidents (ongoing)
 */
export async function fetchActiveIncidents(): Promise<ApiResponse<ActiveIncidentsResponse>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/incidents/active`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) errorDetail = errorData.detail
      } catch { /* ignore */ }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch recent incidents (Phase 6)
 */
export async function fetchIncidents(limit = 50): Promise<ApiResponse<IncidentsResponse>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/incidents?limit=${limit}`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) errorDetail = errorData.detail
      } catch { /* ignore */ }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch reliability metrics (Phase 6)
 */
export async function fetchReliabilityMetrics(windowHours = 24): Promise<ApiResponse<ReliabilityMetrics>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/metrics?window_hours=${windowHours}`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) errorDetail = errorData.detail
      } catch { /* ignore */ }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Phase 8: Metrics history (by week/month)
 */
export interface MetricsHistoryPeriod {
  week_start?: string
  month_start?: string
  disconnect_incidents: number
  engine_stalls: number
  data_stalls: number
  forced_flatten_count: number
  reconciliation_mismatch_count: number
  total_disconnect_duration_sec?: number
}

export interface MetricsHistoryResponse {
  by_period: MetricsHistoryPeriod[]
  stored_history: unknown[]
}

export async function fetchMetricsHistory(
  granularity: 'week' | 'month' = 'week',
  limit = 52
): Promise<ApiResponse<MetricsHistoryResponse>> {
  try {
    const response = await fetchWithTimeout(
      `${API_BASE}/metrics/history?granularity=${granularity}&limit=${limit}`
    )
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) errorDetail = errorData.detail
      } catch { /* ignore */ }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
}

/**
 * Fetch instrument health (Phase 6)
 */
export async function fetchInstrumentHealth(): Promise<ApiResponse<InstrumentHealthResponse>> {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/instrument-health`)
    if (!response.ok) {
      let errorDetail = response.statusText
      try {
        const errorData = await response.json()
        if (errorData.detail) errorDetail = errorData.detail
      } catch { /* ignore */ }
      return { data: null, error: `HTTP ${response.status}: ${errorDetail}` }
    }
    const data = await response.json()
    return { data, error: null }
  } catch (error) {
    return { data: null, error: error instanceof Error ? error.message : 'Unknown error' }
  }
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
