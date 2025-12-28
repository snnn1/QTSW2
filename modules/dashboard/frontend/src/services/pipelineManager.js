/**
 * Pipeline API service
 */

const API_BASE = '/api'
const HEALTH_URL = '/health'
const REQUEST_TIMEOUT = 10000

/* ===============================
   Core helpers
================================ */

async function fetchWithTimeout(url, options = {}, timeout = REQUEST_TIMEOUT) {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeout)

  try {
    const res = await fetch(url, { ...options, signal: controller.signal })
    return res
  } catch (err) {
    if (err.name === 'AbortError') {
      throw new Error('Request timeout')
    }
    throw err
  } finally {
    clearTimeout(timer)
  }
}

async function parseJSONSafe(res) {
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || `HTTP ${res.status}`)
  }

  const text = await res.text()
  return text ? JSON.parse(text) : null
}

/* ===============================
   Metrics
================================ */

export async function getFileCounts() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/metrics/files`, {}, 5000)
    const data = await parseJSONSafe(res)

    return {
      raw_files: Number.isFinite(data?.raw_files) ? data.raw_files : 0,
      processed_files: Number.isFinite(data?.translated_files) ? data.translated_files : 0, // Backend returns translated_files, map to processed_files for UI
      analyzed_files: Number.isFinite(data?.analyzed_files) ? data.analyzed_files : 0,
    }
  } catch {
    return { raw_files: 0, processed_files: 0, analyzed_files: 0 }
  }
}

export async function getNextScheduledRun() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/schedule/next`)
    return await parseJSONSafe(res)
  } catch {
    return null
  }
}

/* ===============================
   Pipeline
================================ */

export async function getPipelineSnapshot() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/pipeline/snapshot`)
    return await parseJSONSafe(res)
  } catch {
    return null
  }
}

export async function getPipelineStatus() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/pipeline/status`)
    const data = await parseJSONSafe(res)

    if (!data?.state) return { active: false }

    const state = String(data.state).toLowerCase()
    const active =
      state === 'starting' ||
      state === 'running' ||
      state.startsWith('running_')

    return { ...data, active }
  } catch {
    return { active: false }
  }
}

export async function startPipeline() {
  const res = await fetchWithTimeout(
    `${API_BASE}/pipeline/start`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manual: true }),
    },
    30000
  )

  return parseJSONSafe(res)
}

export async function startStage(stage) {
  const res = await fetchWithTimeout(
    `${API_BASE}/pipeline/stage/${stage}`,
    { method: 'POST' }
  )

  return parseJSONSafe(res)
}

export async function resetPipeline() {
  const res = await fetchWithTimeout(
    `${API_BASE}/pipeline/reset`,
    { method: 'POST' }
  )

  return parseJSONSafe(res)
}

/* ===============================
   Scheduler
================================ */

export async function getSchedulerStatus() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/scheduler/status`)
    return await parseJSONSafe(res)
  } catch {
    return { enabled: false, status: 'unknown' }
  }
}

export async function enableScheduler() {
  const res = await fetchWithTimeout(
    `${API_BASE}/scheduler/enable`,
    { method: 'POST' },
    30000
  )

  return parseJSONSafe(res)
}

export async function disableScheduler() {
  const res = await fetchWithTimeout(
    `${API_BASE}/scheduler/disable`,
    { method: 'POST' },
    30000
  )

  return parseJSONSafe(res)
}

/* ===============================
   Apps
================================ */

export async function startApp(app) {
  const res = await fetchWithTimeout(
    `${API_BASE}/apps/${app}/start`,
    { method: 'POST' }
  )

  return parseJSONSafe(res)
}

/* ===============================
   Connection Check (authoritative)
================================ */

export async function checkBackendConnection() {
  try {
    // Use direct backend URL in development (port 8001) instead of relying on Vite proxy
    // This fixes connection failures when proxy is unstable
    const isDev = window.location.hostname === 'localhost' && window.location.port === '5173'
    const healthUrl = isDev ? 'http://localhost:8001/health' : HEALTH_URL
    const res = await fetchWithTimeout(healthUrl, {}, 5000) // Increased timeout to 5s
    if (!res.ok) {
      console.warn(`[Health Check] Backend returned status ${res.status}`)
      return false
    }
    return true
  } catch (error) {
    // Log connection errors for debugging (but only once per failure)
    if (error.name !== 'AbortError') {
      console.warn(`[Health Check] Connection failed:`, error.message)
    }
    return false
  }
}
