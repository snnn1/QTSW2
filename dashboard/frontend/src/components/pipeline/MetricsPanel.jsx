import { MetricCard } from '../ui/MetricCard'

/**
 * Metrics Panel component
 */
export function MetricsPanel({
  pipelineStatus,
  stageInfo,
  metrics,
  mergerInfo,
  isRunning,
  onRunStage,
  onRunMerger,
}) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-5 gap-4 mb-6">
      <MetricCard
        label="Run ID"
        value={pipelineStatus.runId}
      />
      <MetricCard
        label="Current Stage"
        value={
          <div>
            <div className="text-lg font-semibold capitalize text-gray-300">{stageInfo.stageLabel}</div>
            {stageInfo.isActive && stageInfo.elapsedSeconds > 0 && (
              <div className="text-xs text-gray-500 mt-1">{stageInfo.elapsedLabel}</div>
            )}
          </div>
        }
      />
      <MetricCard
        label="Raw Files"
        value={metrics.rawFiles}
        button={
          <button
            onClick={() => onRunStage('translator')}
            disabled={isRunning}
            className={`w-full px-3 py-1.5 text-sm rounded font-medium ${
              isRunning
                ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                : 'bg-gray-700 hover:bg-gray-600 text-gray-200'
            }`}
          >
            Run Translator
          </button>
        }
      />
      <MetricCard
        label="Processed Files"
        value={metrics.processedFiles}
        button={
          <button
            onClick={() => onRunStage('analyzer')}
            disabled={isRunning}
            className={`w-full px-3 py-1.5 text-sm rounded font-medium ${
              isRunning
                ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                : 'bg-gray-700 hover:bg-gray-600 text-gray-200'
            }`}
          >
            Run Analyzer
          </button>
        }
      />
      <MetricCard
        label="Data Merger"
        value={<span className={mergerInfo.displayClass}>{mergerInfo.displayText}</span>}
        button={
          <button
            onClick={onRunMerger}
            disabled={mergerInfo.isRunning || isRunning}
            className={`w-full px-3 py-1.5 text-sm rounded font-medium ${
              mergerInfo.isRunning || isRunning
                ? 'bg-gray-600 cursor-not-allowed text-gray-400'
                : 'bg-gray-700 hover:bg-gray-600 text-gray-200'
            }`}
          >
            {mergerInfo.isRunning ? 'Running...' : 'Run Merger'}
          </button>
        }
      />
    </div>
  )
}

