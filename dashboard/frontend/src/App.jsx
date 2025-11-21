import { useState, useEffect, useRef } from 'react'
import './App.css'

const API_BASE = 'http://localhost:8000/api'
const WS_BASE = 'ws://localhost:8000/ws'

function App() {
  const [scheduleTime, setScheduleTime] = useState('07:30')
  const [currentRunId, setCurrentRunId] = useState(null)
  const [currentStage, setCurrentStage] = useState('idle')
  const [fileCounts, setFileCounts] = useState({ raw_files: 0, processed_files: 0, analyzed_files: 0 })
  const [rowsPerMin, setRowsPerMin] = useState(0)
  const [events, setEvents] = useState([])
  const [alert, setAlert] = useState(null)
  const [isRunning, setIsRunning] = useState(false)
  const [chicagoTime, setChicagoTime] = useState('')
  const [stageStartTime, setStageStartTime] = useState(null)
  const [stageElapsedTime, setStageElapsedTime] = useState(0)
  const [currentProcessingItem, setCurrentProcessingItem] = useState('')
  
  // Export stage state
  const [exportStatus, setExportStatus] = useState('not_started') // not_started, active, complete, failed
  const [exportRows, setExportRows] = useState(0)
  const [exportFiles, setExportFiles] = useState(0)
  const [exportPercent, setExportPercent] = useState(0)
  const [exportInstrument, setExportInstrument] = useState('')
  const [exportDataType, setExportDataType] = useState('')
  const [mergerRunning, setMergerRunning] = useState(false)
  const [mergerStatus, setMergerStatus] = useState(null)
  
  const wsRef = useRef(null)
  const reconnectTimeoutRef = useRef(null)
  const isRunningRef = useRef(false)
  const currentRunIdRef = useRef(null)
  
  // Keep refs in sync with state
  useEffect(() => {
    isRunningRef.current = isRunning
  }, [isRunning])
  
  useEffect(() => {
    currentRunIdRef.current = currentRunId
  }, [currentRunId])
  
  // Update Chicago time every second
  useEffect(() => {
    const updateChicagoTime = () => {
      const now = new Date()
      const chicagoTimeStr = now.toLocaleString('en-US', {
        timeZone: 'America/Chicago',
        hour12: false,
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit'
      })
      setChicagoTime(chicagoTimeStr)
    }
    
    updateChicagoTime() // Initial update
    const timeInterval = setInterval(updateChicagoTime, 1000) // Update every second
    
    return () => clearInterval(timeInterval)
  }, [])

  // Load schedule on mount (don't auto-start pipeline)
  useEffect(() => {
    loadSchedule()
    loadFileCounts()
    // Only check status, don't auto-start - user must click "Run Now" button
    checkPipelineStatus()
    
    // Poll file counts every 5 seconds
    const fileCountInterval = setInterval(loadFileCounts, 5000)
    
    // Poll pipeline status every 10 seconds to detect completion
    const statusInterval = setInterval(checkPipelineStatus, 10000)
    
    // Update elapsed time every second when running
    const elapsedInterval = setInterval(() => {
      if (isRunning) {
        if (stageStartTime) {
          const elapsed = Math.floor((Date.now() - stageStartTime) / 1000)
          // Only update if elapsed is positive (prevents showing 0s when timer hasn't started)
          if (elapsed >= 0) {
            if (elapsed !== stageElapsedTime) {
              console.log('[DEBUG] Timer update:', {
                elapsed,
                stageStartTime: new Date(stageStartTime).toISOString(),
                currentStage,
                now: new Date().toISOString()
              })
            }
            setStageElapsedTime(elapsed)
          }
        } else if (currentStage !== 'idle') {
          // If we're running but don't have a start time, initialize it now
          // This handles cases where we reconnect mid-run and don't get historical events
          console.log('[DEBUG] Initializing stageStartTime from timer interval (no start time, stage:', currentStage, ')')
          setStageStartTime(Date.now())
          setStageElapsedTime(0)
        } else {
          console.log('[DEBUG] Timer interval: isRunning but no stageStartTime and stage is idle')
        }
      }
    }, 1000)
    
    return () => {
      clearInterval(fileCountInterval)
      clearInterval(statusInterval)
      clearInterval(elapsedInterval)
      if (reconnectTimeoutRef.current) {
        clearTimeout(reconnectTimeoutRef.current)
      }
      disconnectWebSocket()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const loadSchedule = async () => {
    try {
      const response = await fetch(`${API_BASE}/schedule`)
      if (!response.ok) {
        console.error(`Failed to load schedule: ${response.status} ${response.statusText}`)
        return
      }
      const data = await response.json()
      setScheduleTime(data.schedule_time)
    } catch (error) {
      console.error('Failed to load schedule:', error)
      showAlert('Failed to connect to backend. Is the backend running?', 'error')
    }
  }

  const updateSchedule = async (newTime) => {
    try {
      const response = await fetch(`${API_BASE}/schedule`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ schedule_time: newTime })
      })
      const data = await response.json()
      setScheduleTime(data.schedule_time)
      showAlert('Schedule updated successfully', 'success')
    } catch (error) {
      showAlert('Failed to update schedule', 'error')
    }
  }

  const loadFileCounts = async () => {
    try {
      const response = await fetch(`${API_BASE}/metrics/files`)
      if (!response.ok) {
        console.error(`Failed to load file counts: ${response.status} ${response.statusText}`)
        setFileCounts({ raw_files: -1, processed_files: -1, analyzed_files: -1 })
        return
      }
      const data = await response.json()
      console.log('File counts loaded:', data)
      console.log('Setting processed_files to:', data.processed_files)
      setFileCounts(data)
    } catch (error) {
      console.error('Failed to load file counts:', error)
      // Show error in UI
      setFileCounts({ raw_files: -1, processed_files: -1, analyzed_files: -1 })
    }
  }

  const checkPipelineStatus = async () => {
    try {
      const response = await fetch(`${API_BASE}/pipeline/status`)
      const data = await response.json()
      // Only connect to existing runs, don't auto-start new ones
      if (data.active) {
        console.log('[DEBUG] checkPipelineStatus - active run:', {
          run_id: data.run_id,
          stage: data.stage,
          currentRunId,
          currentStage,
          stageStartTime: stageStartTime ? new Date(stageStartTime).toISOString() : null
        })
        // Only update if we don't already have this run_id, or if stage changed
        if (!currentRunId || currentRunId !== data.run_id) {
          console.log('[DEBUG] New run detected, connecting WebSocket')
          setCurrentRunId(data.run_id)
          setCurrentStage(data.stage)
          setIsRunning(true)
          // Don't set stageStartTime here - let events set it with actual timestamps
          // This prevents resetting the timer when reconnecting to an already-running stage
          setStageElapsedTime(0)
          connectWebSocket(data.run_id)
        } else if (data.stage !== currentStage) {
          console.log('[DEBUG] Stage changed in status check:', currentStage, '->', data.stage)
          setCurrentStage(data.stage)
          // Don't reset timer here either - let events set it with actual timestamps
          setStageElapsedTime(0)
        }
      } else {
        // Reset state if no active run
        if (isRunning) {
          setIsRunning(false)
          setCurrentRunId(null)
          setCurrentStage('idle')
          disconnectWebSocket()
        }
      }
    } catch (error) {
      console.error('Failed to check pipeline status:', error)
      // On error, reset to safe state
      setIsRunning(false)
      setCurrentRunId(null)
      setCurrentStage('idle')
    }
  }

  const resetPipelineState = () => {
    setIsRunning(false)
    setCurrentRunId(null)
    setCurrentStage('idle')
    setEvents([])
    disconnectWebSocket()
    checkPipelineStatus() // Re-check to confirm
    showAlert('Pipeline state reset', 'success')
  }

  const startPipeline = async () => {
    try {
      const response = await fetch(`${API_BASE}/pipeline/start`, {
        method: 'POST'
      })
      const data = await response.json()
      setCurrentRunId(data.run_id)
      setIsRunning(true)
      setCurrentStage('starting')
      setStageStartTime(Date.now())
      setStageElapsedTime(0)
      setCurrentProcessingItem('')
      setEvents([])
      
      // Reset export stage state
      setExportStatus('not_started')
      setExportRows(0)
      setExportFiles(0)
      setExportPercent(0)
      setExportInstrument('')
      setExportDataType('')
      
      connectWebSocket(data.run_id)
      showAlert('Pipeline started', 'success')
    } catch (error) {
      showAlert('Failed to start pipeline', 'error')
    }
  }

  const runStage = async (stageName) => {
    try {
      const response = await fetch(`${API_BASE}/pipeline/stage/${stageName}`, {
        method: 'POST'
      })
      const data = await response.json()
      setCurrentRunId(data.run_id)
      setIsRunning(true)
      setCurrentStage(stageName)
      setStageStartTime(Date.now())
      setStageElapsedTime(0)
      setCurrentProcessingItem('')
      setEvents([])
      
      // Reset export stage state
      setExportStatus('not_started')
      setExportRows(0)
      setExportFiles(0)
      setExportPercent(0)
      setExportInstrument('')
      setExportDataType('')
      
      connectWebSocket(data.run_id)
      showAlert(`${stageName.charAt(0).toUpperCase() + stageName.slice(1)} stage started`, 'success')
    } catch (error) {
      showAlert(`Failed to start ${stageName} stage`, 'error')
    }
  }

  const runDataMerger = async () => {
    setMergerRunning(true)
    setMergerStatus(null)
    try {
      const response = await fetch(`${API_BASE}/merger/run`, {
        method: 'POST'
      })
      const data = await response.json()
      setMergerStatus(data)
      
      if (data.status === 'success') {
        showAlert('Data merger completed successfully', 'success')
        loadFileCounts() // Refresh file counts
      } else {
        showAlert(`Data merger ${data.status}: ${data.message}`, 'error')
      }
    } catch (error) {
      showAlert('Failed to run data merger', 'error')
      setMergerStatus({ status: 'error', message: error.message })
    } finally {
      setMergerRunning(false)
    }
  }

  const connectWebSocket = (runId) => {
    disconnectWebSocket()
    
    if (!runId) {
      console.warn('No run ID provided for WebSocket connection')
      return
    }
    
    // Don't connect if we're already connected to this run_id
    if (wsRef.current && wsRef.current.url.includes(runId)) {
      console.log('Already connected to this run_id')
      return
    }
    
    const wsUrl = `${WS_BASE}/events/${runId}`
    const ws = new WebSocket(wsUrl)
    
    ws.onopen = () => {
      console.log('WebSocket connected to:', wsUrl)
      setEvents(prev => [...prev, {
        timestamp: new Date().toISOString(),
        stage: 'system',
        event: 'log',
        msg: 'WebSocket connected'
      }])
    }
    
    ws.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data)
        console.log('WebSocket message received:', data)
        handleEvent(data)
      } catch (error) {
        console.error('Failed to parse WebSocket message:', error, event.data)
      }
    }
    
    ws.onerror = (error) => {
      console.error('WebSocket error:', error)
      setEvents(prev => [...prev, {
        timestamp: new Date().toISOString(),
        stage: 'system',
        event: 'error',
        msg: `WebSocket error: ${error.message || 'Connection failed'}`
      }])
    }
    
    ws.onclose = (event) => {
      console.log('WebSocket disconnected:', event.code, event.reason)
      // Code 1005 means "No Status Received" - connection closed without proper close frame
      // This can happen when:
      // - Server restarts/reloads (common in development)
      // - Network issues
      // - Backend crashes
      // - Connection times out
      if (event.code === 1005) {
        // This is usually not a critical error - just log it
        console.log('WebSocket closed without status (1005) - likely server restart or network issue')
        // If we have an active run, try to reconnect after a short delay
        if (isRunningRef.current && currentRunIdRef.current) {
          console.log('Attempting to reconnect WebSocket for active run...')
          setTimeout(() => {
            // Check if still running and reconnect
            if (isRunningRef.current && currentRunIdRef.current) {
              connectWebSocket(currentRunIdRef.current)
            }
          }, 2000) // Wait 2 seconds for server to restart
        }
      } else if (event.code !== 1000 && event.code !== 1001) {
        // Log other unexpected disconnects
        setEvents(prev => [...prev, {
          timestamp: new Date().toISOString(),
          stage: 'system',
          event: 'log',
          msg: `WebSocket disconnected (code: ${event.code}${event.reason ? ': ' + event.reason : ''})`
        }])
        // Try to reconnect if we have an active run
        if (isRunningRef.current && currentRunIdRef.current) {
          setTimeout(() => {
            if (isRunningRef.current && currentRunIdRef.current) {
              connectWebSocket(currentRunIdRef.current)
            }
          }, 2000)
        }
      }
      // Status polling (every 10 seconds) will also handle reconnection as a backup
    }
    
    wsRef.current = ws
  }

  const disconnectWebSocket = () => {
    if (wsRef.current) {
      wsRef.current.close()
      wsRef.current = null
    }
  }

  const formatElapsedTime = (seconds) => {
    const hours = Math.floor(seconds / 3600)
    const minutes = Math.floor((seconds % 3600) / 60)
    const secs = seconds % 60
    if (hours > 0) {
      return `${hours}h ${minutes}m ${secs}s`
    } else if (minutes > 0) {
      return `${minutes}m ${secs}s`
    } else {
      return `${secs}s`
    }
  }

  const handleEvent = (event) => {
    console.log('[DEBUG] handleEvent:', {
      stage: event.stage,
      event: event.event,
      timestamp: event.timestamp,
      msg: event.msg?.substring(0, 50),
      currentStage,
      stageStartTime,
      stageElapsedTime
    })
    
    // Only add events that have meaningful content (message, data, or are critical events)
    const hasMessage = event.msg && event.msg.trim().length > 0
    const hasData = event.data && Object.keys(event.data).length > 0
    const isCriticalEvent = event.event === 'failure' || event.event === 'success' || event.event === 'start'
    
    // Skip blank events unless they're critical
    if (!hasMessage && !hasData && !isCriticalEvent) {
      return // Skip blank events
    }
    
    // Add event to list
    setEvents(prev => [...prev, event].slice(-100)) // Keep last 100 events
    
    // Update stage and set timer based on event timestamp (not current time)
    if (event.stage && event.stage !== currentStage) {
      console.log('[DEBUG] Stage changed:', currentStage, '->', event.stage)
      setCurrentStage(event.stage)
      // Use event timestamp to calculate actual stage start time
      if (event.timestamp) {
        const eventTime = new Date(event.timestamp).getTime()
        console.log('[DEBUG] Setting stageStartTime from new stage event:', new Date(eventTime).toISOString())
        setStageStartTime(eventTime)
        setStageElapsedTime(0)
      } else {
        // Fallback to current time if no timestamp
        console.log('[DEBUG] No timestamp in event, using current time')
        setStageStartTime(Date.now())
        setStageElapsedTime(0)
      }
    } else if (event.stage === currentStage) {
      // If we're already in this stage, check for start event or use first event timestamp
      if (event.event === 'start' && event.timestamp) {
        // Use start event timestamp as the definitive start time
        const eventTime = new Date(event.timestamp).getTime()
        console.log('[DEBUG] Setting stageStartTime from start event:', new Date(eventTime).toISOString())
        setStageStartTime(eventTime)
      } else if (!stageStartTime) {
        // If we don't have a start time yet, try to get it from event timestamp
        if (event.timestamp) {
          const eventTime = new Date(event.timestamp).getTime()
          console.log('[DEBUG] Setting stageStartTime from event timestamp (no start time yet):', new Date(eventTime).toISOString())
          setStageStartTime(eventTime)
        } else {
          // Fallback: if no timestamp, use current time (we'll adjust with progress updates)
          console.log('[DEBUG] No timestamp, using current time as fallback')
          setStageStartTime(Date.now())
        }
      }
    }
    
    // Track current processing item
    if (event.stage === 'analyzer' && event.event === 'log') {
      // Extract instrument name from log messages like "Running analyzer for ES"
      const match = event.msg?.match(/Running analyzer for (\w+)/i)
      if (match) {
        setCurrentProcessingItem(match[1])
      }
    } else if (event.stage === 'analyzer' && event.data?.instrument) {
      setCurrentProcessingItem(event.data.instrument)
    } else if (event.stage === 'translator' && event.event === 'log') {
      // Extract file info from translator logs
      const match = event.msg?.match(/Processing (.+)/i)
      if (match) {
        setCurrentProcessingItem(match[1])
      }
    }
    
    // Handle progress updates from analyzer - use elapsed_minutes if available
    if (event.stage === 'analyzer' && event.event === 'metric' && event.data?.elapsed_minutes) {
      // Update elapsed time based on backend's reported elapsed time
      const elapsedSeconds = event.data.elapsed_minutes * 60
      console.log('[DEBUG] Progress update from analyzer:', {
        elapsed_minutes: event.data.elapsed_minutes,
        elapsedSeconds,
        currentStageElapsedTime: stageElapsedTime
      })
      setStageElapsedTime(elapsedSeconds)
      // Adjust start time to match backend's elapsed time (so timer continues correctly)
      const adjustedStartTime = Date.now() - (elapsedSeconds * 1000)
      console.log('[DEBUG] Adjusting stageStartTime to:', new Date(adjustedStartTime).toISOString())
      setStageStartTime(adjustedStartTime)
    }
    
    // Handle export stage events
    if (event.stage === 'export') {
      if (event.event === 'start') {
        setExportStatus('active')
        if (event.data) {
          setExportInstrument(event.data.instrument || '')
          setExportDataType(event.data.dataType || '')
        }
      } else if (event.event === 'metric' && event.data) {
        setExportStatus('active')
        if (event.data.totalBarsProcessed !== undefined) {
          setExportRows(event.data.totalBarsProcessed)
        }
        if (event.data.fileCount !== undefined) {
          setExportFiles(event.data.fileCount)
        }
        // Calculate percent complete if we have estimates (this is a placeholder - adjust based on actual data)
        // For now, we'll use file size as a rough indicator
        if (event.data.fileSizeMB !== undefined) {
          // Rough estimate: assume 100MB = 100% (adjust based on typical export sizes)
          const estimatedPercent = Math.min(100, (event.data.fileSizeMB / 100) * 100)
          setExportPercent(estimatedPercent)
        }
        if (event.data.instrument) {
          setExportInstrument(event.data.instrument)
        }
        if (event.data.dataType) {
          setExportDataType(event.data.dataType)
        }
      } else if (event.event === 'success') {
        setExportStatus('complete')
        if (event.data) {
          if (event.data.totalBarsProcessed !== undefined) {
            setExportRows(event.data.totalBarsProcessed)
          }
          if (event.data.fileCount !== undefined) {
            setExportFiles(event.data.fileCount)
          }
          setExportPercent(100)
        }
      } else if (event.event === 'failure') {
        setExportStatus('failed')
        showAlert(event.msg || 'Export failed', 'error')
      }
    }
    
    // Update metrics
    if (event.event === 'metric' && event.data) {
      if (event.data.processed_file_count !== undefined || event.data.deleted_file_count !== undefined) {
        loadFileCounts() // Refresh file counts
      }
      if (event.data.rows_per_min !== undefined) {
        setRowsPerMin(event.data.rows_per_min)
      }
    }
    
    // Refresh file counts when any stage completes
    if ((event.event === 'success' || event.event === 'failure')) {
      if (event.stage === 'translator' || event.stage === 'analyzer' || event.stage === 'sequential') {
        loadFileCounts() // Refresh file counts after stage completes
      }
    }
    
    // Handle failures
    if (event.event === 'failure' && event.stage !== 'export') {
      showAlert(event.msg || 'Pipeline stage failed', 'error')
    }
    
    // Handle completion
    if (event.event === 'success' && event.stage === 'audit') {
      setIsRunning(false)
      showAlert('Pipeline completed successfully', 'success')
    }
    
    // Handle single stage completion
    if ((event.event === 'success' || event.event === 'failure') && 
        (event.stage === 'translator' || event.stage === 'analyzer' || event.stage === 'sequential')) {
      // Check if this is the final event (stage completed)
      // Wait a moment then check status to see if run is truly complete
      setTimeout(() => {
        checkPipelineStatus()
      }, 2000) // Wait 2 seconds then check status
    }
  }

  const showAlert = (message, type) => {
    setAlert({ message, type })
    setTimeout(() => setAlert(null), 5000)
  }

  return (
    <div className="min-h-screen bg-gray-900 text-gray-100">
      {/* Alert Popup */}
      {alert && (
        <div className={`fixed top-4 right-4 p-4 rounded-lg shadow-lg z-50 ${
          alert.type === 'error' ? 'bg-red-600' : 'bg-green-600'
        }`}>
          <div className="flex items-center justify-between">
            <span>{alert.message}</span>
            <button
              onClick={() => setAlert(null)}
              className="ml-4 text-white hover:text-gray-200"
            >
              ×
            </button>
          </div>
        </div>
      )}

      <div className="container mx-auto px-4 py-8">
        <div className="flex items-center justify-between mb-8">
          <h1 className="text-3xl font-bold">Pipeline Dashboard</h1>
          <div className="flex gap-2">
            <button
              onClick={async () => {
                try {
                  const response = await fetch(`${API_BASE}/apps/translator/start`, { method: 'POST' })
                  const data = await response.json()
                  if (data.url) {
                    window.open(data.url, '_blank')
                  }
                } catch (error) {
                  console.error('Failed to start translator app:', error)
                }
              }}
              className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded font-medium text-sm"
              title="Start and Open Translator App"
            >
              Translator
            </button>
            <button
              onClick={async () => {
                try {
                  const response = await fetch(`${API_BASE}/apps/analyzer/start`, { method: 'POST' })
                  const data = await response.json()
                  if (data.url) {
                    window.open(data.url, '_blank')
                  }
                } catch (error) {
                  console.error('Failed to start analyzer app:', error)
                }
              }}
              className="px-4 py-2 bg-purple-600 hover:bg-purple-700 rounded font-medium text-sm"
              title="Start and Open Analyzer App"
            >
              Analyzer
            </button>
            <button
              onClick={async () => {
                try {
                  const response = await fetch(`${API_BASE}/apps/sequential/start`, { method: 'POST' })
                  const data = await response.json()
                  if (data.url) {
                    window.open(data.url, '_blank')
                  }
                } catch (error) {
                  console.error('Failed to start sequential app:', error)
                }
              }}
              className="px-4 py-2 bg-orange-600 hover:bg-orange-700 rounded font-medium text-sm"
              title="Start and Open Sequential Processor App"
            >
              Sequential
            </button>
          </div>
        </div>

        {/* Controls Section */}
        <div className="bg-gray-800 rounded-lg p-6 mb-6">
          <h2 className="text-xl font-semibold mb-4">Controls</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium mb-2">
                Scheduled Daily Start Time
              </label>
              <div className="flex gap-2 items-center">
                <input
                  type="time"
                  value={scheduleTime}
                  onChange={(e) => setScheduleTime(e.target.value)}
                  className="bg-gray-700 border border-gray-600 rounded px-3 py-2"
                />
                <button
                  onClick={() => updateSchedule(scheduleTime)}
                  className="bg-blue-600 hover:bg-blue-700 px-4 py-2 rounded"
                >
                  Update
                </button>
                <div className="ml-auto flex items-center gap-2">
                  <span className="text-xs text-gray-400">Chicago Time:</span>
                  <span className="text-sm font-mono font-semibold text-gray-200 bg-gray-700 px-2 py-1 rounded">
                    {chicagoTime}
                  </span>
                </div>
              </div>
            </div>
            <div className="flex items-end gap-2">
              <button
                onClick={startPipeline}
                disabled={isRunning}
                className={`px-6 py-2 rounded font-medium ${
                  isRunning
                    ? 'bg-gray-600 cursor-not-allowed'
                    : 'bg-green-600 hover:bg-green-700'
                }`}
              >
                {isRunning ? 'Running...' : 'Run Now'}
              </button>
              {isRunning && (
                <button
                  onClick={resetPipelineState}
                  className="px-4 py-2 rounded font-medium bg-red-600 hover:bg-red-700"
                >
                  Reset
                </button>
              )}
            </div>
          </div>
        </div>

        {/* Data Merger Section */}
        <div className="bg-gray-800 rounded-lg p-6 mb-6">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-xl font-semibold mb-2">Data Merger / Consolidator</h2>
              <p className="text-sm text-gray-400">
                Merge daily analyzer and sequencer files into monthly Parquet files
              </p>
            </div>
            <button
              onClick={runDataMerger}
              disabled={mergerRunning || isRunning}
              className={`px-6 py-3 rounded font-medium ${
                mergerRunning || isRunning
                  ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                  : 'bg-indigo-600 hover:bg-indigo-700 text-white'
              }`}
            >
              {mergerRunning ? 'Running...' : 'Run Merger'}
            </button>
          </div>
          {mergerStatus && (
            <div className={`mt-4 p-4 rounded ${
              mergerStatus.status === 'success' 
                ? 'bg-green-900/30 border border-green-600' 
                : 'bg-red-900/30 border border-red-600'
            }`}>
              <div className="text-sm font-medium mb-2">
                {mergerStatus.status === 'success' ? '✓ Success' : '✗ Error'}
              </div>
              <div className="text-sm text-gray-300">{mergerStatus.message}</div>
              {mergerStatus.output && (
                <details className="mt-2">
                  <summary className="text-xs text-gray-400 cursor-pointer">Show output</summary>
                  <pre className="mt-2 text-xs bg-gray-900 p-2 rounded overflow-auto max-h-40">
                    {mergerStatus.output}
                  </pre>
                </details>
              )}
            </div>
          )}
        </div>

        {/* Metrics Section */}
        <div className="grid grid-cols-1 md:grid-cols-5 gap-4 mb-6">
          <div className="bg-gray-800 rounded-lg p-4">
            <div className="text-sm text-gray-400 mb-1">Run ID</div>
            <div className="text-lg font-mono text-gray-300">
              {currentRunId ? currentRunId.substring(0, 8) + '...' : 'None'}
            </div>
          </div>
          <div className="bg-gray-800 rounded-lg p-4">
            <div className="text-sm text-gray-400 mb-1">Current Stage</div>
            <div className="text-lg font-semibold capitalize">{currentStage}</div>
          </div>
          <div className="bg-gray-800 rounded-lg p-4">
            <div className="text-sm text-gray-400 mb-1">Raw Files</div>
            <div className="text-lg font-semibold mb-2">
              {fileCounts.raw_files === -1 ? 'Error' : fileCounts.raw_files || 0}
            </div>
            <button
              onClick={() => runStage('translator')}
              disabled={isRunning}
              className={`w-full px-3 py-1.5 text-sm rounded font-medium ${
                isRunning
                  ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                  : 'bg-blue-600 hover:bg-blue-700 text-white'
              }`}
            >
              Run Translator
            </button>
          </div>
          <div className="bg-gray-800 rounded-lg p-4">
            <div className="text-sm text-gray-400 mb-1">Processed Files</div>
            <div className="text-lg font-semibold mb-2">
              {fileCounts.processed_files === -1 ? 'Error' : (fileCounts.processed_files ?? 0)}
            </div>
            <button
              onClick={() => runStage('analyzer')}
              disabled={isRunning}
              className={`w-full px-3 py-1.5 text-sm rounded font-medium ${
                isRunning
                  ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                  : 'bg-purple-600 hover:bg-purple-700 text-white'
              }`}
            >
              Run Analyzer
            </button>
          </div>
          <div className="bg-gray-800 rounded-lg p-4">
            <div className="text-sm text-gray-400 mb-1">Analyzed Files</div>
            <div className="text-lg font-semibold mb-2">
              {fileCounts.analyzed_files === -1 ? 'Error' : (fileCounts.analyzed_files ?? 0)}
            </div>
            <button
              onClick={() => runStage('sequential')}
              disabled={isRunning}
              className={`w-full px-3 py-1.5 text-sm rounded font-medium ${
                isRunning
                  ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                  : 'bg-orange-600 hover:bg-orange-700 text-white'
              }`}
            >
              Run Sequential
            </button>
          </div>
        </div>

        {/* Rows per Minute */}
        {rowsPerMin > 0 && (
          <div className="bg-gray-800 rounded-lg p-4 mb-6">
            <div className="text-sm text-gray-400 mb-1">Processing Rate</div>
            <div className="text-lg font-semibold">{rowsPerMin.toLocaleString()} rows/min</div>
          </div>
        )}

        {/* Export Stage Panel */}
        <div className="bg-gray-800 rounded-lg p-6 mb-6">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-xl font-semibold">Export Stage</h2>
            <div className={`px-3 py-1 rounded text-sm font-medium ${
              exportStatus === 'not_started' ? 'bg-gray-700 text-gray-400' :
              exportStatus === 'active' ? 'bg-blue-600 text-white' :
              exportStatus === 'complete' ? 'bg-green-600 text-white' :
              'bg-red-600 text-white'
            }`}>
              {exportStatus === 'not_started' ? 'Not Started' :
               exportStatus === 'active' ? 'Active' :
               exportStatus === 'complete' ? 'Complete' :
               'Failed / Stalled'}
            </div>
          </div>
          
          {exportStatus !== 'not_started' && (
            <div className="space-y-4">
              {/* Progress Bar */}
              {exportStatus === 'active' && exportPercent > 0 && (
                <div>
                  <div className="flex justify-between text-sm text-gray-400 mb-1">
                    <span>Progress</span>
                    <span>{exportPercent.toFixed(1)}%</span>
                  </div>
                  <div className="w-full bg-gray-700 rounded-full h-2.5">
                    <div
                      className="bg-blue-600 h-2.5 rounded-full transition-all duration-300"
                      style={{ width: `${exportPercent}%` }}
                    ></div>
                  </div>
                </div>
              )}
              
              {/* Summary Text */}
              <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-sm">
                <div>
                  <div className="text-gray-400 mb-1">Rows Processed</div>
                  <div className="text-lg font-semibold">{exportRows.toLocaleString()}</div>
                </div>
                <div>
                  <div className="text-gray-400 mb-1">Files</div>
                  <div className="text-lg font-semibold">{exportFiles}</div>
                </div>
                <div>
                  <div className="text-gray-400 mb-1">Instrument</div>
                  <div className="text-lg font-semibold">
                    {exportInstrument || 'Unknown'} {exportDataType && `(${exportDataType})`}
                  </div>
                </div>
              </div>
              
              {exportStatus === 'failed' && (
                <div className="bg-red-900/30 border border-red-600 rounded p-3 text-red-300">
                  Export has failed or stalled. Check the events log for details.
                </div>
              )}
            </div>
          )}
          
          {exportStatus === 'not_started' && (
            <div className="text-gray-500 text-sm">Waiting for export to begin...</div>
          )}
        </div>

        {/* Events Log */}
        <div className="bg-gray-800 rounded-lg p-6">
          <h2 className="text-xl font-semibold mb-4">Live Events</h2>
          <div className="bg-gray-900 rounded p-4 h-96 overflow-y-auto font-mono text-sm">
            {events.length === 0 ? (
              <div className="text-gray-500">No events yet...</div>
            ) : (
              events.map((event, index) => (
                <div
                  key={index}
                  className={`mb-2 p-2 rounded ${
                    event.event === 'failure'
                      ? 'bg-red-900/30 border-l-2 border-red-500'
                      : event.event === 'success'
                      ? 'bg-green-900/30 border-l-2 border-green-500'
                      : 'bg-gray-800 border-l-2 border-gray-600'
                  }`}
                >
                  <div className="flex items-center gap-2">
                    <span className="text-gray-400 text-xs">
                      {new Date(event.timestamp).toLocaleTimeString()}
                    </span>
                    <span className="text-blue-400 capitalize">{event.stage}</span>
                    <span className="text-yellow-400 capitalize">{event.event}</span>
                    {event.msg && (
                      <span className="text-gray-300">{event.msg}</span>
                    )}
                  </div>
                  {event.data && (
                    <div className="text-gray-400 text-xs mt-1 ml-4">
                      {JSON.stringify(event.data)}
                    </div>
                  )}
                </div>
              ))
            )}
          </div>
        </div>
      </div>
    </div>
  )
}

export default App

