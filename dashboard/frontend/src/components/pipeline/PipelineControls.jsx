import { TimeDisplay } from '../ui/TimeDisplay'
import { NextRunInfo } from './NextRunInfo'

/**
 * Pipeline Controls component
 */
export function PipelineControls({
  chicagoTime,
  scheduleInfo,
  isRunning,
  onStartPipeline,
  onResetPipeline,
  onStartApp,
}) {
  return (
    <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <div className="flex items-center gap-4 flex-wrap">
            <TimeDisplay label="Chicago Time" time={chicagoTime} />
            <NextRunInfo scheduleInfo={scheduleInfo} />
          </div>
        </div>
        <div className="flex items-end gap-2 flex-wrap">
          <button
            onClick={onStartPipeline}
            disabled={isRunning}
            className={`px-6 py-2 rounded font-medium ${
              isRunning
                ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                : 'bg-green-600 hover:bg-green-700 text-white'
            }`}
          >
            {isRunning ? 'Running...' : 'Run Now'}
          </button>
          {isRunning && (
            <button
              onClick={onResetPipeline}
              className="px-4 py-2 rounded font-medium bg-red-600 hover:bg-red-700"
            >
              Reset
            </button>
          )}
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




