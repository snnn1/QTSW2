/**
 * Pipeline API service and React hook
 * Single, consistent contract with backend
 */

import { useState, useEffect, useCallback, useRef } from 'react'
import { websocketManager } from '../services/websocketManager'
import { start as startAppService } from '../services/appsManager'
import { formatEventTimestamp } from '../utils/timeUtils'

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
  const res = await fetchWithTimeout(
    `${API_BASE}/pipeline/start`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ manual: true }),
    },
    15000
  )

  if (!res.ok) {
    throw new Error(await res.text())
  }

  return await safeJson(res)
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
  const updatePipelineStatus = useCallback((statusData) => {
    if (!statusData || statusData.state === 'unavailable') {
      setPipelineStatus({ state: 'idle', isRunning: false, runId: null })
      setActiveRunId(null)
      return
    }

    const state = String(statusData.state || 'idle').toLowerCase()
    const isRunning = 
      state === 'starting' ||
      state === 'running' ||
      state.startsWith('running_') ||
      statusData.active === true
    
    const runId = statusData.run_id || statusData.runId || null
    
    setPipelineStatus({
      state,
      isRunning,
      runId,
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

  // Consolidated polling strategy:
  // - When running: Use WebSocket for events, poll status every 10s as backup
  // - When idle: Poll status + metadata every 10s
  useEffect(() => {
    // Initial load
    pollPipelineStatus()
    loadMetadata()
    
    if (pipelineStatus.isRunning) {
      // When running: poll status every 10s as backup (WebSocket is primary)
      statusPollingRef.current = setInterval(pollPipelineStatus, 10000)
    } else {
      // When idle: poll status + metadata every 10s
      statusPollingRef.current = setInterval(() => {
        pollPipelineStatus()
        loadMetadata()
      }, 10000)
    }
    
    return () => {
      if (statusPollingRef.current) {
        clearInterval(statusPollingRef.current)
        statusPollingRef.current = null
      }
    }
  }, [pipelineStatus.isRunning, pollPipelineStatus, loadMetadata])

  // WebSocket connection for events
  // - When pipeline is running: connect to specific run_id
  // - When pipeline is idle: connect to all events (null run_id) to receive scheduler events
  // Use refs to track connection state and prevent unnecessary reconnects
  const wsRunIdRef = useRef(null)
  const wsConnectedRef = useRef(false)
  const snapshotReceivedRef = useRef(false)
  
  useEffect(() => {
    const handleEvent = (event) => {
      // Filtering rules:
      // - Always allow scheduler events (stage === 'scheduler')
      // - Always allow pipeline events (stage === 'pipeline')
      // - When activeRunId is set, also allow events for that run_id
      // - When activeRunId is null, allow all events
      const isSchedulerEvent = event.stage === 'scheduler'
      const isPipelineEvent = event.stage === 'pipeline'
      const isActiveRunEvent = activeRunId && event.run_id === activeRunId
      const isAllEventsMode = activeRunId === null
      
      if (!isSchedulerEvent && !isPipelineEvent && !isActiveRunEvent && !isAllEventsMode) {
        // Skip events that don't match any criteria
        return
      }
      
      // Format timestamp for display
      const formattedEvent = {
        ...event,
        formattedTimestamp: event.timestamp ? formatEventTimestamp(event.timestamp) : ''
      }
      
      // Handle snapshot events (1-hour historical events sent on connect)
      // REPLACE existing events with snapshot (fresh start on connect)
      // Events are already sorted chronologically by the backend
      if (event.type === 'snapshot' && Array.isArray(event.events)) {
        const formattedSnapshot = event.events.map(e => ({
          ...e,
          formattedTimestamp: e.timestamp ? formatEventTimestamp(e.timestamp) : ''
        }))
        
        // Deduplicate snapshot events using stable key
        const seen = new Set()
        const deduplicated = formattedSnapshot.filter(e => {
          // Stable deduplication key: run_id|stage|event|timestamp|data_hash
          // Sort data keys to ensure consistent hashing
          const dataStr = e.data ? JSON.stringify(e.data, Object.keys(e.data).sort()) : '{}'
          const key = `${e.run_id || 'null'}|${e.stage || 'null'}|${e.event || 'null'}|${e.timestamp || 'null'}|${dataStr}`
          if (seen.has(key)) {
            return false
          }
          seen.add(key)
          return true
        })
        
        // Sort by timestamp (parse ISO timestamps for proper sorting)
        deduplicated.sort((a, b) => {
          const tsA = a.timestamp ? new Date(a.timestamp).getTime() : 0
          const tsB = b.timestamp ? new Date(b.timestamp).getTime() : 0
          return tsA - tsB
        })
        
        setEventsFormatted(deduplicated)
        snapshotReceivedRef.current = true
        console.log(`[Events] Snapshot loaded: ${deduplicated.length} events`)
        return
      }
      
      setEventsFormatted(prev => {
        // Stable deduplication key: run_id|stage|event|timestamp|data_hash
        // Sort data keys to ensure consistent hashing
        const dataStr = event.data ? JSON.stringify(event.data, Object.keys(event.data).sort()) : '{}'
        const eventKey = `${event.run_id || 'null'}|${event.stage || 'null'}|${event.event || 'null'}|${event.timestamp || 'null'}|${dataStr}`
        
        // Check if event already exists
        const exists = prev.some(e => {
          const eDataStr = e.data ? JSON.stringify(e.data, Object.keys(e.data).sort()) : '{}'
          const existingKey = `${e.run_id || 'null'}|${e.stage || 'null'}|${e.event || 'null'}|${e.timestamp || 'null'}|${eDataStr}`
          return existingKey === eventKey
        })
        
        if (exists) return prev
        
        // Add new event and sort by timestamp (parse ISO timestamps for proper sorting)
        const updated = [...prev, formattedEvent].sort((a, b) => {
          const tsA = a.timestamp ? new Date(a.timestamp).getTime() : 0
          const tsB = b.timestamp ? new Date(b.timestamp).getTime() : 0
          return tsA - tsB
        })
        
        return updated
      })
      
      // Update metrics/stage info from events
      // Note: EventBus filters out 'metric' events, so this will rarely trigger
      // But we keep it for completeness in case snapshot includes metrics
      if (event.event === 'metric' && event.data) {
        setMetrics(prev => ({ ...prev, ...event.data }))
      }
      if (event.stage && event.event === 'start') {
        setStageInfo(prev => ({ ...prev, name: event.stage }))
      }
      
      // Update status from state_change events
      // Allow state changes for active run OR scheduler/pipeline events
      if (event.event === 'state_change' && event.data) {
        const shouldUpdate = isPipelineEvent || isSchedulerEvent || (activeRunId && event.run_id === activeRunId)
        if (shouldUpdate) {
          const newState = event.data.new_state
          if (newState) {
            updatePipelineStatus({ state: newState, run_id: event.run_id || activeRunId })
          }
        }
      }
    }
    
    // Determine target run_id for connection
    const targetRunId = (activeRunId && pipelineStatus.isRunning) ? activeRunId : null
    
    // Check current connection state
    const isConnected = websocketManager.isConnected()
    const currentRunId = websocketManager.getRunId()
    const runIdChanged = wsRunIdRef.current !== targetRunId
    
    // Always connect on initial mount (when wsRunIdRef.current is null)
    // Or reconnect if:
    // 1. run_id changed (activeRunId changed or pipeline started/stopped)
    // 2. Connection dropped (not connected but we had a connection before)
    const isInitialConnection = wsRunIdRef.current === null
    const needsReconnect = isInitialConnection || (runIdChanged && isConnected) || (!isConnected && wsRunIdRef.current !== null)
    
    if (needsReconnect) {
      // Disconnect existing connection if run_id changed (but not on initial connection)
      if (!isInitialConnection && runIdChanged && isConnected) {
        console.log(`[Events] Disconnecting due to runId change: ${currentRunId} -> ${targetRunId}`)
        websocketManager.disconnect()
      }
      
      // Connect to new run_id (or null for all events)
      // websocketManager.connect() handles waiting for previous connection to close
      console.log(`[Events] Connecting WebSocket (initial: ${isInitialConnection}, runId: ${targetRunId})`)
      websocketManager.connect(
        targetRunId, // null = all events (scheduler), specific run_id = that run only
        handleEvent,
        true // Allow reconnect on connection drop
      )
      
      wsRunIdRef.current = targetRunId
      snapshotReceivedRef.current = false // Reset snapshot flag on (re)connect
      
      if (isInitialConnection) {
        console.log(`[Events] Initial connection established, waiting for snapshot...`)
      }
    }
    
    // Update connection status
    wsConnectedRef.current = websocketManager.isConnected()
    
    return () => {
      // Only disconnect on unmount - websocketManager handles reconnects on connection drop
      // Don't disconnect on every effect re-run
    }
  }, [activeRunId, pipelineStatus.isRunning]) // Removed updatePipelineStatus from deps - it's stable

  // Start pipeline
  const handleStartPipeline = useCallback(async () => {
    if (isStarting || pipelineStatus.isRunning) {
      return
    }
    
    setIsStarting(true)
    try {
      const result = await startPipeline()
      
      if (result.run_id) {
        setActiveRunId(result.run_id)
        setAlertInfo({
          type: 'success',
          message: 'Pipeline started successfully',
        })
        
        // Start polling immediately
        setTimeout(pollPipelineStatus, 1000)
      }
    } catch (error) {
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
      
      // Refresh status
      setTimeout(pollPipelineStatus, 500)
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
      
      // Backend always returns { enabled: true/false, status: "...", message: "..." }
      setSchedulerEnabled(result.enabled)
      setAlertInfo({
        type: 'success',
        message: result.message,
      })
    } catch (error) {
      setAlertInfo({
        type: 'error',
        message: error.message || 'Failed to toggle scheduler',
      })
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
      setTimeout(pollPipelineStatus, 1000)
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
      setTimeout(pollPipelineStatus, 1000)
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
  }
}
