import { StatusBadge } from '../ui/StatusBadge'
import { ProgressBar } from '../ui/ProgressBar'

/**
 * Export Panel component
 */
export function ExportPanel({ exportInfo = {} }) {
  const status = exportInfo.status || 'not_started'
  const rows = exportInfo.rows || 0
  const files = exportInfo.files || 0
  const label = exportInfo.label || '-'
  const percent = exportInfo.percent || 0
  
  return (
    <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-semibold text-gray-300">Export Stage</h2>
        <StatusBadge status={status} />
      </div>
      
      {status !== 'not_started' && (
        <div className="space-y-4">
          {status === 'active' && percent > 0 && (
            <ProgressBar
              percent={percent}
              label="Progress"
              showPercent={true}
            />
          )}
          
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-sm">
            <div>
              <div className="text-gray-400 mb-1">Rows Processed</div>
              <div className="text-lg font-semibold text-gray-300">{rows.toLocaleString()}</div>
            </div>
            <div>
              <div className="text-gray-400 mb-1">Files</div>
              <div className="text-lg font-semibold text-gray-300">{files}</div>
            </div>
            <div>
              <div className="text-gray-400 mb-1">Instrument</div>
              <div className="text-lg font-semibold text-gray-300">{label}</div>
            </div>
          </div>
          
          {status === 'failed' && (
            <div className="bg-red-900/30 border border-red-600 rounded p-3 text-red-400">
              Export has failed or stalled. Check the events log for details.
            </div>
          )}
        </div>
      )}
      
      {status === 'not_started' && (
        <div className="text-gray-500 text-sm">Waiting for export to begin...</div>
      )}
    </div>
  )
}








