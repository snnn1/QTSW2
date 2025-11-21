import { useState, useEffect, useRef, useCallback } from 'react'
import './App.css'

const API_BASE = 'http://localhost:8000/api'

const STREAMS = ['ES1', 'ES2', 'GC1', 'GC2', 'CL1', 'CL2', 'NQ1', 'NQ2', 'NG1', 'NG2', 'YM1', 'YM2']
const DAYS_OF_WEEK = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
const AVAILABLE_TIMES = ['07:30', '08:00', '09:00', '09:30', '10:00', '10:30', '11:00']
const DISPLAY_COLUMNS = ['Date', 'Time', 'Symbol', 'Stream', 'Session', 'Result', 'Profit', 'Direction', 'Peak', 'Target', 'Range']

function App() {
  const [activeTab, setActiveTab] = useState('master') // 'master' or stream ID
  
  // Master matrix data
  const [masterData, setMasterData] = useState([])
  const [masterLoading, setMasterLoading] = useState(false)
  const [masterError, setMasterError] = useState(null)
  
  // Per-stream filters (persisted in localStorage)
  const [streamFilters, setStreamFilters] = useState(() => {
    const saved = localStorage.getItem('matrix_stream_filters')
    if (saved) {
      try {
        return JSON.parse(saved)
      } catch {
        return {}
      }
    }
    return {}
  })
  
  // Infinite scroll
  const [visibleRows, setVisibleRows] = useState(100)
  const observerRef = useRef(null)
  const lastRowRef = useCallback(node => {
    if (masterLoading) return
    if (observerRef.current) observerRef.current.disconnect()
    observerRef.current = new IntersectionObserver(entries => {
      if (entries[0].isIntersecting) {
        setVisibleRows(prev => prev + 50)
      }
    })
    if (node) observerRef.current.observe(node)
  }, [masterLoading])
  
  // Load master matrix on mount (don't rebuild, just load existing)
  useEffect(() => {
    // Wait a bit for backend to be ready, then try loading
    const timer = setTimeout(() => {
      loadMasterMatrix(false)
    }, 1000)
    
    return () => clearTimeout(timer)
  }, [])
  
  // Retry loading if backend wasn't ready
  const retryLoad = () => {
    loadMasterMatrix(false)
  }
  
  // Reload data when filters change (but don't rebuild - filters are applied client-side)
  useEffect(() => {
    // Filters are applied client-side, so we don't need to reload
    // But we could trigger a visual update
  }, [streamFilters])
  
  // Save filters to localStorage whenever they change
  useEffect(() => {
    localStorage.setItem('matrix_stream_filters', JSON.stringify(streamFilters))
  }, [streamFilters])
  
  const loadMasterMatrix = async (rebuild = false) => {
    setMasterLoading(true)
    setMasterError(null)
    
    try {
      // Check if backend is reachable
      try {
        const controller = new AbortController()
        const timeoutId = setTimeout(() => controller.abort(), 3000)
        const healthCheck = await fetch(`${API_BASE.replace('/api', '')}/`, { 
          method: 'GET', 
          signal: controller.signal 
        })
        clearTimeout(timeoutId)
      } catch (e) {
        if (e.name === 'AbortError') {
          setMasterError('Backend connection timeout. Make sure the dashboard backend is running on http://localhost:8000')
        } else {
          setMasterError('Backend not running. Please start the dashboard backend on port 8000. Error: ' + e.message)
        }
        setMasterData([])
        setMasterLoading(false)
        return
      }
      
      // If rebuild requested, build matrix first
      if (rebuild) {
        // Build stream filters for API
        const streamFiltersApi = {}
        Object.keys(streamFilters).forEach(streamId => {
          const filters = streamFilters[streamId]
          if (filters && (filters.exclude_times?.length > 0 || filters.exclude_days_of_week?.length > 0 || filters.exclude_days_of_month?.length > 0)) {
            streamFiltersApi[streamId] = {
              exclude_days_of_week: filters.exclude_days_of_week || [],
              exclude_days_of_month: filters.exclude_days_of_month || [],
              exclude_times: filters.exclude_times || []
            }
          }
        })
        
        const buildResponse = await fetch(`${API_BASE}/matrix/build`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            stream_filters: Object.keys(streamFiltersApi).length > 0 ? streamFiltersApi : null
          })
        })
        
        if (!buildResponse.ok) {
          const errorData = await buildResponse.json()
          setMasterError(errorData.detail || 'Failed to build master matrix')
          setMasterLoading(false)
          return
        }
      }
      
      // Load the matrix data
      const dataResponse = await fetch(`${API_BASE}/matrix/data?limit=50000`)
      
      if (!dataResponse.ok) {
        const errorData = await dataResponse.json()
        setMasterError(errorData.detail || 'Failed to load matrix data')
        setMasterData([])
        setMasterLoading(false)
        return
      }
      
      const data = await dataResponse.json()
      
      if (data.data && data.data.length > 0) {
        setMasterData(data.data)
        setMasterError(null)
      } else {
        setMasterData([])
        setMasterError('No data found. Click "Rebuild Matrix" to build it.')
      }
    } catch (error) {
      if (error.name === 'TypeError' && error.message.includes('fetch')) {
        setMasterError('Cannot connect to backend. Make sure the dashboard backend is running on http://localhost:8000')
      } else {
        setMasterError('Failed to load master matrix: ' + error.message)
      }
      setMasterData([])
    } finally {
      setMasterLoading(false)
    }
  }
  
  const updateStreamFilter = (streamId, filterType, value) => {
    setStreamFilters(prev => {
      const updated = { ...prev }
      if (!updated[streamId]) {
        updated[streamId] = {
          exclude_days_of_week: [],
          exclude_days_of_month: [],
          exclude_times: []
        }
      }
      
      if (filterType === 'exclude_days_of_week') {
        const current = updated[streamId].exclude_days_of_week || []
        if (current.includes(value)) {
          updated[streamId].exclude_days_of_week = current.filter(d => d !== value)
        } else {
          updated[streamId].exclude_days_of_week = [...current, value]
        }
      } else if (filterType === 'exclude_days_of_month') {
        const current = updated[streamId].exclude_days_of_month || []
        const numValue = parseInt(value)
        if (current.includes(numValue)) {
          updated[streamId].exclude_days_of_month = current.filter(d => d !== numValue)
        } else {
          updated[streamId].exclude_days_of_month = [...current, numValue]
        }
      } else if (filterType === 'exclude_times') {
        const current = updated[streamId].exclude_times || []
        if (current.includes(value)) {
          updated[streamId].exclude_times = current.filter(t => t !== value)
        } else {
          updated[streamId].exclude_times = [...current, value]
        }
      }
      
      return updated
    })
  }
  
  const getFilteredData = (data, streamId = null) => {
    let filtered = [...data]
    
    // Filter by stream if specified
    if (streamId) {
      filtered = filtered.filter(row => row.Stream === streamId)
    }
    
    // Apply stream-specific filters
    if (streamId && streamFilters[streamId]) {
      const filters = streamFilters[streamId]
      
      // Day of week filter
      if (filters.exclude_days_of_week?.length > 0) {
        filtered = filtered.filter(row => {
          const date = new Date(row.Date)
          const dayName = date.toLocaleDateString('en-US', { weekday: 'long' })
          return !filters.exclude_days_of_week.includes(dayName)
        })
      }
      
      // Day of month filter
      if (filters.exclude_days_of_month?.length > 0) {
        filtered = filtered.filter(row => {
          const date = new Date(row.Date)
          return !filters.exclude_days_of_month.includes(date.getDate())
        })
      }
      
      // Time filter
      if (filters.exclude_times?.length > 0) {
        filtered = filtered.filter(row => !filters.exclude_times.includes(row.Time))
      }
    }
    
    // Sort: Date (newest first), then Time (earliest first)
    filtered.sort((a, b) => {
      const dateA = new Date(a.Date)
      const dateB = new Date(b.Date)
      
      // First by date (newest first)
      if (dateB.getTime() !== dateA.getTime()) {
        return dateB.getTime() - dateA.getTime()
      }
      
      // Then by time (earliest first)
      const timeA = a.Time || '00:00'
      const timeB = b.Time || '00:00'
      if (timeA !== timeB) {
        return timeA.localeCompare(timeB)
      }
      
      // Then by symbol/stream
      if (a.Symbol !== b.Symbol) {
        return (a.Symbol || '').localeCompare(b.Symbol || '')
      }
      
      return (a.Stream || '').localeCompare(b.Stream || '')
    })
    
    return filtered
  }
  
  const getStreamFilters = (streamId) => {
    return streamFilters[streamId] || {
      exclude_days_of_week: [],
      exclude_days_of_month: [],
      exclude_times: []
    }
  }
  
  const renderFilters = (streamId) => {
    const filters = getStreamFilters(streamId)
    const hasFilters = filters.exclude_days_of_week.length > 0 || 
                      filters.exclude_days_of_month.length > 0 || 
                      filters.exclude_times.length > 0
    
    return (
      <div className="bg-gray-700 rounded-lg p-4 mb-4">
        <div className="flex items-center justify-between mb-3">
          <h4 className="font-medium text-sm">Filters for {streamId}</h4>
          {hasFilters && (
            <span className="text-xs bg-blue-600 px-2 py-1 rounded">Active</span>
          )}
        </div>
        
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          {/* Days of Week */}
          <div>
            <label className="block text-xs font-medium mb-2 text-gray-400">Exclude Days</label>
            <div className="flex flex-wrap gap-1">
              {DAYS_OF_WEEK.map(dow => (
                <button
                  key={dow}
                  onClick={() => updateStreamFilter(streamId, 'exclude_days_of_week', dow)}
                  className={`px-2 py-1 text-xs rounded ${
                    filters.exclude_days_of_week.includes(dow)
                      ? 'bg-red-600 text-white'
                      : 'bg-gray-600 hover:bg-gray-500'
                  }`}
                >
                  {dow.substring(0, 3)}
                </button>
              ))}
            </div>
          </div>
          
          {/* Days of Month */}
          <div>
            <label className="block text-xs font-medium mb-2 text-gray-400">Exclude DOM</label>
            <div className="flex flex-wrap gap-1">
              {[4, 16, 30].map(dom => (
                <button
                  key={dom}
                  onClick={() => updateStreamFilter(streamId, 'exclude_days_of_month', dom)}
                  className={`px-2 py-1 text-xs rounded ${
                    filters.exclude_days_of_month.includes(dom)
                      ? 'bg-red-600 text-white'
                      : 'bg-gray-600 hover:bg-gray-500'
                  }`}
                >
                  {dom}
                </button>
              ))}
              <input
                type="number"
                min="1"
                max="31"
                placeholder="Custom"
                onKeyPress={(e) => {
                  if (e.key === 'Enter') {
                    const value = parseInt(e.target.value)
                    if (value >= 1 && value <= 31) {
                      updateStreamFilter(streamId, 'exclude_days_of_month', value)
                      e.target.value = ''
                    }
                  }
                }}
                className="w-16 px-2 py-1 text-xs bg-gray-600 border border-gray-500 rounded"
              />
            </div>
          </div>
          
          {/* Times */}
          <div>
            <label className="block text-xs font-medium mb-2 text-gray-400">Exclude Times</label>
            <div className="flex flex-wrap gap-1">
              {AVAILABLE_TIMES.map(time => (
                <button
                  key={time}
                  onClick={() => updateStreamFilter(streamId, 'exclude_times', time)}
                  className={`px-2 py-1 text-xs rounded font-mono ${
                    filters.exclude_times.includes(time)
                      ? 'bg-red-600 text-white'
                      : 'bg-gray-600 hover:bg-gray-500'
                  }`}
                >
                  {time}
                </button>
              ))}
            </div>
          </div>
        </div>
      </div>
    )
  }
  
  const renderDataTable = (data, streamId = null) => {
    const filtered = getFilteredData(data, streamId)
    const visible = filtered.slice(0, visibleRows)
    
    if (filtered.length === 0) {
      return (
        <div className="text-center py-8 text-gray-400">
          No data available{streamId ? ` for ${streamId}` : ''}
        </div>
      )
    }
    
    return (
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-700 bg-gray-800">
              {DISPLAY_COLUMNS.map(col => (
                <th key={col} className="text-left p-2 font-medium">{col}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {visible.map((row, idx) => (
              <tr
                key={idx}
                ref={idx === visible.length - 1 ? lastRowRef : null}
                className={`border-b border-gray-700 hover:bg-gray-800 ${
                  row.final_allowed === false ? 'bg-red-900/20' : ''
                }`}
              >
                {DISPLAY_COLUMNS.map(col => {
                  let value = row[col]
                  if (col === 'Symbol' && !value) {
                    value = row['Instrument'] || ''
                  }
                  if (col === 'Date' && value) {
                    value = new Date(value).toLocaleDateString()
                  }
                  // Format numeric columns
                  if (['Profit', 'Peak', 'Target', 'Range'].includes(col) && value !== null && value !== undefined) {
                    const numValue = parseFloat(value)
                    if (!isNaN(numValue)) {
                      value = numValue.toFixed(2)
                    }
                  }
                  return (
                    <td key={col} className="p-2">
                      {value !== null && value !== undefined ? String(value) : '-'}
                    </td>
                  )
                })}
              </tr>
            ))}
          </tbody>
        </table>
        {visible.length < filtered.length && (
          <div className="text-center py-4 text-gray-400 text-sm">
            Showing {visible.length} of {filtered.length} rows (scroll for more)
          </div>
        )}
      </div>
    )
  }
  
  return (
    <div className="min-h-screen bg-gray-900 text-gray-100">
      <div className="container mx-auto px-4 py-8">
        <h1 className="text-3xl font-bold mb-8">Master Matrix</h1>
        
        {/* Tabs */}
        <div className="flex gap-2 mb-6 border-b border-gray-700 overflow-x-auto">
          <button
            onClick={() => setActiveTab('master')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'master'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Master (All Streams)
          </button>
          {STREAMS.map(stream => (
            <button
              key={stream}
              onClick={() => setActiveTab(stream)}
              className={`px-4 py-2 font-medium whitespace-nowrap ${
                activeTab === stream
                  ? 'border-b-2 border-blue-500 text-blue-400'
                  : 'text-gray-400 hover:text-gray-300'
              }`}
            >
              {stream}
            </button>
          ))}
        </div>
        
        {/* Content */}
        {activeTab === 'master' ? (
          <div className="space-y-4">
            <div className="bg-gray-800 rounded-lg p-6">
              <div className="flex items-center justify-between mb-4">
                <div>
                  <h2 className="text-xl font-semibold">All Streams Combined</h2>
                  <p className="text-sm text-gray-400 mt-1">
                    Sorted by: Date (newest first), Time (earliest first)
                  </p>
                </div>
                <button
                  onClick={() => loadMasterMatrix(true)}
                  disabled={masterLoading}
                  className={`px-4 py-2 rounded font-medium text-sm ${
                    masterLoading
                      ? 'bg-gray-600 cursor-not-allowed'
                      : 'bg-blue-600 hover:bg-blue-700'
                  }`}
                >
                  {masterLoading ? 'Loading...' : 'Rebuild Matrix'}
                </button>
              </div>
              
              {masterLoading ? (
                <div className="text-center py-8">Loading master matrix...</div>
              ) : masterError ? (
                <div className="text-center py-8 text-red-400">
                  <div className="mb-4">{masterError}</div>
                  <div className="flex gap-2 justify-center">
                    <button
                      onClick={retryLoad}
                      className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded"
                    >
                      Retry Load
                    </button>
                    {masterError.includes('No data') && (
                      <button
                        onClick={() => loadMasterMatrix(true)}
                        className="px-4 py-2 bg-green-600 hover:bg-green-700 rounded"
                      >
                        Build Matrix
                      </button>
                    )}
                  </div>
                </div>
              ) : (
                <>
                  <div className="mb-4 text-sm text-gray-400">
                    Showing {Math.min(visibleRows, getFilteredData(masterData).length)} of {getFilteredData(masterData).length} trades
                  </div>
                  {renderDataTable(masterData)}
                </>
              )}
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            <div className="bg-gray-800 rounded-lg p-6">
              <h2 className="text-xl font-semibold mb-4">Stream: {activeTab}</h2>
              
              {/* Filters */}
              {renderFilters(activeTab)}
              
              {/* Data Table */}
              {masterLoading ? (
                <div className="text-center py-8">Loading data...</div>
              ) : masterError ? (
                <div className="text-center py-8 text-red-400">
                  <div className="mb-4">{masterError}</div>
                  <div className="flex gap-2 justify-center">
                    <button
                      onClick={retryLoad}
                      className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded"
                    >
                      Retry Load
                    </button>
                    {masterError.includes('No data') && (
                      <button
                        onClick={() => loadMasterMatrix(true)}
                        className="px-4 py-2 bg-green-600 hover:bg-green-700 rounded"
                      >
                        Build Matrix
                      </button>
                    )}
                  </div>
                </div>
              ) : (
                <>
                  <div className="mb-4 text-sm text-gray-400">
                    Showing {Math.min(visibleRows, getFilteredData(masterData, activeTab).length)} of {getFilteredData(masterData, activeTab).length} trades
                  </div>
                  {renderDataTable(masterData, activeTab)}
                </>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

export default App
