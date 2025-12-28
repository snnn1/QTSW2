/**
 * Pipeline API service and React hook
 * Single, consistent contract with backend
 */

import { useState, useEffect, useCallback, useRef } from 'react'
import { start as startAppService } from '../services/appsManager'
import { useWebSocket } from '../contexts/WebSocketContext'

const API_BASE = '/api'
const HEALTH_URL = '/health'

const DEFAULT_TIMEOUT = 8000

/* -------------------------------------------------------
   Fetch helper with timeout
------------------------------------------------------- */
async function fetchWithTimeout(url, options = {}, timeout = DEFAULT_TIMEOUT) {
  const controller = new AbortController()
  const id = setTimeout(() => controller.abort(), timeout)

  try {
    return await fetch(url, { ...options, signal: controller.signal })
  } finally {
    clearTimeout(id)
  }
}

/* -------------------------------------------------------
   JSON parsing helper
------------------------------------------------------- */
async function safeJson(response) {
  const text = await response.text()
  if (!text) return null
  return JSON.parse(text)
}

/* -------------------------------------------------------
   File counts
------------------------------------------------------- */
export async function getFileCounts() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/metrics/files`, {}, 5000)
    if (!res.ok) throw new Error(res.status)
    const data = await safeJson(res)

    return {
      raw_files: Number(data?.raw_files) || 0,
      processed_files: Number(data?.processed_files) || 0,
      analyzed_files: Number(data?.analyzed_files) || 0,
    }
  } catch {
    return { raw_files: 0, processed_files: 0, analyzed_files: 0 }
  }
}

/* -------------------------------------------------------
   Schedule
------------------------------------------------------- */
export async function getNextScheduledRun() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/schedule/next`, {}, 5000)
    if (!res.ok) return null
    return await safeJson(res)
  } catch {
    return null
  }
}

/* -------------------------------------------------------
   Snapshot (events + status)
------------------------------------------------------- */
export async function getPipelineSnapshot() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/pipeline/snapshot`, {}, 8000)
    if (!res.ok) return null
    return await safeJson(res)
  } catch {
    return null
  }
}

/* -------------------------------------------------------
   Pipeline status (authoritative)
------------------------------------------------------- */
export async function getPipelineStatus() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/pipeline/status`, {}, 5000)
    if (!res.ok) return null
    return await safeJson(res)
  } catch {
    return null
  }
}

/* -------------------------------------------------------
   Start pipeline
------------------------------------------------------- */
export async function startPipeline() {
  console.log('[API] Calling POST /api/pipeline/start')
  try {
  const res = await fetchWithTimeout(
    `${API_BASE}/pipeline/start`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manual: true }),
    },
    15000
  )

    console.log('[API] Response status:', res.status, res.statusText)

  if (!res.ok) {
      const errorText = await res.text()
      console.error('[API] Pipeline start failed:', res.status, errorText)
      throw new Error(errorText || `HTTP ${res.status}`)
  }

    const result = await safeJson(res)
    console.log('[API] Pipeline start success:', result)
    return result
  } catch (error) {
    console.error('[API] Pipeline start exception:', error)
    throw error
  }
}

/* -------------------------------------------------------
   Run single stage (translator / analyzer / merger)
------------------------------------------------------- */
export async function startStage(stageName) {
  const res = await fetchWithTimeout(
    `${API_BASE}/pipeline/stage/${stageName}`,
    { method: 'POST' },
    10000
  )

  if (!res.ok) {
    throw new Error(await res.text())
  }

  return await safeJson(res)
}

/* -------------------------------------------------------
   Scheduler
------------------------------------------------------- */
export async function getSchedulerStatus() {
  try {
    const res = await fetchWithTimeout(`${API_BASE}/scheduler/status`, {}, 5000)
    if (!res.ok) return null
    return await safeJson(res)
  } catch {
    return null
  }
}

export async function enableScheduler() {
  const res = await fetchWithTimeout(`${API_BASE}/scheduler/enable`, { method: 'POST' }, 15000)
  if (!res.ok) throw new Error(await res.text())
  return await safeJson(res)
}

