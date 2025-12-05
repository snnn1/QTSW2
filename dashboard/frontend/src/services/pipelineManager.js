/**
 * Pipeline API service - handles all API calls for pipeline operations
 */

const API_BASE = 'http://localhost:8001/api'
const REQUEST_TIMEOUT = 10000 // 10 seconds

/**
 * Helper function to create a fetch request with timeout
 * @param {string} url - URL to fetch
 * @param {object} options - Fetch options
 * @param {number} timeout - Timeout in milliseconds (default: REQUEST_TIMEOUT)
 * @returns {Promise<Response>}
 */
async function fetchWithTimeout(url, options = {}, timeout = REQUEST_TIMEOUT) {
  const controller = new AbortController()
  const timeoutId = setTimeout(() => controller.abort(), timeout)
  
  try {
    const response = await fetch(url, {
      ...options,
      signal: controller.signal
    })
    clearTimeout(timeoutId)
    return response
  } catch (error) {
    clearTimeout(timeoutId)
    if (error.name === 'AbortError') {
      throw new Error('Request timeout')
    }
    throw error
  }
}

/**
 * Helper function to parse JSON response with error handling
 * @param {Response} response - Fetch response object
 * @returns {Promise<object>}
 */
async function parseJSONResponse(response) {
  try {
    const text = await response.text()
    if (!text) {
      return null
    }
    return JSON.parse(text)
  } catch (error) {
    console.error('Failed to parse JSON response:', error)
    throw new Error('Invalid JSON response from server')
  }
}

/**
 * Get file counts from the API
 * @returns {Promise<{raw_files: number, processed_files: number, analyzed_files: number}>}
 */
export const getFileCounts = async () => {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/metrics/files`)
    if (!response.ok) {
      console.error(`Failed to load file counts: ${response.status} ${response.statusText}`)
      return { raw_files: -1, processed_files: -1, analyzed_files: -1 }
    }
    const data = await parseJSONResponse(response)
    console.log('File counts loaded:', data)
    return data
  } catch (error) {
    console.error('Failed to load file counts:', error)
    return { raw_files: -1, processed_files: -1, analyzed_files: -1 }
  }
}

/**
 * Get next scheduled run information
 * @returns {Promise<object|null>}
 */
export const getNextScheduledRun = async () => {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/schedule/next`)
    if (!response.ok) {
      console.error(`Failed to load next scheduled run: ${response.status} ${response.statusText}`)
      return null
    }
    const data = await parseJSONResponse(response)
    return data
  } catch (error) {
    console.error('Failed to load next scheduled run:', error)
    return null
  }
}

/**
 * Get current pipeline status
 * @returns {Promise<{active: boolean, run_id?: string, stage?: string}>}
 */
export const getPipelineStatus = async () => {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/pipeline/status`)
    const data = await parseJSONResponse(response)
    return data
  } catch (error) {
    console.error('Failed to check pipeline status:', error)
    return { active: false }
  }
}

/**
 * Start the full pipeline
 * @returns {Promise<{run_id: string}>}
 */
export const startPipeline = async () => {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/pipeline/start`, {
      method: 'POST'
    })
    if (!response.ok) {
      throw new Error(`Failed to start pipeline: ${response.status}`)
    }
    const data = await parseJSONResponse(response)
    return data
  } catch (error) {
    console.error('Failed to start pipeline:', error)
    throw error
  }
}

/**
 * Start a specific pipeline stage
 * @param {string} stageName - Name of the stage to start
 * @returns {Promise<{run_id: string}>}
 */
export const startStage = async (stageName) => {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/pipeline/stage/${stageName}`, {
      method: 'POST'
    })
    if (!response.ok) {
      throw new Error(`Failed to start stage ${stageName}: ${response.status}`)
    }
    const data = await parseJSONResponse(response)
    return data
  } catch (error) {
    console.error(`Failed to start ${stageName} stage:`, error)
    throw error
  }
}

/**
 * Run the data merger
 * @returns {Promise<{status: string, message?: string}>}
 */
export const runMerger = async () => {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/merger/run`, {
      method: 'POST'
    }, 300000) // 5 minute timeout for merger
    if (!response.ok) {
      throw new Error(`Failed to run merger: ${response.status}`)
    }
    const data = await parseJSONResponse(response)
    return data
  } catch (error) {
    console.error('Failed to run data merger:', error)
    throw error
  }
}

/**
 * Start an application (translator, analyzer, matrix)
 * @param {string} appName - Name of the app ('translator', 'analyzer', 'matrix')
 * @returns {Promise<{url: string}>}
 */
export const startApp = async (appName) => {
  try {
    const response = await fetchWithTimeout(`${API_BASE}/apps/${appName}/start`, {
      method: 'POST'
    })
    if (!response.ok) {
      throw new Error(`Failed to start ${appName} app: ${response.status}`)
    }
    const data = await parseJSONResponse(response)
    return data
  } catch (error) {
    console.error(`Failed to start ${appName} app:`, error)
    throw error
  }
}

/**
 * Check if backend is connected
 * @returns {Promise<boolean>}
 */
export const checkBackendConnection = async () => {
  try {
    const response = await fetchWithTimeout('http://localhost:8001/', {
      method: 'GET'
    }, 2000) // 2 second timeout for health check
    return response.ok
  } catch (error) {
    return false
  }
}


