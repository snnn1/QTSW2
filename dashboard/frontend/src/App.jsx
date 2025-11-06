import { useState, useEffect, useRef } from 'react'
import './App.css'

const API_BASE = 'http://localhost:8000/api'
const WS_BASE = 'ws://localhost:8000/ws'

function App() {
  const [scheduleTime, setScheduleTime] = useState('07:30')
  const [currentRunId, setCurrentRunId] = useState(null)
  const [currentStage, setCurrentStage] = useState('idle')
  const [fileCounts, setFileCounts] = useState({ raw_files: 0, processed_files: 0 })
  const [rowsPerMin, setRowsPerMin] = useState(0)
  const [events, setEvents] = useState([])
  const [alert, setAlert] = useState(null)
  const [isRunning, setIsRunning] = useState(false)
  
  const wsRef = useRef(null)
  const reconnectTimeoutRef = useRef(null)

  // Load schedule on mount
  useEffect(() => {
    loadSchedule()
    loadFileCounts()
    checkPipelineStatus()
    
    // Poll file counts every 5 seconds
    const fileCountInterval = setInterval(loadFileCounts, 5000)
    
    return () => {
      clearInterval(fileCountInterval)
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
      const data = await response.json()
      setScheduleTime(data.schedule_time)
    } catch (error) {
      console.error('Failed to load schedule:', error)
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
      const data = await response.json()
      setFileCounts(data)
    } catch (error) {
      console.error('Failed to load file counts:', error)
    }
  }

  const checkPipelineStatus = async () => {
    try {
      const response = await fetch(`${API_BASE}/pipeline/status`)
      const data = await response.json()
      if (data.active) {
        setCurrentRunId(data.run_id)
        setCurrentStage(data.stage)
        setIsRunning(true)
        connectWebSocket(data.run_id)
      }
    } catch (error) {
      console.error('Failed to check pipeline status:', error)
    }
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
      setEvents([])
      connectWebSocket(data.run_id)
      showAlert('Pipeline started', 'success')
    } catch (error) {
      showAlert('Failed to start pipeline', 'error')
    }
  }

  const connectWebSocket = (runId) => {
    disconnectWebSocket()
    
    if (!runId) {
      console.warn('No run ID provided for WebSocket connection')
      return
    }
    
    const wsUrl = `${WS_BASE}/events/${runId}`
    const ws = new WebSocket(wsUrl)
    
    ws.onopen = () => {
      console.log('WebSocket connected')
    }
    
    ws.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data)
        handleEvent(data)
      } catch (error) {
        console.error('Failed to parse WebSocket message:', error)
      }
    }
    
    ws.onerror = (error) => {
      console.error('WebSocket error:', error)
    }
    
    ws.onclose = () => {
      console.log('WebSocket disconnected')
      // Reconnect after 2 seconds if pipeline is still running
      reconnectTimeoutRef.current = setTimeout(() => {
        if (isRunning && currentRunId) {
          connectWebSocket(currentRunId)
        }
      }, 2000)
    }
    
    wsRef.current = ws
  }

  const disconnectWebSocket = () => {
    if (wsRef.current) {
      wsRef.current.close()
      wsRef.current = null
    }
  }

  const handleEvent = (event) => {
    // Add event to list
    setEvents(prev => [...prev, event].slice(-100)) // Keep last 100 events
    
    // Update stage
    if (event.stage) {
      setCurrentStage(event.stage)
    }
    
    // Update metrics
    if (event.event === 'metric' && event.data) {
      if (event.data.processed_file_count !== undefined) {
        loadFileCounts() // Refresh file counts
      }
      if (event.data.rows_per_min !== undefined) {
        setRowsPerMin(event.data.rows_per_min)
      }
    }
    
    // Handle failures
    if (event.event === 'failure') {
      showAlert(event.msg || 'Pipeline stage failed', 'error')
    }
    
    // Handle completion
    if (event.event === 'success' && event.stage === 'audit') {
      setIsRunning(false)
      showAlert('Pipeline completed successfully', 'success')
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
        <h1 className="text-3xl font-bold mb-8">Pipeline Dashboard</h1>

        {/* Controls Section */}
        <div className="bg-gray-800 rounded-lg p-6 mb-6">
          <h2 className="text-xl font-semibold mb-4">Controls</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium mb-2">
                Scheduled Daily Start Time
              </label>
              <div className="flex gap-2">
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
              </div>
            </div>
            <div className="flex items-end">
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
            </div>
          </div>
        </div>

        {/* Metrics Section */}
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4 mb-6">
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
            <div className="text-lg font-semibold">{fileCounts.raw_files}</div>
          </div>
          <div className="bg-gray-800 rounded-lg p-4">
            <div className="text-sm text-gray-400 mb-1">Processed Files</div>
            <div className="text-lg font-semibold">{fileCounts.processed_files}</div>
          </div>
        </div>

        {/* Rows per Minute */}
        {rowsPerMin > 0 && (
          <div className="bg-gray-800 rounded-lg p-4 mb-6">
            <div className="text-sm text-gray-400 mb-1">Processing Rate</div>
            <div className="text-lg font-semibold">{rowsPerMin.toLocaleString()} rows/min</div>
          </div>
        )}

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

