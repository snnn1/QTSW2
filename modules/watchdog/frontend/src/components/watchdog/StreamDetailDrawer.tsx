/**
 * StreamDetailDrawer component
 * Right-side slide-out drawer with stream details
 */
import { formatChicagoDateTime } from '../../utils/timeUtils.ts'
import type { StreamState } from '../../types/watchdog'

interface StreamDetailDrawerProps {
  stream: StreamState | null
  isOpen: boolean
  onClose: () => void
}

export function StreamDetailDrawer({ stream, isOpen, onClose }: StreamDetailDrawerProps) {
  if (!isOpen || !stream) {
    return null
  }
  
  return (
    <div className="fixed inset-y-0 right-0 w-96 bg-gray-900 border-l border-gray-700 z-50 overflow-y-auto">
      <div className="p-4">
        {/* Header */}
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-xl font-semibold">Stream Details</h2>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-white text-2xl"
          >
            ×
          </button>
        </div>
        
        {/* Stream Summary */}
        <div className="mb-6">
          <h3 className="text-lg font-semibold mb-2">Stream Summary</h3>
          <div className="space-y-1 text-sm">
            <div><span className="text-gray-400">Stream:</span> <span className="font-mono">{stream.stream}</span></div>
            <div><span className="text-gray-400">Instrument:</span> {stream.instrument}</div>
            <div><span className="text-gray-400">Session:</span> {stream.session}</div>
            <div><span className="text-gray-400">State:</span> {stream.state}</div>
            <div><span className="text-gray-400">Slot Time:</span> <span className="font-mono">{stream.slot_time_chicago}</span></div>
            <div><span className="text-gray-400">Committed:</span> {stream.committed ? 'Yes' : 'No'}</div>
            {stream.commit_reason && (
              <div><span className="text-gray-400">Commit Reason:</span> {stream.commit_reason}</div>
            )}
          </div>
        </div>
        
        {/* Range Details */}
        {stream.range_high !== null && stream.range_low !== null && (
          <div className="mb-6">
            <h3 className="text-lg font-semibold mb-2">Range Details</h3>
            <div className="space-y-1 text-sm">
              <div><span className="text-gray-400">Range High:</span> <span className="font-mono">{stream.range_high.toFixed(2)}</span></div>
              <div><span className="text-gray-400">Range Low:</span> <span className="font-mono">{stream.range_low.toFixed(2)}</span></div>
              <div><span className="text-gray-400">Range Size:</span> <span className="font-mono">{(stream.range_high - stream.range_low).toFixed(2)}</span></div>
              {stream.freeze_close !== null && (
                <div><span className="text-gray-400">Freeze Close:</span> <span className="font-mono">{stream.freeze_close.toFixed(2)}</span></div>
              )}
              <div><span className="text-gray-400">Invalidated:</span> {stream.range_invalidated ? 'Yes' : 'No'}</div>
              {stream.range_locked_time_chicago && (
                <div><span className="text-gray-400">Range Locked:</span> <span className="font-mono">{formatChicagoDateTime(stream.range_locked_time_utc!)}</span></div>
              )}
            </div>
          </div>
        )}
        
        {/* State Timeline */}
        <div className="mb-6">
          <h3 className="text-lg font-semibold mb-2">State Timeline</h3>
          <div className="text-sm">
            <div className="text-gray-400">Current State:</div>
            <div className="ml-4">{stream.state}</div>
            <div className="text-gray-400 mt-2">State Entry Time:</div>
            <div className="ml-4 font-mono">{formatChicagoDateTime(stream.state_entry_time_utc)}</div>
          </div>
        </div>
        
        {/* Note: Execution attempts, order rejections, protective failures would come from events */}
        <div className="text-sm text-gray-500">
          Full execution history available in Execution Journal page.
        </div>
      </div>
    </div>
  )
}
