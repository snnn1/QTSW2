import { useEffect, useState } from 'react'
import './App.css'
import { usePipelineState } from './hooks/usePipelineState'
import { parseChicagoTime } from './utils/timeUtils'
import ErrorBoundary from './components/ErrorBoundary'
import { PipelineControls } from './components/pipeline/PipelineControls'
import { MetricsPanel } from './components/pipeline/MetricsPanel'
import { ExportPanel } from './components/pipeline/ExportPanel'
import { EventsLog } from './components/pipeline/EventsLog'
import { ProcessingRateCard } from './components/pipeline/ProcessingRateCard'
// AnalyzerFilesPanel removed - events now show in main EventsLog
import { checkBackendConnection } from './services/pipelineManager'

function App() {
  console.log('[App] Component rendering...')
  const {
    pipelineStatus,
    stageInfo,
    exportInfo,
    metrics,
    scheduleInfo,
    eventsFormatted,
    mergerInfo,
    alertInfo,
    schedulerEnabled,
    startPipeline,
    runStage,
    runDataMerger,
    startApp,
    resetPipelineState,
    clearAlert,
    toggleScheduler,
  } = usePipelineState()

  // Update Chicago time every second
  const [chicagoTime, setChicagoTime] = useState(parseChicagoTime())
  useEffect(() => {
    const updateChicagoTime = () => {
      setChicagoTime(parseChicagoTime())
    }
    updateChicagoTime()
    const timeInterval = setInterval(updateChicagoTime, 1000)
    return () => clearInterval(timeInterval)
  }, [])

  // Phase-1 always-on: Remove aggressive disconnect logic
  // Health endpoint failures should log and retry, NOT flip UI state aggressively
  const [backendConnected, setBackendConnected] = useState(true)
  
  useEffect(() => {
    let checkInterval = null
    
    const checkConnection = async () => {
      const connected = await checkBackendConnection()
      
      if (connected) {
        setBackendConnected(true)
      } else {
        // Log but don't aggressively flip UI state
        console.log(`[Connection] Health check failed - will retry`)
        // Keep showing as connected - don't disrupt UI
        // Health checks are best-effort, not authoritative
      }
    }
    
    // Wait 2 seconds before first check to allow backend to fully start
    setTimeout(() => {
      checkConnection()
    }, 2000)
    
    // Check every 10 seconds when connected, slower when disconnected
    checkInterval = setInterval(checkConnection, 10000)
    
    return () => {
      if (checkInterval) {
        clearInterval(checkInterval)
      }
    }
  }, []) // Empty deps - only run once on mount

  // Check if accessing from wrong port (production mode should be on 8001)
  useEffect(() => {
    const currentPort = window.location.port
    if (currentPort === '8000' && window.location.hostname === 'localhost') {
      console.error('[App] Wrong port detected! Backend is on port 8001, but page is on port 8000.')
      console.error('[App] Please navigate to: http://localhost:8001')
      // Show alert to user
      alert('Wrong port detected!\n\nBackend is running on port 8001.\nPlease navigate to:\nhttp://localhost:8001\n\nClosing this page...')
      // Redirect after a moment
      setTimeout(() => {
        window.location.href = 'http://localhost:8001'
      }, 2000)
    }
  }, [])

  return (
    <ErrorBoundary>
      <div className="min-h-screen bg-black text-white">
        {/* Alert Popup */}
        {alertInfo && (
          <div className={`fixed top-4 right-4 p-4 rounded-lg shadow-lg z-50 ${
            alertInfo.type === 'error' ? 'bg-red-600' : 'bg-green-600'
          }`}>
            <div className="flex items-center justify-between">
              <span>{alertInfo.message}</span>
              <button
                onClick={clearAlert}
                className="ml-4 text-white hover:text-gray-200"
              >
                ×
              </button>
            </div>
          </div>
        )}

        {/* Phase-1 always-on: Removed aggressive "disconnected" UI */}
        {/* WebSocket close handling shows "Reconnecting..." in the events panel instead */}

        <div className="container mx-auto px-4 py-8">
          <PipelineControls
            chicagoTime={chicagoTime}
            scheduleInfo={scheduleInfo}
            isRunning={pipelineStatus.isRunning}
            schedulerEnabled={schedulerEnabled}
            pipelineState={pipelineStatus.state}
            activeRunId={pipelineStatus.runId}
            onStartPipeline={startPipeline}
            onResetPipeline={resetPipelineState}
            onStartApp={startApp}
            onToggleScheduler={toggleScheduler}
          />

          <MetricsPanel
            pipelineStatus={pipelineStatus}
            stageInfo={stageInfo}
            metrics={metrics}
            mergerInfo={mergerInfo}
            isRunning={pipelineStatus.isRunning}
            onRunStage={runStage}
            onRunMerger={runDataMerger}
          />

          <ProcessingRateCard rowsPerMin={metrics.rowsPerMin} />

          <ExportPanel exportInfo={exportInfo} />

          <EventsLog eventsFormatted={eventsFormatted} />
        </div>
      </div>
    </ErrorBoundary>
  )
}

export default App
