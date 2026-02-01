/**
 * Matrix API Client
 * 
 * Thin wrapper around matrix backend endpoints for consistent error handling
 * and centralized API configuration.
 */

// API base URL - can be overridden via environment variable
const API_PORT = import.meta.env.VITE_API_PORT || '8000'
const API_BASE = `http://localhost:${API_PORT}/api`

/**
 * Check if backend is reachable
 */
export async function checkBackendHealth(timeoutMs = 3000) {
  try {
    const controller = new AbortController()
    const timeoutId = setTimeout(() => controller.abort(), timeoutMs)
    await fetch(`${API_BASE.replace('/api', '')}/`, {
      method: 'GET',
      signal: controller.signal
    })
    clearTimeout(timeoutId)
    return { success: true }
  } catch (error) {
    if (error.name === 'AbortError') {
      return {
        success: false,
        error: `Backend connection timeout. Make sure the dashboard backend is running on http://localhost:${API_PORT}`
      }
    }
    return {
      success: false,
      error: `Backend not running. Please start the dashboard backend on port ${API_PORT}. Error: ${error.message}`
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

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to build master matrix')
  }

  return await response.json()
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
  streamInclude = null
}) {
  const params = new URLSearchParams({
    limit: limit.toString(),
    order,
    essential_columns_only: essentialColumnsOnly.toString(),
    skip_cleaning: skipCleaning.toString(),
    contract_multiplier: contractMultiplier.toString(),
    include_filtered_executed: includeFilteredExecuted.toString()
  })
  
  // Add stream_include if specified
  if (streamInclude && Array.isArray(streamInclude) && streamInclude.length > 0) {
    params.append('stream_include', streamInclude.join(','))
  }

  const response = await fetch(`${API_BASE}/matrix/data?${params}`)

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to load matrix data')
  }

  return await response.json()
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
 */
export async function generateTimetable({ date, analyzerRunsDir = 'data/analyzed', scfThreshold = 0.5 }) {
  const response = await fetch(`${API_BASE}/timetable/generate`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      date,
      analyzer_runs_dir: analyzerRunsDir,
      scf_threshold: scfThreshold
    })
  })

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to generate timetable')
  }

  return await response.json()
}

/**
 * Get current execution timetable file
 */
export async function getCurrentTimetable() {
  const response = await fetch(`${API_BASE}/timetable/current`)
  
  if (!response.ok) {
    return null
  }
  
  return await response.json()
}

/**
 * Save execution timetable
 */
export async function saveExecutionTimetable({ tradingDate, streams }) {
  const response = await fetch(`${API_BASE}/timetable/execution`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      trading_date: tradingDate,
      streams
    })
  })

  if (!response.ok) {
    const errorText = await response.text()
    throw new Error(`Failed to save execution timetable: ${errorText}`)
  }

  return await response.json()
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
