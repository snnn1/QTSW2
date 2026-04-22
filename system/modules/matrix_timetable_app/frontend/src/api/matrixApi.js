/**
 * Matrix API Client
 * 
 * Thin wrapper around matrix backend endpoints for consistent error handling
 * and centralized API configuration.
 */

import { devLog } from '../utils/logger'

// Default: same-origin `/api` so Vite dev proxy can forward to FastAPI. Override with VITE_API_BASE (e.g. absolute URL ending in /api) for production split-host.
export const API_PORT = import.meta.env.VITE_API_PORT || '8000'
export const API_BASE =
  (import.meta.env.VITE_API_BASE && String(import.meta.env.VITE_API_BASE).trim()) || '/api'

/**
 * Parse JSON after reading body as text so empty responses (wrong proxy/port) fail with a clear message
 * instead of `JSON.parse: unexpected end of data`.
 */
async function readResponseJson(response) {
  const text = await response.text()
  if (text == null || String(text).trim() === '') {
    throw new Error(
      `Empty HTTP response (${response.status} ${response.statusText || ''}). ` +
        'The FastAPI process may have crashed during the request (check the backend terminal), or something other than the dashboard is bound to the API port. ' +
        'Confirm Vite proxies /api to the dashboard (vite.config.js; default backend :8000) and that only one backend listens on that port.'
    )
  }
  try {
    return JSON.parse(text)
  } catch (e) {
    const snippet = String(text).replace(/\s+/g, ' ').slice(0, 200)
    throw new Error(
      `Response was not JSON (${response.status}): ${snippet}${text.length > 200 ? '…' : ''}`
    )
  }
}

function errorMessageFromPayload(data) {
  if (data == null || typeof data !== 'object') return null
  const d = data.detail ?? data.error ?? data.message
  if (d == null) return null
  if (typeof d === 'string') return d
  if (Array.isArray(d)) {
    return d.map((x) => (x && typeof x === 'object' && x.msg != null ? x.msg : JSON.stringify(x))).join('; ')
  }
  return String(d)
}

/**
 * URL that must reach FastAPI (under /api), not the Vite dev root.
 * Dev: `/api/matrix/test` is proxied (vite.config.js → :8000). Absolute: `http://host:port/api/matrix/test`.
 */
export function getBackendProbeUrl() {
  return `${String(API_BASE).replace(/\/$/, '')}/matrix/test`
}

/**
 * Check if backend is reachable
 */
export async function checkBackendHealth(timeoutMs = 3000) {
  try {
    const controller = new AbortController()
    const timeoutId = setTimeout(() => controller.abort(), timeoutMs)
    const probeUrl = getBackendProbeUrl()
    const response = await fetch(probeUrl, {
      method: 'GET',
      signal: controller.signal,
    })
    clearTimeout(timeoutId)
    if (!response.ok) {
      const snippet = (await response.text()).replace(/\s+/g, ' ').slice(0, 400)
      return {
        success: false,
        error: `Backend responded ${response.status} at ${probeUrl}${snippet ? `: ${snippet}` : ''}`,
      }
    }
    return { success: true }
  } catch (error) {
    if (error.name === 'AbortError') {
      return {
        success: false,
        error: `Backend connection timeout (${getBackendProbeUrl()}). Is the dashboard API on http://localhost:${API_PORT}?`,
      }
    }
    return {
      success: false,
      error: `Backend not reachable (${getBackendProbeUrl()}): ${error.message}`,
    }
  }
}

/**
 * Build master matrix
 */
export async function buildMatrix({ streamFilters = {}, visibleYears = [], rebuildStream = null, warmupMonths = 1 }) {
  const streamFiltersApi = {}
  Object.keys(streamFilters).forEach(streamId => {
    const filters = streamFilters[streamId]
    if (filters) {
      streamFiltersApi[streamId] = {
        exclude_days_of_week: filters.exclude_days_of_week || [],
        exclude_days_of_month: filters.exclude_days_of_month || [],
        exclude_times: filters.exclude_times || []
      }
    }
  })

  const buildBody = {
    stream_filters: Object.keys(streamFiltersApi).length > 0 ? streamFiltersApi : null,
    warmup_months: warmupMonths
  }
  
  if (visibleYears.length > 0) {
    buildBody.visible_years = visibleYears
  }
  
  if (rebuildStream) {
    buildBody.streams = [rebuildStream]
  }

  const response = await fetch(`${API_BASE}/matrix/build`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(buildBody)
  })

  const payload = await readResponseJson(response)
  if (!response.ok) {
    throw new Error(errorMessageFromPayload(payload) || 'Failed to build master matrix')
  }
  return payload
}


