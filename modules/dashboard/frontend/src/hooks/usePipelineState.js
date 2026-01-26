/**
 * Pipeline API service and React hook
 * Single, consistent contract with backend
 */

import { useState, useEffect, useCallback, useRef } from 'react'
import { start as startAppService } from '../services/appsManager'
import { useWebSocket } from '../contexts/WebSocketContext'
import {
  API_TIMEOUT_SHORT,
  API_TIMEOUT_DEFAULT,
  API_TIMEOUT_LONG,
  POLL_INTERVAL_IDLE,
  POLL_INTERVAL_RUNNING
} from '../config/constants'

const API_BASE = '/api'
const HEALTH_URL = '/health'

/* -------------------------------------------------------
   Fetch helper with timeout
------------------------------------------------------- */
async function fetchWithTimeout(url, options = {}, timeout = API_TIMEOUT_DEFAULT) {
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
    // Short timeout - cache should make this instant
    const res = await fetchWithTimeout(`${API_BASE}/metrics/files`, {}, API_TIMEOUT_SHORT)
    if (!res.ok) throw new Error(res.status)
    const data = await safeJson(res)

    return {
      raw_files: Number(data?.raw_files) || 0,
      processed_files: Number(data?.translated_files) || 0, // Backend returns translated_files, map to processed_files for UI
      analyzed_files: Number(data?.analyzed_files) || 0,
    }
  } catch (error) {
    // Don't return zeros on timeout - throw so caller can keep last-known-good values
    throw error
  }
}

/* -------------------------------------------------------
   Schedule
------------------------------------------------------- */
export async function getNextScheduledRun() {
  try {
    // Short timeout for fast failure
    const res = await fetchWithTimeout(`${API_BASE}/schedule/next`, {}, API_TIMEOUT_SHORT)
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
    const res = await fetchWithTimeout(`${API_BASE}/pipeline/snapshot`, {}, API_TIMEOUT_DEFAULT)
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
    // Short timeout for fast failure
    const res = await fetchWithTimeout(`${API_BASE}/pipeline/status`, {}, 2000)
    if (!res.ok) return null
    return await safeJson(res)
  } catch {
    return null
  }
}

/* -------------------------------------------------------
   Start pipeline
------------------------------------------------------- */
export async function startPipeline(manualOverride = false) {
  console.log('[API] Calling POST /api/pipeline/start', { manualOverride })
  try {
  const res = await fetchWithTimeout(
    `${API_BASE}/pipeline/start`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manual: true, manual_override: manualOverride }),
    },
    API_TIMEOUT_LONG
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
    // Short timeout for fast failure
    const res = await fetchWithTimeout(`${API_BASE}/scheduler/status`, {}, API_TIMEOUT_SHORT)
    if (!res.ok) return null
    return await safeJson(res)
  } catch {
    return null
  }
}

