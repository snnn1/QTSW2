/**
 * StreamDetailDrawer component
 * Right-side slide-out drawer with stream details
 */
import { useMemo } from 'react'
import { formatChicagoDateTime, formatChicagoTime } from '../../utils/timeUtils.ts'
import { getEventLevel, extractEventMessage } from '../../utils/eventUtils'
import type { StreamState, WatchdogEvent } from '../../types/watchdog'

interface StreamDetailDrawerProps {
  stream: StreamState | null
  events?: WatchdogEvent[]
  isOpen: boolean
  onClose: () => void
}

export function StreamDetailDrawer({ stream, events = [], isOpen, onClose }: StreamDetailDrawerProps) {
  const EXECUTION_EVENT_TYPES = new Set([
    'ORDER_SUBMITTED', 'ORDER_ACKNOWLEDGED', 'ORDER_REJECTED', 'ORDER_CANCELLED',
    'EXECUTION_FILLED', 'EXECUTION_PARTIAL_FILL', 'EXECUTION_BLOCKED', 'EXECUTION_ALLOWED',
    'PROTECTIVE_ORDERS_SUBMITTED', 'PROTECTIVE_ORDERS_FAILED_FLATTENED',
  ])
  
  const streamEvents = useMemo(() => {
    if (!stream || !events.length) return []
    return events
      .filter((e) => e.stream === stream.stream)
      .slice(-50)
      .reverse()
  }, [stream?.stream, events])
  
  const executionEvents = useMemo(() => {
    if (!stream || !events.length) return []
    return events
      .filter((e) => e.stream === stream.stream && EXECUTION_EVENT_TYPES.has(e.event_type))
      .slice(-30)
      .reverse()
  }, [stream?.stream, events])
  
  if (!isOpen || !stream) {
    return null
  }
  
  const getLevelColor = (level: string) => {
    switch (level) {
      case 'ERROR': return 'bg-red-600'
      case 'WARN': return 'bg-amber-500'
      default: return 'bg-gray-600'
    }
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
            <div><span className="text-gray-400">Instrument:</span> {stream.execution_instrument || stream.instrument}</div>
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
        
        {/* Execution Attempts */}
        {executionEvents.length > 0 && (
          <div className="mb-6">
            <h3 className="text-lg font-semibold mb-2">Execution Attempts ({executionEvents.length})</h3>
            <div className="overflow-y-auto max-h-40 space-y-1 text-xs">
              {executionEvents.map((ev) => {
                const level = getEventLevel(ev.event_type)
                const msg = extractEventMessage(ev)
                const data = ev.data || {}
                const qty = data.quantity ?? data.fill_quantity ?? data.entry_filled_qty
                const price = data.price ?? data.fill_price
                const extra = [qty != null && `qty=${qty}`, price != null && `@${price}`].filter(Boolean).join(' ')
                return (
                  <div key={`${ev.run_id}:${ev.event_seq}`} className="flex gap-2 py-1 border-b border-gray-700">
                    <span className="font-mono shrink-0">{formatChicagoTime(ev.timestamp_chicago || ev.timestamp_utc)}</span>
                    <span className={`px-1 rounded shrink-0 ${getLevelColor(level)}`}>{level}</span>
                    <span className="truncate" title={msg}>
                      {ev.event_type}
                      {extra && <span className="text-gray-400 ml-1">({extra})</span>}
                    </span>
                  </div>
                )
              })}
            </div>
          </div>
        )}
        
        {/* Stream Event History */}
        {streamEvents.length > 0 && (
          <div className="mb-6">
            <h3 className="text-lg font-semibold mb-2">Recent Events ({streamEvents.length})</h3>
            <div className="overflow-y-auto max-h-48 space-y-1 text-xs">
              {streamEvents.map((ev) => {
                const level = getEventLevel(ev.event_type)
                const msg = extractEventMessage(ev)
                return (
                  <div key={`${ev.run_id}:${ev.event_seq}`} className="flex gap-2 py-1 border-b border-gray-700">
                    <span className="font-mono shrink-0">{formatChicagoTime(ev.timestamp_chicago || ev.timestamp_utc)}</span>
                    <span className={`px-1 rounded shrink-0 ${getLevelColor(level)}`}>{level}</span>
                    <span className="truncate" title={msg}>{ev.event_type}</span>
                  </div>
                )
              })}
            </div>
          </div>
        )}
        
        <div className="text-sm text-gray-500">
          Full execution history available in Execution Journal page.
        </div>
      </div>
    </div>
  )
}
