import { useReducer, useEffect, useRef, useCallback, useMemo } from 'react'
import { websocketManager } from '../services/websocketManager'
import * as pipelineManager from '../services/pipelineManager'
import { computeElapsed, syncElapsedFromBackend, formatElapsedTime } from '../utils/timeUtils'
import { start as startAppService } from '../services/appsManager'
import { isVerboseLogEvent } from '../utils/eventFilter'

// Action types - simplified and consolidated (reduced from 27 to 15)
export const ActionTypes = {
  // Pipeline lifecycle
  PIPELINE_STARTED: 'PIPELINE_STARTED',
  PIPELINE_STAGE_CHANGED: 'PIPELINE_STAGE_CHANGED',
  PIPELINE_EVENT: 'PIPELINE_EVENT',
  PIPELINE_COMPLETED: 'PIPELINE_COMPLETED', // Handles both success and failure
  RESET_PIPELINE: 'RESET_PIPELINE',
  
  // Export stage (consolidated: EXPORT_PROGRESS, EXPORT_COMPLETED, EXPORT_FAILED → EXPORT_UPDATE)
  EXPORT_UPDATE: 'EXPORT_UPDATE', // Handles progress, completed, failed
  
  // Data updates
  FILE_COUNTS_UPDATED: 'FILE_COUNTS_UPDATED',
  METRICS_UPDATED: 'METRICS_UPDATED',
  NEXT_SCHEDULED_RUN_UPDATED: 'NEXT_SCHEDULED_RUN_UPDATED',
  
  // UI state
  SET_ALERT: 'SET_ALERT',
  CLEAR_ALERT: 'CLEAR_ALERT',
  MERGER_UPDATE: 'MERGER_UPDATE', // Handles running and status (consolidated: SET_MERGER_RUNNING, SET_MERGER_STATUS)
  UPDATE_ELAPSED_TIME: 'UPDATE_ELAPSED_TIME',
}

// Initial state
const initialState = {
  // Pipeline state
  currentRunId: null,
  currentStage: 'idle',
  isRunning: false,
  stageStartTime: null,
  stageElapsedTime: 0,
  currentProcessingItem: '',
  
  // File counts
  fileCounts: { raw_files: 0, processed_files: 0, analyzed_files: 0 },
  
  // Metrics
  rowsPerMin: 0,
  nextScheduledRun: null,
  
  // Events
  events: [],
  
  // Export stage state
  export: {
    status: 'not_started', // not_started, active, complete, failed
    rows: 0,
    files: 0,
    percent: 0,
    instrument: '',
    dataType: '',
  },
  
  // Merger state
  mergerRunning: false,
  mergerStatus: null,
  
  // UI state
  alert: null,
}