export async function disableScheduler() {
  const res = await fetchWithTimeout(`${API_BASE}/scheduler/disable`, { method: 'POST' }, 15000)
  if (!res.ok) throw new Error(await res.text())
  return await safeJson(res)
}

/* -------------------------------------------------------
   Reset pipeline
------------------------------------------------------- */
export async function resetPipeline() {
  const res = await fetchWithTimeout(`${API_BASE}/pipeline/reset`, { method: 'POST' }, 8000)
  if (!res.ok) throw new Error(await res.text())
  return await safeJson(res)
}

/* -------------------------------------------------------
   Clear pipeline lock
------------------------------------------------------- */
export async function clearPipelineLock() {
  console.log('[API] Calling POST /api/pipeline/clear-lock')
  const res = await fetchWithTimeout(`${API_BASE}/pipeline/clear-lock`, { method: 'POST' }, 8000)
  if (!res.ok) throw new Error(await res.text())
  return await safeJson(res)
}

/* -------------------------------------------------------
   Backend connectivity check
------------------------------------------------------- */
export async function checkBackendConnection() {
  try {
    const res = await fetchWithTimeout(HEALTH_URL, {}, 3000)
    return res.ok
  } catch {
    return false
  }
}

/* -------------------------------------------------------
   React Hook: usePipelineState
------------------------------------------------------- */
export function usePipelineState() {
  // Pipeline state
  const [pipelineStatus, setPipelineStatus] = useState({
    state: 'idle',
    isRunning: false,
    runId: null,
  })
  const [isStarting, setIsStarting] = useState(false)
  const [activeRunId, setActiveRunId] = useState(null)
  
  // Other state
  const [stageInfo, setStageInfo] = useState({})
  const [exportInfo, setExportInfo] = useState({
    status: 'not_started',
    rows: 0,
    files: 0,
    label: '-',
    percent: 0,
  })
  const [metrics, setMetrics] = useState({ rowsPerMin: 0 })
  const [scheduleInfo, setScheduleInfo] = useState(null)
  const [eventsFormatted, setEventsFormatted] = useState([])
  const [mergerInfo, setMergerInfo] = useState({})
  const [alertInfo, setAlertInfo] = useState(null)
  const [schedulerEnabled, setSchedulerEnabled] = useState(false)
  
  // Refs for polling
  const pollingIntervalRef = useRef(null)
  const statusPollingRef = useRef(null)

  // Update pipeline status from backend response
  // Phase-1: Only update state when backend explicitly provides valid data
  // Never reset state on polling failure - keep last known state
  const updatePipelineStatus = useCallback((statusData) => {
    // Phase-1 rule: If status fails → do nothing, keep last known state
    // Only change state when backend explicitly tells you to
    if (!statusData || statusData.state === 'unavailable') {
      // Don't reset state - just log and return
      console.warn('[Status] Status unavailable or missing, keeping last known state')
      return
    }

    // Phase-1 always-on: Simple state mapping - canonical states only: idle, running, stopped, error
    const state = String(statusData.state || 'idle').toLowerCase()
    const isRunning = state === 'running'  // Only "running" state means pipeline is running
    
    const runId = statusData.run_id || statusData.runId || null
    
    // Guard: Don't overwrite "running" state with "idle" if we have an active run_id
    // This prevents immediate polling from resetting the state right after start
    setPipelineStatus(prev => {
      // If we're currently running and backend says idle, but we have a run_id, keep running
      // This handles the race condition where polling happens before backend updates
      if (prev.isRunning && state === 'idle' && (runId || prev.runId)) {
        console.log('[Status] Keeping running state - backend may not have updated yet', { 
          prevState: prev.state, 
          backendState: state, 
          runId: runId || prev.runId 
        })
        return prev  // Keep current running state
      }
      
      // Otherwise, update with backend state
      return {
        state,
        isRunning,
        runId,
      }
    })
    setActiveRunId(runId)
  }, [])

  // Poll pipeline status (lightweight - just status, no snapshot)
  const pollPipelineStatus = useCallback(async () => {
    try {
      const status = await getPipelineStatus()
      updatePipelineStatus(status)
    } catch (error) {
      console.error('Error polling pipeline status:', error)
    }
  }, [updatePipelineStatus])

  // Load metadata (scheduler, schedule, file counts) - separate from status polling
  const loadMetadata = useCallback(async () => {
    try {
      // Load scheduler status
      const schedulerStatus = await getSchedulerStatus()
      setSchedulerEnabled(schedulerStatus?.enabled || false)
      
      // Load schedule info
      const nextRun = await getNextScheduledRun()
      setScheduleInfo(nextRun)
      
      // Load file counts as metrics
      const fileCounts = await getFileCounts()
      setMetrics(prev => ({ ...prev, ...fileCounts }))
    } catch (error) {
      console.error('Error loading metadata:', error)
    }
  }, [])

  // Phase-1 always-on: Minimal polling - WebSocket is primary source of truth
  // Poll status + metadata every 30 seconds (just for button state, WebSocket handles events)
  useEffect(() => {
    // Initial load
    pollPipelineStatus()
    loadMetadata()
    
    // Poll every 30 seconds (lightweight, just for status/metadata)
      statusPollingRef.current = setInterval(() => {
        pollPipelineStatus()
        loadMetadata()
    }, 30000)  // 30 seconds - WebSocket handles real-time events
    
    return () => {
      if (statusPollingRef.current) {
        clearInterval(statusPollingRef.current)
        statusPollingRef.current = null
      }
    }
  }, [pollPipelineStatus, loadMetadata])  // Fixed interval, don't depend on isRunning

  // Phase-1 always-on: Consume events from WebSocket context (singleton at App level)
  const { events: wsEvents, subscribe: subscribeToWebSocket } = useWebSocket()
  
  // Subscribe to WebSocket events and handle them
  useEffect(() => {
    const handleEvent = (event) => {
      // Handle snapshot events (replace existing events)
      if (event.type === 'snapshot' && Array.isArray(event.events)) {
        setEventsFormatted(event.events)
        console.log(`[Events] Snapshot loaded: ${event.events.length} events`)
        return
      }
      
      // Update status from state_change events
      if (event.event === 'state_change' && event.data) {
          const newState = event.data.new_state
          if (newState) {
            updatePipelineStatus({ state: newState, run_id: event.run_id || activeRunId })
          }
      }
    }
    
    // Subscribe to WebSocket events
    const unsubscribe = subscribeToWebSocket(handleEvent)
    
    return () => {
      unsubscribe()
    }
  }, [subscribeToWebSocket, updatePipelineStatus, activeRunId])
  
  // Sync WebSocket events to local state (for EventsLog component)
  useEffect(() => {
    setEventsFormatted(wsEvents)
  }, [wsEvents])

  // Start pipeline
  const handleStartPipeline = useCallback(async () => {
    console.log('[Pipeline] Start button clicked', { isStarting, isRunning: pipelineStatus.isRunning, state: pipelineStatus.state })
    
    if (isStarting || pipelineStatus.isRunning) {
      console.log('[Pipeline] Start blocked - already starting or running')
      return
    }
    
    setIsStarting(true)
    try {
      console.log('[Pipeline] Calling startPipeline API...')
      const result = await startPipeline()
      console.log('[Pipeline] Start API response:', result)
      
      if (result && result.run_id) {
        console.log('[Pipeline] Pipeline started, run_id:', result.run_id)
        setActiveRunId(result.run_id)
        // Immediately set running state so button disables right away
        // WebSocket events will update this later, but we want immediate UI feedback
        setPipelineStatus({
          state: 'running',
          isRunning: true,
          runId: result.run_id
        })
        setAlertInfo({
          type: 'success',
          message: 'Pipeline started successfully',
        })
        
        // Don't poll immediately - backend may not have updated state yet
        // WebSocket events will update state quickly, and regular polling will catch it
        // Poll after a short delay to avoid race condition
        setTimeout(() => {
          pollPipelineStatus()
        }, 1000)  // Wait 1 second before polling to let backend update
      } else {
        console.warn('[Pipeline] Start API returned no run_id:', result)
        setAlertInfo({
          type: 'error',
          message: 'Pipeline start response missing run_id',
        })
      }
    } catch (error) {
      console.error('[Pipeline] Start failed:', error)
      setAlertInfo({
        type: 'error',
        message: error.message || 'Failed to start pipeline',
      })
    } finally {
      setIsStarting(false)
    }
  }, [isStarting, pipelineStatus.isRunning, pollPipelineStatus])

  // Reset pipeline
  const handleResetPipeline = useCallback(async () => {
    try {
      await resetPipeline()
      setActiveRunId(null)
      setPipelineStatus({ state: 'idle', isRunning: false, runId: null })
      setEventsFormatted([])
      setAlertInfo({
        type: 'success',
        message: 'Pipeline reset successfully',
      })
      
      // Status will update via WebSocket events - no need to poll
    } catch (error) {
      setAlertInfo({
        type: 'error',
        message: error.message || 'Failed to reset pipeline',
      })
    }
  }, [pollPipelineStatus])

  // Toggle scheduler
  const handleToggleScheduler = useCallback(async () => {
    try {
      const result = schedulerEnabled 
        ? await disableScheduler()
        : await enableScheduler()
      
      // Refresh status from Windows Task Scheduler to get authoritative state
      const status = await getSchedulerStatus()
      if (status) {
        setSchedulerEnabled(status.enabled)
      } else {
        // Fallback to result if status check fails
        setSchedulerEnabled(result.enabled)
      }
      
      setAlertInfo({
        type: 'success',
        message: result.message,
      })
    } catch (error) {
      setAlertInfo({
        type: 'error',
        message: error.message || 'Failed to toggle scheduler',
      })
      // Refresh status even on error to see actual state
      try {
        const status = await getSchedulerStatus()
        if (status) {
          setSchedulerEnabled(status.enabled)
        }
      } catch (e) {
        // Ignore status refresh errors
      }
    }
  }, [schedulerEnabled])

  // Run stage
  const handleRunStage = useCallback(async (stageName) => {
    try {
      await startStage(stageName)
      setAlertInfo({
        type: 'success',
        message: `${stageName} started`,
      })
      // Status will update via WebSocket events - no need to poll
    } catch (error) {
      setAlertInfo({
        type: 'error',
        message: error.message || `Failed to start ${stageName}`,
      })
    }
  }, [pollPipelineStatus])

  // Run data merger
  const handleRunDataMerger = useCallback(async () => {
    try {
      await startStage('merger')
      setAlertInfo({
        type: 'success',
        message: 'Data merger started',
      })
      // Status will update via WebSocket events - no need to poll
    } catch (error) {
      setAlertInfo({
        type: 'error',
        message: error.message || 'Failed to start data merger',
      })
    }
  }, [pollPipelineStatus])

  // Start app
  const handleStartApp = useCallback(async (appName) => {
    try {
      await startAppService(appName)
    } catch (error) {
      setAlertInfo({
        type: 'error',
        message: error.message || `Failed to start ${appName}`,
      })
    }
  }, [])

  // Clear alert
  const clearAlert = useCallback(() => {
    setAlertInfo(null)
  }, [])

  // Clear pipeline lock
  const handleClearLock = useCallback(async () => {
    try {
      await clearPipelineLock()
      setAlertInfo({
        type: 'success',
        message: 'Pipeline lock cleared successfully',
      })
      // Refresh status after clearing lock
      pollPipelineStatus()
    } catch (error) {
      setAlertInfo({
        type: 'error',
        message: error.message || 'Failed to clear pipeline lock',
      })
    }
  }, [pollPipelineStatus])

  return {
    pipelineStatus,
    stageInfo,
    exportInfo,
    metrics,
    scheduleInfo,
    eventsFormatted,
    mergerInfo,
    alertInfo,
    schedulerEnabled,
    startPipeline: handleStartPipeline,
    runStage: handleRunStage,
    runDataMerger: handleRunDataMerger,
    startApp: handleStartApp,
    resetPipelineState: handleResetPipeline,
    clearAlert,
    toggleScheduler: handleToggleScheduler,
    clearPipelineLock: handleClearLock,
  }
}
