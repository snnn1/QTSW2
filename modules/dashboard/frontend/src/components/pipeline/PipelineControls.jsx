import { useEffect, useRef } from 'react'
import { TimeDisplay } from '../ui/TimeDisplay'
import { NextRunInfo } from './NextRunInfo'

/**
 * Pipeline Controls component
 */
export function PipelineControls({
  chicagoTime,
  scheduleInfo,
  isRunning,
  isStarting,
  schedulerEnabled,
  pipelineState,
  activeRunId,
  onStartPipeline,
  onResetPipeline,
  onStartApp,
  onToggleScheduler,
  onClearLock,
}) {
  // Determine if reset button should be enabled
  // Phase-1 always-on: Simple button logic
  // Start button: enabled if not currently running or starting
  const canStart = !isRunning && !isStarting
  
  // Reset button: enabled if running or in non-idle state
  const canReset = isRunning || (pipelineState && pipelineState !== 'idle')
  
  // Debug logging
  if (typeof window !== 'undefined' && window.location.search.includes('debug')) {
    console.log('[PipelineControls] Button state:', { isRunning, pipelineState, canStart })
  }

  return (
    <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <div className="flex items-center gap-4 flex-wrap">
            <TimeDisplay label="Chicago Time" time={chicagoTime} />
            <NextRunInfo scheduleInfo={scheduleInfo} />
          </div>
          {pipelineState && (
            <div className="mt-2 text-sm text-gray-400">
              Pipeline state: <span className="font-medium text-white">{pipelineState}</span>
              {activeRunId && (
                <span className="ml-2 text-xs">(Run: {activeRunId.substring(0, 8)})</span>
              )}
            </div>
          )}
        </div>
        <div className="flex items-end gap-2 flex-wrap">
          <button
            onClick={() => {
              console.log('[PipelineControls] Button clicked', { canStart, isRunning, pipelineState })
              if (canStart) {
                onStartPipeline()
              } else {
                console.warn('[PipelineControls] Button click ignored - disabled', { isRunning, pipelineState })
              }
            }}
            disabled={!canStart}
            className={`px-6 py-2 rounded font-medium ${
              !canStart
                ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                : 'bg-green-600 hover:bg-green-700 text-white'
            }`}
            title={!canStart ? `Cannot start: isRunning=${isRunning}, isStarting=${isStarting}, state=${pipelineState}` : 'Start pipeline'}
          >
            {isRunning || isStarting ? 'Running...' : 'Run Pipeline Now'}
          </button>
          {canReset && (
            <button
              onClick={() => {
                if (window.confirm('Are you sure you want to reset the pipeline? This will cancel any running operation.')) {
                  onResetPipeline();
                }
              }}
              className="px-4 py-2 rounded font-medium bg-red-600 hover:bg-red-700 text-white"
            >
              Reset / Cancel
            </button>
          )}
          <button
            onClick={onToggleScheduler}
            className={`px-4 py-2 rounded font-medium ${
              schedulerEnabled
                ? 'bg-blue-600 hover:bg-blue-700 text-white'
                : 'bg-gray-600 hover:bg-gray-700 text-white'
            }`}
            title={schedulerEnabled ? 'Disable automated scheduling' : 'Enable automated scheduling'}
          >
            {schedulerEnabled ? 'Automation: ON' : 'Automation: OFF'}
          </button>
          <button
            onClick={() => {
              if (window.confirm('Clear the pipeline lock? This will allow the pipeline to run if it\'s stuck with a lock error.')) {
                onClearLock();
              }
            }}
            className="px-4 py-2 rounded font-medium bg-orange-600 hover:bg-orange-700 text-white"
            title="Clear pipeline lock file (use when getting 'Failed to acquire lock' errors)"
          >
            Clear Lock
          </button>
          <div className="flex gap-2 ml-auto">
            <button
              onClick={() => onStartApp('translator')}
              className="px-4 py-2 bg-gray-700 hover:bg-gray-600 rounded font-medium text-sm text-gray-200"
              title="Start and Open Translator App"
            >
              Translator
            </button>
            <button
              onClick={() => onStartApp('analyzer')}
              className="px-4 py-2 bg-gray-700 hover:bg-gray-600 rounded font-medium text-sm text-gray-200"
              title="Start and Open Analyzer App"
            >
              Analyzer
            </button>
            <button
              onClick={() => onStartApp('matrix')}
              className="px-4 py-2 bg-gray-700 hover:bg-gray-600 rounded font-medium text-sm text-gray-200"
              title="Start and Open Master Matrix App"
            >
              Matrix
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}