/**
 * Get matrix data
 */
export async function getMatrixData({
  limit = 10000,
  order = 'newest',
  essentialColumnsOnly = true,
  skipCleaning = false,
  contractMultiplier = 1.0,
  includeFilteredExecuted = false,
  streamInclude = null,
  startDate = null,
  endDate = null,
  includeStats = true,
  nocache = false
}) {
  devLog(`[API] getMatrixData called with includeFilteredExecuted=${includeFilteredExecuted} (type: ${typeof includeFilteredExecuted}), nocache=${nocache}`)
  const params = new URLSearchParams({
    limit: limit.toString(),
    order,
    essential_columns_only: essentialColumnsOnly.toString(),
    skip_cleaning: skipCleaning.toString(),
    contract_multiplier: contractMultiplier.toString(),
    include_filtered_executed: includeFilteredExecuted.toString()
  })
  devLog(`[API] Query params: include_filtered_executed=${params.get('include_filtered_executed')}`)
  
  if (streamInclude && Array.isArray(streamInclude) && streamInclude.length > 0) {
    params.append('stream_include', streamInclude.join(','))
  }
  if (startDate) params.append('start_date', startDate)
  if (endDate) params.append('end_date', endDate)
  if (!includeStats) params.append('include_stats', 'false')
  if (nocache) params.append('nocache', 'true')

  const response = await fetch(`${API_BASE}/matrix/data?${params}`)
  const payload = await readResponseJson(response)
  if (!response.ok) {
    throw new Error(errorMessageFromPayload(payload) || 'Failed to load matrix data')
  }
  return payload
}

/**
 * Get stream stats
 */
export async function getStreamStats({ streamId, includeFilteredExecuted = false, contractMultiplier = 1.0 }) {
  const response = await fetch(`${API_BASE}/matrix/stream-stats`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      stream_id: streamId,
      include_filtered_executed: includeFilteredExecuted,
      contract_multiplier: contractMultiplier
    })
  })

  if (!response.ok) {
    const errorText = await response.text()
    throw new Error(`Failed to fetch stats for stream ${streamId}: ${errorText}`)
  }

  return await response.json()
}

/**
 * Get profit breakdown
 */
export async function getProfitBreakdown({
  breakdownType,
  streamFilters = {},
  useFiltered = false,
  contractMultiplier = 1.0,
  streamInclude = null
}) {
  const requestBody = {
    breakdown_type: breakdownType,
    stream_filters: streamFilters,
    use_filtered: useFiltered,
    contract_multiplier: contractMultiplier
  }
  
  // Add stream_include if provided
  if (streamInclude && Array.isArray(streamInclude) && streamInclude.length > 0) {
    requestBody.stream_include = streamInclude
  }
  
  const response = await fetch(`${API_BASE}/matrix/breakdown`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(requestBody)
  })

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to get profit breakdown')
  }

  return await response.json()
}

/**
 * Generate timetable for a trading day (uses RS calculation)
 * Prefer snake_case keys (matches POST JSON). CamelCase aliases supported for callers.
 */
export async function generateTimetable({
  date,
  mode,
  analyzer_runs_dir,
  scf_threshold,
  analyzerRunsDir,
  scfThreshold,
} = {}) {
  const runsDir = analyzer_runs_dir ?? analyzerRunsDir ?? 'data/analyzed'
  const scf = scf_threshold ?? scfThreshold ?? 0.5
  const payload = {
    analyzer_runs_dir: runsDir,
    scf_threshold: scf,
  }
  if (date != null && String(date).trim() !== '') {
    payload.date = String(date).trim()
  }
  if (mode === 'live' || mode === 'historical') {
    payload.mode = mode
  }
  // TEMP audit (remove after debugging)
  console.log('API_REQUEST', '/api/timetable/generate', payload)
  const response = await fetch(`${API_BASE}/timetable/generate`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(payload)
  })

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to generate timetable')
  }

  return await response.json()
}

/**
 * Legacy eligibility JSON status for UI/debug only. Execution authority is timetable_current.json only;
 * fields include robot_execution_authority / legacy_eligibility_json_non_authoritative from the API.
 * @returns {Promise<Record<string, unknown>|null>}
 */
export async function getEligibilityStatus() {
  const response = await fetch(`${API_BASE}/timetable/eligibility/status`)
  if (!response.ok) return null
  const data = await response.json()
  if (data?.status === 'none') return null
  return data
}