// Reducer
function pipelineReducer(state, action) {
  switch (action.type) {
    case ActionTypes.PIPELINE_STARTED:
      // Only clear events if this is a NEW run (different run_id)
      // Don't clear if we're reconnecting to the same run
      const isNewRun = state.currentRunId !== action.payload.runId
      return {
        ...state,
        currentRunId: action.payload.runId,
        isRunning: true,
        currentStage: action.payload.stage || 'starting',
        stageStartTime: action.payload.startTime || Date.now(),
        stageElapsedTime: 0,
        currentProcessingItem: '',
        // Only clear events if this is a new run
        events: isNewRun ? [] : state.events,
        export: isNewRun ? {
          status: 'not_started',
          rows: 0,
          files: 0,
          percent: 0,
          instrument: '',
          dataType: '',
        } : state.export,
      }

    case ActionTypes.PIPELINE_STAGE_CHANGED:
      return {
        ...state,
        currentStage: action.payload.stage,
        stageStartTime: action.payload.startTime || Date.now(),
        stageElapsedTime: 0,
      }

    case ActionTypes.PIPELINE_EVENT:
      const event = action.payload
      const hasMessage = event.msg && event.msg.trim().length > 0
      const hasData = event.data && Object.keys(event.data).length > 0
      const isCriticalEvent = event.event === 'failure' || event.event === 'success' || event.event === 'start'
      
      // Skip blank events unless they're critical
      if (!hasMessage && !hasData && !isCriticalEvent) {
        return state
      }

      // Filter out verbose log events using utility function
      if (isVerboseLogEvent(event)) {
        return state
      }

      // Create a unique key for deduplication
      // Use timestamp, stage, event type, and message hash to identify duplicates
      const eventKey = `${event.timestamp || ''}-${event.stage || ''}-${event.event || ''}-${event.msg?.substring(0, 50) || ''}`
      
      // Check if this event already exists (prevent duplicates)
      const isDuplicate = state.events.some(existingEvent => {
        const existingKey = `${existingEvent.timestamp || ''}-${existingEvent.stage || ''}-${existingEvent.event || ''}-${existingEvent.msg?.substring(0, 50) || ''}`
        return existingKey === eventKey
      })
      
      // Skip if duplicate
      if (isDuplicate) {
        return state
      }

      const newEvents = [...state.events, event].slice(-100) // Keep last 100 events
      
      let newState = {
        ...state,
        events: newEvents,
      }

      // Update stage if changed
      if (event.stage && event.stage !== state.currentStage) {
        const eventTime = event.timestamp ? new Date(event.timestamp).getTime() : Date.now()
        newState = {
          ...newState,
          currentStage: event.stage,
          stageStartTime: eventTime,
          stageElapsedTime: 0,
        }
      } else if (event.stage === state.currentStage) {
        // If we're already in this stage, check for start event or use first event timestamp
        if (event.event === 'start' && event.timestamp) {
          const eventTime = new Date(event.timestamp).getTime()
          newState = {
            ...newState,
            stageStartTime: eventTime,
          }
        } else if (!newState.stageStartTime && event.timestamp) {
          const eventTime = new Date(event.timestamp).getTime()
          newState = {
            ...newState,
            stageStartTime: eventTime,
          }
        }
      }

      // Track current processing item
      if (event.stage === 'analyzer' && event.event === 'log') {
        const match = event.msg?.match(/Running analyzer for (\w+)/i)
        if (match) {
          newState.currentProcessingItem = match[1]
        }
      } else if (event.stage === 'analyzer' && event.data?.instrument) {
        newState.currentProcessingItem = event.data.instrument
      } else if (event.stage === 'translator' && event.event === 'log') {
        const match = event.msg?.match(/Processing (.+)/i)
        if (match) {
          newState.currentProcessingItem = match[1]
        }
      }

      // Handle progress updates from analyzer
      if (event.stage === 'analyzer' && event.event === 'metric' && event.data?.elapsed_minutes) {
        const elapsedMinutes = event.data.elapsed_minutes
        // Validate elapsed_minutes - should be reasonable (less than 24 hours = 1440 minutes)
        // If it's too large, it's likely a data error - ignore it
        if (elapsedMinutes > 0 && elapsedMinutes < 1440) {
          const elapsedSeconds = elapsedMinutes * 60
          const adjustedStartTime = syncElapsedFromBackend(elapsedMinutes)
          newState = {
            ...newState,
            stageElapsedTime: elapsedSeconds,
            stageStartTime: adjustedStartTime,
          }
        } else {
          // Log warning for debugging but don't update with invalid data
          console.warn(`Invalid elapsed_minutes from analyzer: ${elapsedMinutes} (ignoring)`)
        }
      }

      return newState

    case ActionTypes.EXPORT_UPDATE:
      // Consolidated export update - handles progress, completed, failed
      const { status, ...exportData } = action.payload
      return {
        ...state,
        export: {
          ...state.export,
          status: status || state.export.status,
          ...exportData,
        },
      }

    case ActionTypes.PIPELINE_COMPLETED:
      // Handles both success and failure
      return {
        ...state,
        isRunning: false,
      }

    case ActionTypes.FILE_COUNTS_UPDATED:
      return {
        ...state,
        fileCounts: action.payload,
      }

    case ActionTypes.NEXT_SCHEDULED_RUN_UPDATED:
      return {
        ...state,
        nextScheduledRun: action.payload,
      }

    case ActionTypes.METRICS_UPDATED:
      return {
        ...state,
        rowsPerMin: action.payload.rowsPerMin || state.rowsPerMin,
      }

    case ActionTypes.RESET_PIPELINE:
      return {
        ...state,
        currentRunId: null,
        currentStage: 'idle',
        isRunning: false,
        stageStartTime: null,
        stageElapsedTime: 0,
        currentProcessingItem: '',
        events: [],
        export: {
          status: 'not_started',
          rows: 0,
          files: 0,
          percent: 0,
          instrument: '',
          dataType: '',
        },
      }

    case ActionTypes.SET_ALERT:
      return {
        ...state,
        alert: action.payload,
      }

    case ActionTypes.CLEAR_ALERT:
      return {
        ...state,
        alert: null,
      }

    case ActionTypes.MERGER_UPDATE:
      // Consolidated merger update - handles both running and status
      return {
        ...state,
        mergerRunning: action.payload.running !== undefined ? action.payload.running : state.mergerRunning,
        mergerStatus: action.payload.status !== undefined ? action.payload.status : state.mergerStatus,
      }

    case ActionTypes.UPDATE_ELAPSED_TIME:
      return {
        ...state,
        stageElapsedTime: action.payload,
      }

    default:
      return state
  }
}

