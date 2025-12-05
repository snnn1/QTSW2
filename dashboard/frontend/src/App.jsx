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
import { checkBackendConnection } from './services/pipelineManager'

function App() {
  const {
    pipelineStatus,
    stageInfo,
    exportInfo,
    metrics,
    scheduleInfo,
    eventsFormatted,
    mergerInfo,
    alertInfo,
    startPipeline,
    runStage,
    runDataMerger,
    startApp,
    resetPipelineState,
    clearAlert,
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

  // Backend connection status
  const [backendConnected, setBackendConnected] = useState(true)
  useEffect(() => {
    const checkConnection = async () => {
      const connected = await checkBackendConnection()
      setBackendConnected(connected)
    }
    
    // Check immediately
    checkConnection()
    
    // Check every 5 seconds
    const interval = setInterval(checkConnection, 5000)
    return () => clearInterval(interval)
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

        {/* Backend Connection Status */}
        {!backendConnected && (
          <div className="fixed bottom-4 right-4 bg-red-600 text-white px-4 py-2 rounded-lg shadow-lg z-40 flex items-center gap-2">
            <span className="w-2 h-2 bg-white rounded-full animate-pulse"></span>
            <span>Backend disconnected</span>
          </div>
        )}

        <div className="container mx-auto px-4 py-8">
          <PipelineControls
            chicagoTime={chicagoTime}
            scheduleInfo={scheduleInfo}
            isRunning={pipelineStatus.isRunning}
            onStartPipeline={startPipeline}
            onResetPipeline={resetPipelineState}
            onStartApp={startApp}
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