/**
 * Live execution timetable. Server ensures file exists (auto-publish from master matrix when needed).
 * Response always includes trading_date, effective_session_trading_date, streams[] on success.
 */
export async function getCurrentTimetable() {
  const response = await fetch(`${API_BASE}/timetable/current`)
  
  if (!response.ok) {
    return null
  }
  
  return await response.json()
}

/**
 * Map GET /api/timetable/current payload to the execution contract shape used by the timetable tab
 * (stream, slot_time, enabled, block_reason). Authority is server-side; UI must not infer these from local filters.
 * @param {Record<string, unknown>|null|undefined} tt
 * @returns {{ session_trading_date: string, trading_date: string, eligibility_trade_date?: string, streams: Array<Record<string, unknown>> } | null}
 */
export function apiDocToExecutionTimetable(tt) {
  if (!tt || typeof tt !== 'object') return null
  const effRaw = tt.effective_session_trading_date ?? tt.session_trading_date ?? tt.trading_date ?? ''
  const sessionTrading = String(effRaw).split('T')[0].trim()
  const tdRaw = tt.trading_date ?? sessionTrading
  const tradingDate = String(tdRaw).split('T')[0].trim()
  const rawStreams = tt.streams
  const streams = Array.isArray(rawStreams)
    ? rawStreams.map((s) => {
        if (!s || typeof s !== 'object') return null
        const stream = s.stream
        if (!stream) return null
        const entry = {
          stream: String(stream),
          slot_time: s.slot_time != null ? String(s.slot_time) : '',
          enabled: s.enabled !== false,
        }
        if (s.block_reason != null && String(s.block_reason).trim() !== '') {
          entry.block_reason = String(s.block_reason)
        }
        return entry
      }).filter(Boolean)
    : []
  const out = {
    session_trading_date: sessionTrading || tradingDate,
    trading_date: tradingDate || sessionTrading,
    streams,
  }
  if (tt.eligibility_trade_date != null && String(tt.eligibility_trade_date).trim() !== '') {
    out.eligibility_trade_date = String(tt.eligibility_trade_date).split('T')[0]
  }
  return out
}

/**
 * Canonical content hash (sorted JSON SHA-256) for debounced publish decisions.
 */
export async function computeTimetableContentHash({ session_trading_date, streams }) {
  const response = await fetch(`${API_BASE}/timetable/content_hash`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      session_trading_date,
      streams: streams || []
    })
  })
  if (!response.ok) {
    const err = await response.json().catch(() => ({}))
    throw new Error(err.detail || `content_hash failed: ${response.status}`)
  }
  return await response.json()
}

/**
 * Strip UI-only keys; match POST /api/matrix/resequence stream_filters shape for execution publish merge.
 */
export function streamFiltersForExecutionApi(streamFilters = {}) {
  const out = {}
  Object.keys(streamFilters).forEach(streamId => {
    const filters = streamFilters[streamId]
    if (filters && typeof filters === 'object') {
      out[streamId] = {
        exclude_days_of_week: filters.exclude_days_of_week || [],
        exclude_days_of_month: filters.exclude_days_of_month || [],
        exclude_times: filters.exclude_times || []
      }
    }
  })
  return out
}

/**
 * Trigger live execution timetable publish from the authoritative in-memory master matrix snapshot.
 * Pass `streamFilters` (Matrix UI state) so calendar rules match background publish when configs/stream_filters.json is missing.
 */
export async function saveExecutionTimetable({
  tradingDate,
  mode = 'live',
  replay = false,
  reason,
  source,
  streamFilters
} = {}) {
  const replayPublish = replay || mode === 'historical'
  const payload = {
    trading_date: tradingDate ?? null,
    mode: mode === 'historical' ? 'historical' : 'live',
    replay: replayPublish,
    reason: reason ?? null,
    source: source ?? null
  }
  if (streamFilters != null && typeof streamFilters === 'object') {
    const apiFilters = streamFiltersForExecutionApi(streamFilters)
    if (Object.keys(apiFilters).length > 0) {
      payload.stream_filters = apiFilters
    }
  }
  // TEMP audit (remove after debugging)
  console.log('API_REQUEST', '/api/timetable/execution', payload)
  const response = await fetch(`${API_BASE}/timetable/execution`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(payload)
  })

  const payloadText = await response.text()
  if (!response.ok) {
    let detail = payloadText
    try {
      const j = JSON.parse(payloadText)
      detail = errorMessageFromPayload(j) || payloadText
    } catch {
      /* use raw */
    }
    throw new Error(
      detail
        ? `Failed to save execution timetable: ${detail}`
        : `Failed to save execution timetable (${response.status}). Empty body — see backend logs.`
    )
  }

  if (!payloadText.trim()) {
    throw new Error('Execution timetable: success response had empty body')
  }
  return JSON.parse(payloadText)
}

