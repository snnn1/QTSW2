import { useMemo } from 'react'

/**
 * Analyzer Files Panel component
 * Displays start and finish times for files processed by the analyzer
 */
export function AnalyzerFilesPanel({ events }) {
  // Extract analyzer file processing events
  const fileProcessingEvents = useMemo(() => {
    const fileEvents = events.filter(e => 
      e.stage === 'analyzer' && 
      (e.event === 'file_start' || e.event === 'file_finish')
    )
    
    // Group by instrument and track start/finish
    const instrumentMap = new Map()
    
    fileEvents.forEach(event => {
      const instrument = event.data?.instrument || 'Unknown'
      
      if (!instrumentMap.has(instrument)) {
        instrumentMap.set(instrument, {
          instrument,
          startTime: null,
          finishTime: null,
          startTimestamp: null,
          finishTimestamp: null,
          duration: null,
          status: 'processing',
          error: null
        })
      }
      
      const info = instrumentMap.get(instrument)
      
      if (event.event === 'file_start') {
        info.startTime = event.data?.start_time || event.timestamp || 'Unknown'
        info.startTimestamp = event.data?.start_timestamp || (event.timestamp ? new Date(event.timestamp).getTime() / 1000 : null)
        info.status = 'processing'
      } else if (event.event === 'file_finish') {
        info.finishTime = event.data?.finish_time || event.timestamp || 'Unknown'
        info.finishTimestamp = event.data?.finish_timestamp || (event.timestamp ? new Date(event.timestamp).getTime() / 1000 : null)
        info.duration = event.data?.duration_seconds || (info.finishTimestamp && info.startTimestamp ? info.finishTimestamp - info.startTimestamp : null)
        info.status = event.data?.status || 'completed'
        info.error = event.data?.error || null
      }
    })
    
    // Convert to array and sort by start time (most recent first)
    return Array.from(instrumentMap.values())
      .sort((a, b) => {
        const aTime = a.startTimestamp || 0
        const bTime = b.startTimestamp || 0
        return bTime - aTime
      })
  }, [events])
  
  // Format duration
  const formatDuration = (seconds) => {
    if (!seconds) return 'N/A'
    if (seconds < 60) return `${seconds.toFixed(1)}s`
    const minutes = Math.floor(seconds / 60)
    const secs = seconds % 60
    return `${minutes}m ${secs.toFixed(0)}s`
  }
  
  // Format timestamp
  const formatTimestamp = (timestamp) => {
    if (!timestamp) return 'N/A'
    try {
      // If it's a string timestamp, parse it
      if (typeof timestamp === 'string') {
        const date = new Date(timestamp)
        if (isNaN(date.getTime())) {
          // Try parsing as "YYYY-MM-DD HH:MM:SS"
          return timestamp
        }
        return date.toLocaleString('en-US', {
          month: 'short',
          day: 'numeric',
          year: 'numeric',
          hour: '2-digit',
          minute: '2-digit',
          second: '2-digit',
          hour12: true
        })
      }
      // If it's a Unix timestamp (seconds)
      if (typeof timestamp === 'number') {
        const date = new Date(timestamp * 1000)
        return date.toLocaleString('en-US', {
          month: 'short',
          day: 'numeric',
          year: 'numeric',
          hour: '2-digit',
          minute: '2-digit',
          second: '2-digit',
          hour12: true
        })
      }
      return timestamp
    } catch (e) {
      return timestamp
    }
  }
  
  // Get status badge class
  const getStatusClass = (status) => {
    switch (status) {
      case 'success':
        return 'bg-green-600 text-white'
      case 'failed':
      case 'exception':
        return 'bg-red-600 text-white'
      case 'processing':
        return 'bg-yellow-600 text-white animate-pulse'
      default:
        return 'bg-gray-600 text-white'
    }
  }
  
  if (fileProcessingEvents.length === 0) {
    return (
      <div className="bg-gray-900 rounded-lg p-4 border border-gray-700">
        <h2 className="text-xl font-semibold text-gray-300 mb-4">Analyzer File Processing</h2>
        <div className="text-gray-500 text-sm">No file processing events yet...</div>
      </div>
    )
  }
  
  return (
    <div className="bg-gray-900 rounded-lg p-4 border border-gray-700">
      <h2 className="text-xl font-semibold text-gray-300 mb-4">Analyzer File Processing</h2>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-700">
              <th className="text-left py-2 px-3 text-gray-400 font-semibold">Instrument</th>
              <th className="text-left py-2 px-3 text-gray-400 font-semibold">Start Time</th>
              <th className="text-left py-2 px-3 text-gray-400 font-semibold">Finish Time</th>
              <th className="text-left py-2 px-3 text-gray-400 font-semibold">Duration</th>
              <th className="text-left py-2 px-3 text-gray-400 font-semibold">Status</th>
            </tr>
          </thead>
          <tbody>
            {fileProcessingEvents.map((file, index) => (
              <tr key={`${file.instrument}-${index}`} className="border-b border-gray-800 hover:bg-gray-800">
                <td className="py-2 px-3 text-gray-200 font-mono font-semibold">{file.instrument}</td>
                <td className="py-2 px-3 text-gray-300">{formatTimestamp(file.startTime)}</td>
                <td className="py-2 px-3 text-gray-300">
                  {file.finishTime ? formatTimestamp(file.finishTime) : <span className="text-gray-500 italic">Processing...</span>}
                </td>
                <td className="py-2 px-3 text-gray-300">{formatDuration(file.duration)}</td>
                <td className="py-2 px-3">
                  <span className={`px-2 py-1 rounded text-xs font-semibold ${getStatusClass(file.status)}`}>
                    {file.status === 'processing' ? 'Processing' : 
                     file.status === 'success' ? 'Success' :
                     file.status === 'failed' ? 'Failed' :
                     file.status === 'exception' ? 'Exception' : file.status}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {fileProcessingEvents.some(f => f.status === 'processing') && (
        <div className="mt-3 text-xs text-gray-400">
          <span className="inline-block w-2 h-2 bg-yellow-600 rounded-full animate-pulse mr-1"></span>
          Some files are still processing...
        </div>
      )}
    </div>
  )
}











