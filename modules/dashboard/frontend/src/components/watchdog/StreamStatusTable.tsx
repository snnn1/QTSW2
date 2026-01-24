/**
 * StreamStatusTable component
 * Main stream status table with live timers
 */
import { useState, useEffect } from 'react'
import { formatDuration, computeTimeInState } from '../../utils/timeUtils'
import type { StreamState } from '../../types/watchdog'

interface StreamStatusTableProps {
  streams: StreamState[]
  onStreamClick: (stream: StreamState) => void
}

export function StreamStatusTable({ streams, onStreamClick }: StreamStatusTableProps) {
  const [, forceUpdate] = useState(0)
  
  // Force re-render every second for live timers
  useEffect(() => {
    const interval = setInterval(() => {
      forceUpdate(prev => prev + 1)
    }, 1000)
    return () => clearInterval(interval)
  }, [])
  
  // Empty state
  if (streams.length === 0) {
    return (
      <div className="bg-gray-800 rounded-lg p-8 text-center text-gray-500">
        No active streams
      </div>
    )
  }
  
  const getStateBadgeColor = (state: string) => {
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
  
  return (
    <div className="bg-gray-800 rounded-lg overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead className="bg-gray-700">
            <tr>
              <th className="px-4 py-2 text-left">Stream</th>
              <th className="px-4 py-2 text-left">Instr</th>
              <th className="px-4 py-2 text-left">Session</th>
              <th className="px-4 py-2 text-left">State</th>
              <th className="px-4 py-2 text-left">Time in State</th>
              <th className="px-4 py-2 text-left">Slot</th>
              <th className="px-4 py-2 text-left">Range</th>
              <th className="px-4 py-2 text-left">Commit</th>
              <th className="px-4 py-2 text-left">Issues</th>
            </tr>
          </thead>
          <tbody>
            {streams.map((stream) => {
              const timeInState = computeTimeInState(stream.state_entry_time_utc)
              const issues: string[] = []
              
              // Determine issues (simplified - would need more data)
              if (stream.committed && stream.commit_reason === 'RANGE_INVALIDATED') {
                issues.push('⚠️ Range Invalidated')
              }
              
              return (
                <tr
                  key={`${stream.trading_date}_${stream.stream}`}
                  onClick={() => onStreamClick(stream)}
                  className="border-b border-gray-700 hover:bg-gray-750 cursor-pointer"
                >
                  <td className="px-4 py-2 font-mono">{stream.stream}</td>
                  <td className="px-4 py-2">{stream.instrument || '-'}</td>
                  <td className="px-4 py-2">{stream.session || '-'}</td>
                  <td className="px-4 py-2">
                    <span className={`px-2 py-1 rounded text-xs ${getStateBadgeColor(stream.state)}`}>
                      {stream.state}
                    </span>
                  </td>
                  <td className={`px-4 py-2 font-mono ${getTimeInStateColor(timeInState)}`}>
                    {formatDuration(timeInState)}
                  </td>
                  <td className="px-4 py-2 font-mono">{stream.slot_time_chicago || '-'}</td>
                  <td className="px-4 py-2 font-mono">
                    {stream.state === 'RANGE_LOCKED' && stream.range_high && stream.range_low
                      ? `H: ${stream.range_high} / L: ${stream.range_low}`
                      : '-'}
                  </td>
                  <td className="px-4 py-2">{stream.commit_reason || '-'}</td>
                  <td className="px-4 py-2">
                    {issues.length > 0 ? (
                      <span className="text-amber-500" title={issues.join(', ')}>
                        {issues[0]}
                      </span>
                    ) : (
                      '-'
                    )}
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
