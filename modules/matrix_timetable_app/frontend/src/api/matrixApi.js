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
 * Update master matrix (rolling window)
 */
export async function updateMatrix({ streamFilters = {} }) {
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

  const updateBody = {
    mode: 'window',
    stream_filters: Object.keys(streamFiltersApi).length > 0 ? streamFiltersApi : null
  }

  const response = await fetch(`${API_BASE}/matrix/update`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(updateBody)
  })

  if (!response.ok) {
    const errorData = await response.json()
    throw new Error(errorData.detail || 'Failed to update master matrix')
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
  contractMultiplier = 1.0
}) {
  const response = await fetch(`${API_BASE}/matrix/breakdown`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      breakdown_type: breakdownType,
      stream_filters: streamFilters,
      use_filtered: useFiltered,
      contract_multiplier: contractMultiplier
    })
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