/**
 * Preview execution timetable for a specific session date without writing timetable_current.json.
 * Historical mode should use this for the grid so the selected date is reflected immediately.
 */
export async function previewExecutionTimetable({
  tradingDate,
  mode = 'historical',
  streamFilters,
} = {}) {
  const payload = {
    trading_date: tradingDate ?? null,
    mode: mode === 'historical' ? 'historical' : 'live',
  }
  if (streamFilters != null && typeof streamFilters === 'object') {
    const apiFilters = streamFiltersForExecutionApi(streamFilters)
    if (Object.keys(apiFilters).length > 0) {
      payload.stream_filters = apiFilters
    }
  }
  const response = await fetch(`${API_BASE}/timetable/preview`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(payload)
  })

  const payloadText = await response.text()
  if (!response.ok) {
    let detail = payloadText
    try {
      const j = JSON.parse(payloadText)
      detail = errorMessageFromPayload(j) || payloadText
    } catch {
      /* use raw */
    }
    throw new Error(
      detail
        ? `Failed to preview execution timetable: ${detail}`
        : `Failed to preview execution timetable (${response.status}). Empty body — see backend logs.`
    )
  }

  if (!payloadText.trim()) {
    throw new Error('Execution timetable preview: success response had empty body')
  }
  return JSON.parse(payloadText)
}

/**
 * Reload latest matrix file from disk (without rebuilding)
 * This immediately reflects the current disk state.
 */
/**
 * Resequence master matrix (rolling resequence)
 * @param {Object} options - Resequence options
 * @param {Object} options.streamFilters - Stream filter configuration
 * @param {number} options.resequenceDays - Number of trading days to resequence (default 40)
 */
export async function resequenceMatrix({ streamFilters = {}, resequenceDays = 40 }) {
  const streamFiltersApi = {}
  Object.keys(streamFilters).forEach(streamId => {
    const filters = streamFilters[streamId]
    if (filters) {
      streamFiltersApi[streamId] = {
        exclude_days_of_week: filters.exclude_days_of_week || [],
        exclude_days_of_month: filters.exclude_days_of_month || [],
        exclude_times: filters.exclude_times || []
      }
    }
  })

  const resequenceBody = {
    resequence_days: resequenceDays,
    stream_filters: Object.keys(streamFiltersApi).length > 0 ? streamFiltersApi : null
  }

  const response = await fetch(`${API_BASE}/matrix/resequence`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(resequenceBody)
  })

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to resequence master matrix')
  }

  return await response.json()
}

export async function reloadLatestMatrix() {
  const response = await fetch(`${API_BASE}/matrix/reload_latest`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' }
  })

  if (!response.ok) {
    let errorMessage = 'Failed to reload latest matrix'
    try {
      const errorData = await response.json()
      errorMessage = errorData.detail || errorMessage
    } catch {
      // If response isn't JSON, use status text
      errorMessage = `${errorMessage}: ${response.status} ${response.statusText}`
    }
    throw new Error(errorMessage)
  }

  return await response.json()
}

/**
 * List available matrix files
 */
export async function listMatrixFiles() {
  const response = await fetch(`${API_BASE}/matrix/files`)
  if (!response.ok) {
    throw new Error('Failed to list matrix files')
  }
  return await response.json()
}

/**
 * Get matrix performance metrics for the dashboard
 */
export async function getPerformanceMetrics() {
  const response = await fetch(`${API_BASE}/matrix/performance`)

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to get performance metrics')
  }

  return await response.json()
}

/**
 * Compare two matrix files
 */
export async function diffMatrices(fileA, fileB) {
  const response = await fetch(`${API_BASE}/matrix/diff`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ file_a: fileA, file_b: fileB })
  })

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to diff matrices')
  }

  return await response.json()
}

/**
 * Get stream health metrics
 */
export async function getStreamHealth() {
  const response = await fetch(`${API_BASE}/matrix/stream-health`)

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to get stream health')
  }

  return await response.json()
}

/**
 * Get matrix freshness metadata (analyzer vs matrix build times)
 */
export async function getMatrixFreshness(analyzerRunsDir = 'data/analyzed') {
  const params = new URLSearchParams({
    analyzer_runs_dir: analyzerRunsDir
  })

  const response = await fetch(`${API_BASE}/matrix/freshness?${params}`)

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to get matrix freshness')
  }

  return await response.json()
}