/**
 * Custom hook for pipeline state management
 */
export function usePipelineState() {
  const [state, dispatch] = useReducer(pipelineReducer, initialState)
  const isRunningRef = useRef(false)
  const currentRunIdRef = useRef(null)

  // Keep refs in sync with state
  useEffect(() => {
    isRunningRef.current = state.isRunning
  }, [state.isRunning])

  useEffect(() => {
    currentRunIdRef.current = state.currentRunId
  }, [state.currentRunId])

  // Helper function to show alerts
  const showAlert = useCallback((message, type) => {
    dispatch({ type: ActionTypes.SET_ALERT, payload: { message, type } })
    setTimeout(() => {
      dispatch({ type: ActionTypes.CLEAR_ALERT })
    }, 5000)
  }, [])

  // Load file counts
  const loadFileCounts = useCallback(async () => {
    const counts = await pipelineManager.getFileCounts()
    dispatch({ type: ActionTypes.FILE_COUNTS_UPDATED, payload: counts })
  }, [])

  // Load next scheduled run
  const loadNextScheduledRun = useCallback(async () => {
    const nextRun = await pipelineManager.getNextScheduledRun()
    if (nextRun) {
      dispatch({ type: ActionTypes.NEXT_SCHEDULED_RUN_UPDATED, payload: nextRun })
    }
  }, [])

  // Handle WebSocket events
  const handleWebSocketEvent = useCallback((event) => {
    console.log('[DEBUG] handleEvent:', {
      stage: event.stage,
      event: event.event,
      timestamp: event.timestamp,
      msg: event.msg?.substring(0, 50),
    })

    // Dispatch event - reducer will handle state updates
    dispatch({ type: ActionTypes.PIPELINE_EVENT, payload: event })

    // Handle export stage events (consolidated)
    if (event.stage === 'export') {
      let status = 'active'
      if (event.event === 'start') {
        status = 'active'
      } else if (event.event === 'success') {
        status = 'complete'
      } else if (event.event === 'failure') {
        status = 'failed'
        showAlert(event.msg || 'Export failed', 'error')
      }
      
      const payload = {
        status,
        instrument: event.data?.instrument || '',
        dataType: event.data?.dataType || '',
      }
      
      if (event.event === 'metric' && event.data) {
        if (event.data.totalBarsProcessed !== undefined) payload.rows = event.data.totalBarsProcessed
        if (event.data.fileCount !== undefined) payload.files = event.data.fileCount
        if (event.data.fileSizeMB !== undefined) payload.percent = Math.min(100, (event.data.fileSizeMB / 100) * 100)
      } else if (event.event === 'success') {
        payload.rows = event.data?.totalBarsProcessed || 0
        payload.files = event.data?.fileCount || 0
        payload.percent = 100
      }
      
      dispatch({ type: ActionTypes.EXPORT_UPDATE, payload })
    }

    // Update metrics
    if (event.event === 'metric' && event.data) {
      if (event.data.processed_file_count !== undefined || event.data.deleted_file_count !== undefined) {
        loadFileCounts()
      }
      if (event.data.rows_per_min !== undefined) {
        dispatch({
          type: ActionTypes.METRICS_UPDATED,
          payload: { rowsPerMin: event.data.rows_per_min },
        })
      }
    }

    // Refresh file counts when stages complete
    if ((event.event === 'success' || event.event === 'failure')) {
      if (event.stage === 'translator' || event.stage === 'analyzer') {
        loadFileCounts()
      }
    }

    // Handle failures
    if (event.event === 'failure' && event.stage !== 'export') {
      showAlert(event.msg || 'Pipeline stage failed', 'error')
    }

    // Handle completion
    if (event.event === 'success' && event.stage === 'audit') {
      dispatch({ type: ActionTypes.PIPELINE_COMPLETED })
      showAlert('Pipeline completed successfully', 'success')
    }

    // Handle single stage completion - check status after delay
    // Only update stage if changed, don't clear events
    if ((event.event === 'success' || event.event === 'failure') &&
        (event.stage === 'translator' || event.stage === 'analyzer')) {
      // Use setTimeout to avoid circular dependency
      setTimeout(() => {
        checkPipelineStatus()
      }, 2000)
    }
  }, [showAlert, loadFileCounts, state.currentStage])

  // Check pipeline status
  const checkPipelineStatus = useCallback(async () => {
    const status = await pipelineManager.getPipelineStatus()
    if (status.active) {
      if (!state.currentRunId || state.currentRunId !== status.run_id) {
        // New run - clear events
        dispatch({
          type: ActionTypes.PIPELINE_STARTED,
          payload: { runId: status.run_id, startTime: null, stage: status.stage },
        })
        websocketManager.connect(
          status.run_id,
          handleWebSocketEvent,
          () => isRunningRef.current && currentRunIdRef.current === status.run_id
        )
      } else if (status.stage !== state.currentStage) {
        // Same run, different stage - don't clear events
        dispatch({ type: ActionTypes.PIPELINE_STAGE_CHANGED, payload: { stage: status.stage } })
      } else if (!websocketManager.isConnected()) {
        // Same run, same stage, just reconnecting - don't clear events
        websocketManager.connect(
          status.run_id,
          handleWebSocketEvent,
          () => isRunningRef.current && currentRunIdRef.current === status.run_id
        )
      }
      // If same run, same stage, and connected - do nothing (don't clear events)
    } else {
      // Pipeline is not active - only reset if we think it's running
      // This prevents clearing events if status check temporarily fails
      if (state.isRunning && !websocketManager.isConnected()) {
        // Only reset if we're sure it's done (no active connection)
        dispatch({ type: ActionTypes.RESET_PIPELINE })
        websocketManager.disconnect()
      }
    }
  }, [state.currentRunId, state.currentStage, state.isRunning, handleWebSocketEvent])


  // Start pipeline
  const startPipeline = useCallback(async () => {
    try {
      const data = await pipelineManager.startPipeline()
      dispatch({
        type: ActionTypes.PIPELINE_STARTED,
        payload: { runId: data.run_id, startTime: Date.now() },
      })
      websocketManager.connect(
        data.run_id,
        handleWebSocketEvent,
        () => isRunningRef.current && currentRunIdRef.current === data.run_id
      )
      showAlert('Pipeline started', 'success')
    } catch (error) {
      showAlert('Failed to start pipeline', 'error')
    }
  }, [handleWebSocketEvent, showAlert])

  // Run stage
  const runStage = useCallback(async (stageName) => {
    try {
      const data = await pipelineManager.startStage(stageName)
      dispatch({
        type: ActionTypes.PIPELINE_STARTED,
        payload: { runId: data.run_id, startTime: Date.now() },
      })
      dispatch({ type: ActionTypes.PIPELINE_STAGE_CHANGED, payload: { stage: stageName } })
      websocketManager.connect(
        data.run_id,
        handleWebSocketEvent,
        () => isRunningRef.current && currentRunIdRef.current === data.run_id
      )
      showAlert(`${stageName.charAt(0).toUpperCase() + stageName.slice(1)} stage started`, 'success')
    } catch (error) {
      showAlert(`Failed to start ${stageName} stage`, 'error')
    }
  }, [handleWebSocketEvent, showAlert])

  // Run data merger
  const runDataMerger = useCallback(async () => {
    dispatch({ type: ActionTypes.MERGER_UPDATE, payload: { running: true, status: null } })
    try {
      const data = await pipelineManager.runMerger()
      dispatch({ type: ActionTypes.MERGER_UPDATE, payload: { running: false, status: data } })
      if (data.status === 'success') {
        showAlert('Data merger completed successfully', 'success')
        loadFileCounts()
      } else {
        showAlert(`Data merger ${data.status}: ${data.message}`, 'error')
      }
    } catch (error) {
      showAlert('Failed to run data merger', 'error')
      dispatch({ type: ActionTypes.MERGER_UPDATE, payload: { running: false, status: { status: 'error', message: error.message } } })
    }
  }, [showAlert, loadFileCounts])

  // Reset pipeline state
  const resetPipelineState = useCallback(() => {
    dispatch({ type: ActionTypes.RESET_PIPELINE })
    websocketManager.disconnect()
    checkPipelineStatus()
    showAlert('Pipeline state reset', 'success')
  }, [checkPipelineStatus, showAlert])

  // Update elapsed time every second when running
  useEffect(() => {
    if (!state.isRunning || !state.stageStartTime) return

    const interval = setInterval(() => {
      const elapsed = computeElapsed(state.stageStartTime)
      if (elapsed !== state.stageElapsedTime) {
        dispatch({ type: ActionTypes.UPDATE_ELAPSED_TIME, payload: elapsed })
      }
    }, 1000)

    return () => clearInterval(interval)
  }, [state.isRunning, state.stageStartTime, state.stageElapsedTime])

  // Load initial data and set up polling
  useEffect(() => {
    loadNextScheduledRun()
    loadFileCounts()
    checkPipelineStatus()

    // Reduced polling - WebSocket handles real-time updates
    // Poll file counts every 30 seconds (WebSocket updates on stage completion)
    const fileCountInterval = setInterval(loadFileCounts, 30000)

    // Poll pipeline status every 30 seconds (WebSocket handles active runs)
    const statusInterval = setInterval(checkPipelineStatus, 30000)

    // Update next scheduled run every 15 seconds (doesn't change often)
    const nextRunInterval = setInterval(loadNextScheduledRun, 15000)

    return () => {
      clearInterval(fileCountInterval)
      clearInterval(statusInterval)
      clearInterval(nextRunInterval)
      websocketManager.disconnect()
    }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  // Clear alert
  const clearAlert = useCallback(() => {
    dispatch({ type: ActionTypes.CLEAR_ALERT })
  }, [])

  // View-model transformations
  const pipelineStatus = useMemo(() => ({
    runId: state.currentRunId ? state.currentRunId.substring(0, 8) + '...' : 'None',
    isRunning: state.isRunning,
  }), [state.currentRunId, state.isRunning])

  const stageInfo = useMemo(() => {
    // Calculate elapsed time, but cap it at reasonable values
    let elapsed = state.isRunning && state.stageStartTime
      ? computeElapsed(state.stageStartTime)
      : state.stageElapsedTime
    
    // Safety check: cap elapsed time at 24 hours (86400 seconds)
    // If it's larger, reset to 0 to prevent display issues
    if (elapsed > 86400) {
      console.warn(`Elapsed time too large: ${elapsed}s, resetting to 0`)
      elapsed = 0
    }
    
    return {
      stage: state.currentStage,
      stageLabel: state.currentStage.charAt(0).toUpperCase() + state.currentStage.slice(1),
      elapsedLabel: formatElapsedTime(elapsed),
      elapsedSeconds: elapsed,
      isActive: state.isRunning,
      processingItem: state.currentProcessingItem,
    }
  }, [state.currentStage, state.isRunning, state.stageStartTime, state.stageElapsedTime, state.currentProcessingItem])

  const exportInfo = useMemo(() => {
    const label = state.export.instrument
      ? `${state.export.instrument}${state.export.dataType ? ` (${state.export.dataType})` : ''}`
      : 'Unknown'
    return {
      status: state.export.status,
      percent: state.export.percent,
      rows: state.export.rows,
      files: state.export.files,
      instrument: state.export.instrument || 'Unknown',
      dataType: state.export.dataType,
      label,
    }
  }, [state.export])

  const metrics = useMemo(() => ({
    rawFiles: state.fileCounts.raw_files === -1 ? 'Error' : (state.fileCounts.raw_files || 0),
    processedFiles: state.fileCounts.processed_files === -1 ? 'Error' : (state.fileCounts.processed_files ?? 0),
    analyzedFiles: state.fileCounts.analyzed_files === -1 ? 'Error' : (state.fileCounts.analyzed_files ?? 0),
    rowsPerMin: state.rowsPerMin,
  }), [state.fileCounts, state.rowsPerMin])

  const scheduleInfo = useMemo(() => {
    if (!state.nextScheduledRun) return null
    return {
      nextRunTime: state.nextScheduledRun.next_run_time_short,
      waitDisplay: state.nextScheduledRun.wait_display || 
        `${state.nextScheduledRun.wait_minutes_int || 0} min ${state.nextScheduledRun.wait_seconds_remaining || 0} sec`,
      countdown: state.nextScheduledRun.wait_display,
    }
  }, [state.nextScheduledRun])

  const eventsFormatted = useMemo(() => {
    return state.events.map(event => {
      const severityClass = event.event === 'failure'
        ? 'bg-red-900/30 border-l-2 border-red-500'
        : event.event === 'success'
        ? 'bg-green-900/30 border-l-2 border-green-500'
        : event.event === 'start'
        ? 'bg-blue-900/30 border-l-2 border-blue-500'
        : event.event === 'metric'
        ? 'bg-gray-800/50 border-l-2 border-gray-500'
        : 'bg-gray-800 border-l-2 border-gray-600'
      
      const formattedTimestamp = event.timestamp
        ? new Date(event.timestamp).toLocaleTimeString()
        : ''

      // Only show data summary for important events, and make it cleaner
      let dataSummary = null
      if (event.data && Object.keys(event.data).length > 0) {
        // Only show key metrics, not full data dumps
        if (event.event === 'metric') {
          const keyFields = ['raw_file_count', 'processed_file_count', 'deleted_file_count', 'rows_per_min', 'totalBarsProcessed', 'fileCount']
          const filteredData = Object.fromEntries(
            Object.entries(event.data).filter(([key]) => keyFields.includes(key))
          )
          if (Object.keys(filteredData).length > 0) {
            dataSummary = JSON.stringify(filteredData)
          }
        } else if (event.event === 'failure' || event.event === 'success') {
          // Show data for failures/successes
          dataSummary = JSON.stringify(event.data)
        }
      }

      // Clean up message - truncate very long messages
      let message = event.msg || ''
      if (message.length > 150) {
        message = message.substring(0, 150) + '...'
      }

      return {
        ...event,
        formattedTimestamp,
        severityClass,
        message,
        dataSummary,
      }
    })
  }, [state.events])

  const mergerInfo = useMemo(() => ({
    isRunning: state.mergerRunning,
    status: state.mergerStatus?.status || null,
    message: state.mergerStatus?.message || null,
    displayText: state.mergerStatus
      ? (state.mergerStatus.status === 'success' ? '✓ Ready' : '✗ Error')
      : state.mergerRunning
      ? 'Running...'
      : 'Ready',
    displayClass: state.mergerStatus
      ? (state.mergerStatus.status === 'success' ? 'text-green-400' : 'text-red-400')
      : state.mergerRunning
      ? 'text-gray-400'
      : 'text-gray-300',
  }), [state.mergerRunning, state.mergerStatus])

  const alertInfo = useMemo(() => state.alert, [state.alert])

  // Start app using appsManager
  const startApp = useCallback(async (appName) => {
    try {
      await startAppService(appName)
    } catch (error) {
      console.error(`Failed to start ${appName} app:`, error)
    }
  }, [])

  return {
    // State (view-model)
    pipelineStatus,
    stageInfo,
    exportInfo,
    metrics,
    scheduleInfo,
    eventsFormatted,
    mergerInfo,
    alertInfo,
    
    // Actions
    startPipeline,
    runStage,
    runDataMerger,
    startApp,
    resetPipelineState,
    clearAlert,
  }
}