export async function enableScheduler() {
  const res = await fetchWithTimeout(`${API_BASE}/scheduler/enable`, { method: 'POST' }, API_TIMEOUT_LONG)
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
  const res = await fetchWithTimeout(`${API_BASE}/pipeline/reset`, { method: 'POST' }, API_TIMEOUT_DEFAULT)
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
    const res = await fetchWithTimeout(HEALTH_URL, {}, API_TIMEOUT_SHORT)
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
  // Track recently started runs to prevent premature state reset
  const recentlyStartedRunIdRef = useRef(null)
  
  // Other state
  const [stageInfo, setStageInfo] = useState({})
  const [exportInfo, setExportInfo] = useState({
    status: 'not_started',
    rows: 0,
    files: 0,
    label: '-',
    percent: 0,
  })
  const [metrics, setMetrics] = useState({ 
    rowsPerMin: 0,
    raw_files: 0,
    processed_files: 0,
    analyzed_files: 0
  })
  const [scheduleInfo, setScheduleInfo] = useState(null)
  const [eventsFormatted, setEventsFormatted] = useState([])
  const [mergerInfo, setMergerInfo] = useState({})
  const [alertInfo, setAlertInfo] = useState(null)
  const [schedulerEnabled, setSchedulerEnabled] = useState(false)
  
  // Refs for polling
  const pollingIntervalRef = useRef(null)
  const statusPollingRef = useRef(null)
  const metricsRef = useRef(metrics)  // Keep ref to current metrics for callback
  
  // Keep ref in sync with state
  useEffect(() => {
    metricsRef.current = metrics
  }, [metrics])

  // Phase-1 always-on: Consume events from WebSocket context (singleton at App level)
  const { events: wsEvents, subscribe: subscribeToWebSocket, isConnected: wsConnected } = useWebSocket()

  // Update pipeline status from backend response
  // Phase-1: Only update state when backend explicitly provides valid data
  // Never reset state on polling failure - keep last known state
  const updatePipelineStatus = useCallback((statusData, source = 'unknown') => {
    // Phase-1 rule: If status fails → do nothing, keep last known state
    // Only change state when backend explicitly tells you to
    if (!statusData || statusData.state === 'unavailable') {
      // Don't reset state - just log and return
      console.warn('[Status] Status unavailable or missing, keeping last known state')
      return
    }

    // Phase-1 always-on: Simple state mapping - canonical states only: idle, running, stopped, error
    const state = String(statusData.state || 'idle').toLowerCase()
    
    // Check internal state to detect success/completion
    const internalState = statusData._internal_state || statusData.internal_state || null
    const isSuccess = internalState === 'success'
    
    // If backend reports success, pipeline is definitely done - make button clickable
    const isRunning = isSuccess ? false : (state === 'running')  // Only "running" state means pipeline is running
    
    const runId = statusData.run_id || statusData.runId || null
    
    setPipelineStatus(prev => {
      const newStatus = {
        state,
        isRunning,
        runId,
      }
      
      // If pipeline finished (success or transitioned from running to idle), reset isStarting flag
      if (isSuccess || (prev.isRunning && !isRunning && state === 'idle')) {
        // Pipeline finished - reset isStarting flag and clear recently started flag
        setIsStarting(false)
        if (recentlyStartedRunIdRef.current === runId || recentlyStartedRunIdRef.current === prev.runId) {
          recentlyStartedRunIdRef.current = null
        }
      }
      
      return newStatus
    })
    setActiveRunId(runId)
  }, [])

  // Poll pipeline status (lightweight - just status, no snapshot)
  const pollPipelineStatus = useCallback(async () => {
    try {
      const status = await getPipelineStatus()
      updatePipelineStatus(status, 'polling')
    } catch (error) {
      console.error('Error polling pipeline status:', error)
    }
  }, [updatePipelineStatus])

  // Load metadata (scheduler, schedule, file counts) - separate from status polling
  // OPTIMIZED: File counts update immediately (cached), others update as they arrive
  // Never reset to 0 on timeout - keep last-known-good values
  const loadMetadata = useCallback(async () => {
    // File counts is cached - load it first and update immediately (don't wait for validation)
    getFileCounts()
      .then(fileCounts => {
        // Always update immediately - cache ensures fast response
        if (fileCounts) {
          setMetrics(prev => ({ ...prev, ...fileCounts }))
        }
      })
      .catch(error => {
        console.error('Error loading file counts:', error)
        // Keep last-known-good values (don't reset to 0)
      })
    
    // Load scheduler and nextRun independently - update UI as each arrives (don't wait for both)
    getSchedulerStatus()
      .then(status => {
        if (status?.enabled !== undefined) {
          setSchedulerEnabled(status.enabled)
        }
      })
      .catch(error => {
        console.error('Error loading scheduler status:', error)
        // Keep last-known-good value
      })
    
    getNextScheduledRun()
      .then(nextRun => {
        if (nextRun) {
          setScheduleInfo(nextRun)
        }
      })
      .catch(error => {
        console.error('Error loading next scheduled run:', error)
        // Keep last-known-good value
      })
  }, [])

  // State Priority Order:
  // 1. WebSocket events (authoritative real-time state)
  // 2. WebSocket snapshot (initial sync)
  // 3. Polling (fallback only when WebSocket is disconnected)
  //
  // Polling must never overwrite newer WebSocket-derived state
  // Polling frequency should remain reduced when WebSocket is healthy
  
  // Phase-1 always-on: Minimal polling - WebSocket is primary source of truth
  // Poll status + metadata at reduced frequency when WebSocket is connected
  // Increase polling frequency when WebSocket is disconnected (fallback mode)
  useEffect(() => {
    // Initial load - prioritize status for Run ID, then metadata
    pollPipelineStatus()  // Get Run ID immediately from status endpoint
    loadMetadata()  // Load metadata (file counts, scheduler) - non-blocking
    
    // Adaptive polling based on WebSocket connection state
    // When WebSocket connected: poll every 60s idle, 10s running (lightweight fallback)
    // When WebSocket disconnected: poll every 10s (more aggressive fallback)
    const getPollInterval = () => {
      if (!wsConnected) {
        return 10000  // 10 seconds when WebSocket disconnected (fallback mode)
      }
      // WebSocket connected - use reduced frequency
      return pipelineStatus.isRunning ? 10000 : 60000  // 10s running, 60s idle
    }
    
    const pollInterval = getPollInterval()
    
    // Poll at adaptive interval (lightweight, just for status/metadata)
    statusPollingRef.current = setInterval(() => {
      pollPipelineStatus()
      loadMetadata()
    }, pollInterval)
    
    return () => {
      if (statusPollingRef.current) {
        clearInterval(statusPollingRef.current)
        statusPollingRef.current = null
      }
    }
  }, [pollPipelineStatus, loadMetadata, pipelineStatus.isRunning, wsConnected])  // Re-run when running state or WebSocket connection changes

  // State Priority Order:
  // 1. WebSocket events (authoritative real-time state)
  // 2. WebSocket snapshot (initial sync)
  // 3. Polling (fallback only when WebSocket is disconnected)
  //
  // Polling must never overwrite newer WebSocket-derived state
  // Polling frequency should remain reduced when WebSocket is healthy
  
  // Phase-1 always-on: Minimal polling - WebSocket is primary source of truth
  // Poll status + metadata at reduced frequency when WebSocket is connected
  // Increase polling frequency when WebSocket is disconnected (fallback mode)
  useEffect(() => {
    // Initial load - prioritize status for Run ID, then metadata
    pollPipelineStatus()  // Get Run ID immediately from status endpoint
    loadMetadata()  // Load metadata (file counts, scheduler) - non-blocking
    
    // Adaptive polling based on WebSocket connection state
    // When WebSocket connected: poll every 60s idle, 10s running (lightweight fallback)
    // When WebSocket disconnected: poll every 10s (more aggressive fallback)
    const getPollInterval = () => {
      if (!wsConnected) {
        return 10000  // 10 seconds when WebSocket disconnected (fallback mode)
      }
      // WebSocket connected - use reduced frequency
      return pipelineStatus.isRunning ? 10000 : 60000  // 10s running, 60s idle
    }
    
    const pollInterval = getPollInterval()
    
    // Poll at adaptive interval (lightweight, just for status/metadata)
    statusPollingRef.current = setInterval(() => {
      pollPipelineStatus()
      loadMetadata()
    }, pollInterval)
    
    return () => {
      if (statusPollingRef.current) {
        clearInterval(statusPollingRef.current)
        statusPollingRef.current = null
      }
    }
  }, [pollPipelineStatus, loadMetadata, pipelineStatus.isRunning, wsConnected])  // Re-run when running state or WebSocket connection changes
  
  // Subscribe to WebSocket events and handle them
  useEffect(() => {
    const handleEvent = (event) => {
      // Handle snapshot events (replace existing events)
      // Snapshot is optional - if it arrives, use it, but don't wait for it
      if (event.type === 'snapshot' && Array.isArray(event.events)) {
        setEventsFormatted(event.events)
        console.log(`[Events] Snapshot loaded: ${event.events.length} events`)
        
        // Extract run_id from snapshot quickly (only check last 10 events - minimal work)
        // Run in next tick to avoid blocking UI
        setTimeout(() => {
          if (event.events.length > 0) {
            // Only check last 10 events (most recent) - ultra-fast lookup
            const recentEvents = event.events.slice(-10)
            let latestRunId = null
            
            // Quick scan for run_id (don't worry about state - status endpoint has that)
            for (let i = recentEvents.length - 1; i >= 0; i--) {
              const e = recentEvents[i]
              if (e.run_id) {
                latestRunId = e.run_id
                break  // Found it, stop
              }
            }
            
            // Update run_id if found (status endpoint will provide state)
            if (latestRunId) {
              updatePipelineStatus({
                run_id: latestRunId,
              }, 'websocket-snapshot')
            }
          }
        }, 0)
        return
      }
      
      // Handle scheduler events - track as recently started to update button state
      if (event.stage === 'scheduler' && event.event === 'scheduled_run_started' && event.run_id) {
        // Track scheduler-triggered runs as recently started
        recentlyStartedRunIdRef.current = event.run_id
        setTimeout(() => {
          if (recentlyStartedRunIdRef.current === event.run_id) {
            recentlyStartedRunIdRef.current = null
          }
        }, 30000)
        // Update state to running immediately for scheduler-triggered runs
        updatePipelineStatus({
          state: 'running',
          run_id: event.run_id,
        }, 'websocket')
        return
      }
      
      // Update status from state_change events
      // CRITICAL: Use complete canonical_state from event for truthfulness
      // This ensures WebSocket and polling always agree
      if (event.event === 'state_change' && event.data) {
        // Prefer canonical_state if available (complete truth)
        if (event.data.canonical_state) {
          const canonical = event.data.canonical_state
          // Map internal FSM state to canonical state (idle, running, stopped, error)
          // This matches the backend canonical_state() function
          const internalState = canonical.state
          let canonicalState
          if (internalState === 'idle' || internalState === 'success') {
            canonicalState = 'idle'
          } else if (internalState === 'stopped') {
            canonicalState = 'stopped'
          } else if (internalState === 'failed') {
            canonicalState = 'error'
          } else {
            // running_translator, running_analyzer, running_merger, starting, scheduled, retrying
            canonicalState = 'running'
          }
          
          // Track as recently started if transitioning to running
          if (canonicalState === 'running' && canonical.run_id) {
            recentlyStartedRunIdRef.current = canonical.run_id
            setTimeout(() => {
              if (recentlyStartedRunIdRef.current === canonical.run_id) {
                recentlyStartedRunIdRef.current = null
              }
            }, 30000)
          }
          
          updatePipelineStatus({
            state: canonicalState,
            run_id: canonical.run_id || event.run_id || activeRunId,
            current_stage: canonical.current_stage,
            started_at: canonical.started_at,
            updated_at: canonical.updated_at,
            error: canonical.error,
            retry_count: canonical.retry_count,
            metadata: canonical.metadata,
          }, 'websocket')
        } else {
          // Fallback to partial state (backward compatibility)
          const newState = event.data.new_state
          if (newState) {
            // Track as recently started if transitioning to running
            if (newState === 'running' && event.run_id) {
              recentlyStartedRunIdRef.current = event.run_id
              setTimeout(() => {
                if (recentlyStartedRunIdRef.current === event.run_id) {
                  recentlyStartedRunIdRef.current = null
                }
              }, 30000)
            }
            updatePipelineStatus({ state: newState, run_id: event.run_id || activeRunId }, 'websocket')
          }
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
  const handleStartPipeline = useCallback(async (manualOverride = false) => {
    console.log('[Pipeline] Start button clicked', { isStarting, isRunning: pipelineStatus.isRunning, state: pipelineStatus.state, manualOverride })
    
    if (isStarting || pipelineStatus.isRunning) {
      console.log('[Pipeline] Start blocked - already starting or running')
      return
    }
    
    setIsStarting(true)
    try {
      console.log('[Pipeline] Calling startPipeline API...', { manualOverride })
      const result = await startPipeline(manualOverride)
      console.log('[Pipeline] Start API response:', result)
      
      if (result && result.run_id) {
        console.log('[Pipeline] Pipeline started, run_id:', result.run_id)
        setActiveRunId(result.run_id)
        // Track this run as recently started (for 30 seconds) to prevent premature state reset
        recentlyStartedRunIdRef.current = result.run_id
        setTimeout(() => {
          // Clear the recently started flag after 30 seconds
          if (recentlyStartedRunIdRef.current === result.run_id) {
            recentlyStartedRunIdRef.current = null
          }
        }, 30000)
        
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
        
        // CRITICAL: Don't reset isStarting here - keep it true until pipeline actually finishes
        // The button stays disabled via isRunning=true, and isStarting will be reset when
        // WebSocket events or polling confirm the pipeline has finished
        
        // Don't poll immediately - backend may not have updated state yet
        // WebSocket events will update state quickly, and regular polling will catch it
        // Poll after a short delay to avoid race condition
        setTimeout(() => {
          pollPipelineStatus()
        }, 1000)  // Wait 1 second before polling to let backend update
      } else {
        console.warn('[Pipeline] Start API returned no run_id:', result)
        setIsStarting(false)  // Only reset on error
        setAlertInfo({
          type: 'error',
          message: 'Pipeline start response missing run_id',
        })
      }
    } catch (error) {
      console.error('[Pipeline] Start failed:', error)
      setIsStarting(false)  // Reset on error
      
      // Check if this is a health override requirement
      const errorMsg = error.message || ''
      const requiresOverride = errorMsg.includes('requires override') || errorMsg.includes('health is unstable') || errorMsg.includes('health is degraded')
      
      if (requiresOverride && !manualOverride) {
        // Prompt user to override
        const shouldOverride = window.confirm(
          'Pipeline health is unstable/degraded. Recent runs have failed.\n\n' +
          'Do you want to override the health check and run anyway?\n\n' +
          'Click OK to override and run, or Cancel to abort.'
        )
        
        if (shouldOverride) {
          // Retry with override
          return handleStartPipeline(true)
        } else {
          setAlertInfo({
            type: 'warning',
            message: 'Pipeline start cancelled. Health check requires override.',
          })
        }
      } else {
        setAlertInfo({
          type: 'error',
          message: error.message || 'Failed to start pipeline',
        })
      }
    }
    // REMOVED: finally block that was resetting isStarting too early
    // isStarting will be reset when updatePipelineStatus detects pipeline has finished
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
    isStarting,
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
