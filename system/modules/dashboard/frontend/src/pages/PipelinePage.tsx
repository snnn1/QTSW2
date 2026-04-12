/**
 * PipelinePage - Existing pipeline dashboard page
 * Preserved from original App.jsx
 */
import { useEffect, useState } from 'react'
import '../App.css'
import { usePipelineState } from '../hooks/usePipelineState'
import { parseChicagoTime } from '../utils/timeUtils'
import ErrorBoundary from '../components/ErrorBoundary'
import { PipelineControls } from '../components/pipeline/PipelineControls'
import { MetricsPanel } from '../components/pipeline/MetricsPanel'
import { ExportPanel } from '../components/pipeline/ExportPanel'
import { EventsLog } from '../components/pipeline/EventsLog'
import { ProcessingRateCard } from '../components/pipeline/ProcessingRateCard'
import { checkBackendConnection } from '../services/pipelineManager'
import { DashboardNavigationBar } from '../components/shared/DashboardNavigationBar'

export function PipelinePage() {
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
    clearPipelineLock,
    isStarting,
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
  const [backendConnected, setBackendConnected] = useState(true)
  
  useEffect(() => {
    let checkInterval: NodeJS.Timeout | null = null
    
    const checkConnection = async () => {
      const connected = await checkBackendConnection()
      
      if (connected) {
        setBackendConnected(true)
      } else {
        console.log(`[Connection] Health check failed - will retry`)
      }
    }
    
    setTimeout(() => {
      checkConnection()
    }, 2000)
    
    checkInterval = setInterval(checkConnection, 10000)
    
    return () => {
      if (checkInterval) {
        clearInterval(checkInterval)
      }
    }
  }, [])

  // Check if accessing from wrong port
  useEffect(() => {
    const currentPort = window.location.port
    if (currentPort === '8000' && window.location.hostname === 'localhost') {
      console.error('[App] Wrong port detected! Backend is on port 8001, but page is on port 8000.')
      console.error('[App] Please navigate to: http://localhost:8001')
      alert('Wrong port detected!\n\nBackend is running on port 8001.\nPlease navigate to:\nhttp://localhost:8001\n\nClosing this page...')
      setTimeout(() => {
        window.location.href = 'http://localhost:8001'
      }, 2000)
    }
  }, [])

  return (
    <ErrorBoundary>
      <div className="min-h-screen bg-black text-white">
        <DashboardNavigationBar />
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

        <div className="container mx-auto px-4 py-8 pt-14">
          <PipelineControls
            chicagoTime={chicagoTime}
            scheduleInfo={scheduleInfo}
            isRunning={pipelineStatus.isRunning}
            isStarting={isStarting}
            schedulerEnabled={schedulerEnabled}
            pipelineState={pipelineStatus.state}
            activeRunId={pipelineStatus.runId}
            onStartPipeline={startPipeline}
            onResetPipeline={resetPipelineState}
            onStartApp={startApp}
            onToggleScheduler={toggleScheduler}
            onClearLock={clearPipelineLock}
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
