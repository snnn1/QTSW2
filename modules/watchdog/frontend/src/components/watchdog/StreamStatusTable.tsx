/**
 * StreamStatusTable component
 * Main stream status table with live timers
 */
import { useState, useEffect } from 'react'
import { formatDuration, computeTimeInState } from '../../utils/timeUtils.ts'
import { useStreamPnl } from '../../hooks/useStreamPnl'
import type { StreamState } from '../../types/watchdog'

interface StreamStatusTableProps {
  streams: StreamState[]
  onStreamClick: (stream: StreamState) => void
  marketOpen?: boolean | null
}

export function StreamStatusTable({ streams, onStreamClick, marketOpen }: StreamStatusTableProps) {
  // Get current trading date from first stream (all streams should have same trading_date)
  const currentTradingDate = streams[0]?.trading_date || new Date().toISOString().split('T')[0]
  const { pnl } = useStreamPnl(currentTradingDate)
  const [, forceUpdate] = useState(0)
  
  // Force re-render every second for live timers
  useEffect(() => {
    const interval = setInterval(() => {
      forceUpdate(prev => prev + 1)
    }, 1000)
    return () => clearInterval(interval)
  }, [])
  
  // Empty state with market-aware messaging
  if (streams.length === 0) {
    if (marketOpen === false) {
      // Market closed - this is expected
      return (
        <div className="bg-gray-800 rounded-lg p-8 text-center">
          <div className="text-gray-400 text-lg mb-2">No active streams</div>
          <div className="text-gray-500 text-sm">Market is closed — this is expected</div>
        </div>
      )
    } else if (marketOpen === true) {
      // Market open but no streams - streams will begin forming ranges when they enter their range windows
      return (
        <div className="bg-gray-800 rounded-lg p-8 text-center">
          <div className="text-amber-500 text-lg mb-2">No active streams</div>
          <div className="text-gray-400 text-sm">Streams will begin forming ranges when they enter their range windows</div>
        </div>
      )
    } else {
      // Market status unknown
      return (
        <div className="bg-gray-800 rounded-lg p-8 text-center text-gray-500">
          No active streams
        </div>
      )
    }
  }
  
  const getStateBadgeColor = (state: string) => {
    if (!state || state === '') {
      return 'bg-gray-700 text-gray-400'
    }
    switch (state) {
      case 'PRE_HYDRATION':
        return 'bg-gray-600 text-white'
      case 'ARMED':
        return 'bg-blue-600 text-white'
      case 'RANGE_BUILDING':
        return 'bg-blue-500 text-white'
      case 'RANGE_LOCKED':
        return 'bg-green-600 text-white'
      case 'DONE':
        return 'bg-gray-500 text-gray-300'
      default:
        return 'bg-gray-600 text-white'
    }
  }
  
  const getTimeInStateColor = (seconds: number) => {
    if (seconds > 600) return 'text-red-500' // >10 min
    if (seconds > 300) return 'text-amber-500' // >5 min
    return 'text-white'
  }
  
  // Sort streams by slot_time_chicago (latest first), then by stream name
  const sortedStreams = [...streams].sort((a, b) => {
    // Parse slot times for comparison
    const parseSlotTime = (slotTime: string | null | undefined): number => {
      if (!slotTime || slotTime === '-' || slotTime === '') return 0
      // Handle ISO format: "2026-01-27T07:30:00-06:00" -> extract "07:30"
      if (slotTime.includes('T')) {
        try {
          const match = slotTime.match(/T(\d{2}):(\d{2})/)
          if (match) {
            const hours = parseInt(match[1], 10)
            const minutes = parseInt(match[2], 10)
            return hours * 60 + minutes // Convert to minutes for comparison
          }
        } catch {}
      }
      // Handle "HH:MM" format
      if (slotTime.includes(':')) {
        try {
          const [hours, minutes] = slotTime.split(':').map(Number)
          if (!isNaN(hours) && !isNaN(minutes)) {
            return hours * 60 + minutes
          }
        } catch {}
      }
      return 0
    }
    
    const aSlot = parseSlotTime(a.slot_time_chicago)
    const bSlot = parseSlotTime(b.slot_time_chicago)
    
    // Sort by slot time descending (latest first), then by stream name ascending
    if (bSlot !== aSlot) {
      return bSlot - aSlot // Descending order (latest slot time first)
    }
    return a.stream.localeCompare(b.stream)
  })
  
  return (
    <div className="bg-gray-800 rounded-lg overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead className="bg-gray-700">
            <tr>
              <th className="px-2 py-1 text-left">Stream</th>
              <th className="px-2 py-1 text-left">Instr</th>
              <th className="px-2 py-1 text-left">Session</th>
              <th className="px-2 py-1 text-left">State</th>
              <th className="px-2 py-1 text-left">Time in State</th>
              <th className="px-2 py-1 text-left">Slot</th>
              <th className="px-2 py-1 text-left">Range</th>
              <th className="px-2 py-1 text-left">PnL</th>
              <th className="px-2 py-1 text-left">Commit</th>
              <th className="px-2 py-1 text-left">Issues</th>
            </tr>
          </thead>
          <tbody>
            {sortedStreams.map((stream) => {
              const timeInState = computeTimeInState(stream.state_entry_time_utc)
              const issues: string[] = []
              
              // Determine issues (simplified - would need more data)
              if (stream.committed && stream.commit_reason === 'RANGE_INVALIDATED') {
                issues.push('⚠️ Range Invalidated')
              }
              
              // Get P&L for this stream
              const streamPnl = pnl[stream.stream]
              
              return (
                <tr
                  key={`${stream.trading_date}_${stream.stream}`}
                  onClick={() => onStreamClick(stream)}
                  className="border-b border-gray-700 hover:bg-gray-750 cursor-pointer"
                >
                  <td className="px-2 py-1 font-mono">{stream.stream}</td>
                  <td className="px-2 py-1">{stream.execution_instrument || stream.instrument || '-'}</td>
                  <td className="px-2 py-1">{stream.session || '-'}</td>
                  <td className="px-2 py-1">
                    {stream.state ? (
                      <span className={`px-2 py-0.5 rounded text-xs ${getStateBadgeColor(stream.state)}`}>
                        {stream.state}
                      </span>
                    ) : (
                      <span className="px-2 py-0.5 rounded text-xs bg-gray-700 text-gray-400">
                        -
                      </span>
                    )}
                  </td>
                  <td className={`px-2 py-1 font-mono ${getTimeInStateColor(timeInState)}`}>
                    {formatDuration(timeInState)}
                  </td>
                  <td className="px-2 py-1 font-mono">
                    {(() => {
                      const slotTime = stream.slot_time_chicago
                      if (!slotTime || slotTime === '' || slotTime === '-') return '-'
                      // Backend may already format to "HH:MM", or it might be ISO format
                      // Handle both cases
                      if (slotTime.includes('T')) {
                        // ISO format: extract time portion
                        try {
                          const match = slotTime.match(/T(\d{2}):(\d{2})/)
                          if (match) return `${match[1]}:${match[2]}`
                        } catch {}
                      }
                      // Already formatted as "HH:MM" or similar
                      return slotTime
                    })()}
                  </td>
                  <td className="px-2 py-1 font-mono whitespace-nowrap">
                    {(() => {
                      const rangeHigh = stream.range_high
                      const rangeLow = stream.range_low
                      // Check for null, undefined, or NaN
                      if (rangeHigh != null && rangeLow != null && 
                          !isNaN(rangeHigh) && !isNaN(rangeLow) &&
                          isFinite(rangeHigh) && isFinite(rangeLow)) {
                        return `${rangeHigh.toFixed(2)} / ${rangeLow.toFixed(2)}`
                      }
                      return '-'
                    })()}
                  </td>
                  <td className="px-2 py-1 font-mono">
                    {(() => {
                      if (!streamPnl) {
                        return <span className="text-gray-500">-</span>
                      }
                      
                      if (streamPnl.open_positions > 0) {
                        return <span className="text-amber-500">OPEN</span>
                      }
                      
                      const realizedPnl = streamPnl.realized_pnl
                      if (realizedPnl === undefined || realizedPnl === null) {
                        return <span className="text-gray-500">-</span>
                      }
                      
                      return (
                        <span className={realizedPnl >= 0 ? 'text-green-500' : 'text-red-500'}>
                          ${realizedPnl.toFixed(2)}
                        </span>
                      )
                    })()}
                  </td>
                  <td className="px-2 py-1">
                    {stream.committed && stream.commit_reason 
                      ? stream.commit_reason 
                      : stream.committed 
                        ? 'COMMITTED' 
                        : '-'}
                  </td>
                  <td className="px-2 py-1">
                    {(() => {
                      const issueList: string[] = []
                      
                      // Range invalidated
                      if (stream.range_invalidated) {
                        issueList.push('⚠️ Range Invalidated')
                      }
                      
                      // Committed with specific reason
                      if (stream.committed && stream.commit_reason === 'RANGE_INVALIDATED') {
                        issueList.push('⚠️ Range Invalidated')
                      }
                      
                      // Note: Removed "Stuck in state" indicator - time in state is already visible in "Time in State" column
                      
                      return issueList.length > 0 ? (
                        <span className="text-amber-500" title={issueList.join(', ')}>
                          {issueList[0]}
                        </span>
                      ) : (
                        '-'
                      )
                    })()}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}
