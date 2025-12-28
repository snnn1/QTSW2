import { useState, useEffect, useRef, useCallback, useMemo } from 'react'
import { List } from 'react-window'
import './App.css'
import { useMatrixWorker } from './useMatrixWorker'
import { STREAMS, DAYS_OF_WEEK, AVAILABLE_TIMES, ANALYZER_COLUMN_ORDER, DEFAULT_COLUMNS } from './utils/constants'
import { getDefaultFilters, loadAllFilters, saveAllFilters, getStreamFiltersFromStorage } from './utils/filterUtils'
// Use existing utility files instead of duplicating code
import { 
  getContractValue, 
  parseDateValue,
  calculateTimeProfit,
  calculateDailyProfit,
  calculateDOMProfit,
  calculateDateProfit,
  calculateMonthlyProfit,
  calculateYearlyProfit
} from './utils/profitCalculations'
import { calculateStats as calculateStatsUtil } from './utils/statsCalculations'
// Use existing hooks
import { useMatrixFilters } from './hooks/useMatrixFilters'
import { useMatrixData } from './hooks/useMatrixData'
import { useColumnSelection } from './hooks/useColumnSelection'

// API base URL - can be overridden via environment variable
const API_PORT = import.meta.env.VITE_API_PORT || '8000'
const API_BASE = `http://localhost:${API_PORT}/api`

function App() {
  // Error boundary - catch any initialization errors
  try {
    return <AppContent />
  } catch (error) {
    console.error('App initialization error:', error)
    return (
      <div className="min-h-screen bg-black text-white p-8">
        <h1 className="text-2xl font-bold mb-4 text-red-400">Error Loading App</h1>
        <p className="text-gray-300 mb-2">Error: {error.message}</p>
        <pre className="bg-gray-900 p-4 rounded text-sm overflow-auto">{error.stack}</pre>
      </div>
    )
  }
}

function AppContent() {
  const [activeTab, setActiveTab] = useState('timetable') // 'timetable', 'master', or stream ID
  const [currentTime, setCurrentTime] = useState(new Date())
  const [lastMergeTime, setLastMergeTime] = useState(null)
  
  // Track if initial load has been attempted to prevent reload loops
  const hasLoadedRef = useRef(false)
  
  // Web Worker for all heavy computations
  const {
    workerReady,
    filteredLength,
    filterMask,
    filteredIndices: workerFilteredIndices,
    filteredRows: workerFilteredRows,
    stats: workerStats,
    statsLoading,
    profitBreakdown: workerProfitBreakdown,
    breakdownType: workerBreakdownType,
    breakdownLoading: workerBreakdownLoading,
    timetable: workerTimetable,
    timetableLoading: workerTimetableLoading,
    error: workerError,
    initData: workerInitData,
    filter: workerFilter,
    calculateStats: workerCalculateStats,
    getRows: workerGetRows,
    calculateProfitBreakdown,
    calculateTimetable: workerCalculateTimetable
  } = useMatrixWorker()
  
  // Use existing hooks for filter state management
  const {
    streamFilters,
    setStreamFilters,
    getFiltersForStream
  } = useMatrixFilters()
  
  // Backend readiness state
  const [backendReady, setBackendReady] = useState(false)
  const [backendConnecting, setBackendConnecting] = useState(true)
  const [backendConnectionError, setBackendConnectionError] = useState(null)
  
  // Keep local state for complex logic (loadMasterMatrix, toggleColumn, etc. have complex implementations)
  const [masterData, setMasterData] = useState([])
  const [masterLoading, setMasterLoading] = useState(false)
  const [masterError, setMasterError] = useState(null)
  const [availableYearsFromAPI, setAvailableYearsFromAPI] = useState([])
  
  // Per-stream selected columns (persisted in localStorage)
  const [selectedColumns, setSelectedColumns] = useState(() => {
    const saved = localStorage.getItem('matrix_selected_columns')
    if (saved) {
      try {
        return JSON.parse(saved)
      } catch {
        return {}
      }
    }
    return {}
  })
  
  // Column selector visibility
  const [showColumnSelector, setShowColumnSelector] = useState(false)
  
  // Show/Hide filtered days toggle (default: ON/show filtered days)
  const [showFilteredDays, setShowFilteredDays] = useState(() => {
    const saved = localStorage.getItem('matrix_show_filtered_days')
    if (saved !== null) {
      return saved === 'true'
    }
    return true // Default: show filtered days
  })
  
  // Save showFilteredDays to localStorage when it changes
  useEffect(() => {
    localStorage.setItem('matrix_show_filtered_days', String(showFilteredDays))
  }, [showFilteredDays])
  
  // Filtered data indices (from worker mask)
  const [filteredIndices, setFilteredIndices] = useState([])
  
  // Available columns (detected from data)
  const [availableColumns, setAvailableColumns] = useState([])
  
  // Include filtered executed trades in stats (default: true)
  const [includeFilteredExecuted, setIncludeFilteredExecuted] = useState(() => {
    const saved = localStorage.getItem('matrix_include_filtered_executed')
    return saved !== null ? JSON.parse(saved) : true
  })
  
  // Stats visibility per stream
  const [showStats, setShowStats] = useState(() => {
    const saved = localStorage.getItem('matrix_show_stats')
    if (saved) {
      try {
        return JSON.parse(saved)
      } catch {
        return {}
      }
    }
    return {}
  })
  
  // Per-stream filters (persisted in localStorage)
  // Contract multiplier for master stream (default 1 contract)
  const [masterContractMultiplier, setMasterContractMultiplier] = useState(() => {
    const saved = localStorage.getItem('matrix_master_contract_multiplier')
    return saved ? parseFloat(saved) || 1 : 1
  })
  // Temporary input value for multiplier (doesn't trigger recalculations)
  const [multiplierInput, setMultiplierInput] = useState(() => {
    const saved = localStorage.getItem('matrix_master_contract_multiplier')
    return saved ? parseFloat(saved) || 1 : 1
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
  
  // Load master matrix function - defined early so it can be used in useEffect
  const loadMasterMatrix = useCallback(async (rebuild = false, rebuildStream = null) => {
    setMasterLoading(true)
    setMasterError(null)
    
    // Preserve existing data during rebuild/load - only replace when new data is successfully loaded
    const hadExistingData = masterData.length > 0
    
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
          setMasterError(`Backend connection timeout. Make sure the dashboard backend is running on http://localhost:${API_PORT}`)
        } else {
          setMasterError(`Backend not running. Please start the dashboard backend on port ${API_PORT}. Error: ` + e.message)
        }
        // Only clear data if we had no existing data (initial load failure)
        if (!hadExistingData) {
          setMasterData([])
        }
        setMasterLoading(false)
        return
      }
      
      // If rebuild requested, build matrix first
      if (rebuild) {
        // Build stream filters for API
        const streamFiltersApi = {}
        Object.keys(streamFilters).forEach(streamId => {
          const filters = streamFilters[streamId]
          if (filters) {
            streamFiltersApi[streamId] = {
              exclude_days_of_week: filters.exclude_days_of_week || [],
              exclude_days_of_month: filters.exclude_days_of_month || [],
              exclude_times: filters.exclude_times || []
            }
          }
        })
        
        const visibleYearsSet = new Set()
        Object.keys(streamFilters).forEach(id => {
          const f = streamFilters[id]
          if (f && Array.isArray(f.include_years)) {
            f.include_years.forEach(y => {
              const num = parseInt(y)
              if (!isNaN(num)) {
                visibleYearsSet.add(num)
              }
            })
          }
        })
        const visibleYears = Array.from(visibleYearsSet).sort((a, b) => a - b)
        
        const buildBody = {
          stream_filters: Object.keys(streamFiltersApi).length > 0 ? streamFiltersApi : null
        }
        if (visibleYears.length > 0) {
          buildBody.visible_years = visibleYears
        }
        buildBody.warmup_months = 1
        if (rebuildStream) {
          buildBody.streams = [rebuildStream]
        }
        
        try {
          const buildResponse = await fetch(`${API_BASE}/matrix/build`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(buildBody)
          })
          
          if (!buildResponse.ok) {
            const errorData = await buildResponse.json()
            setMasterError(errorData.detail || 'Failed to build master matrix')
            // Preserve existing data if rebuild fails
            setMasterLoading(false)
            return
          }
          
          await buildResponse.json()
        } catch (error) {
          setMasterError(`Failed to build master matrix: ${error.message}`)
          // Preserve existing data if rebuild fails
          setMasterLoading(false)
          return
        }
      }
      
      // Load the matrix data (NO LIMIT - load all trades)
      const dataResponse = await fetch(`${API_BASE}/matrix/data?limit=0&essential_columns_only=true&skip_cleaning=true`)
      
      if (!dataResponse.ok) {
        const errorData = await dataResponse.json()
        setMasterError(errorData.detail || 'Failed to load matrix data')
        // Only clear data if we had no existing data (initial load failure)
        if (!hadExistingData) {
          setMasterData([])
        }
        setMasterLoading(false)
        return
      }
      
      const data = await dataResponse.json()
      const trades = data.data || []
      
      if (data.years && Array.isArray(data.years) && data.years.length > 0) {
        setAvailableYearsFromAPI(data.years)
      }
      
      if (trades.length > 0) {
        // Only now do we replace the data - new data is successfully loaded
        setMasterData(trades)
        setLastMergeTime(new Date()) // Track when merge data was received
        
        if (trades.length > 0) {
          workerInitData(trades)
        }
        
        if (availableColumns.length === 0) {
          const cols = Object.keys(trades[0])
          const hiddenColumns = [
            'global_trade_id', 'filter_reasons', 'onr_high', 'onr_low', 'onr',
            'scf_s1', 'scf_s2', 'prewindow_high_s1', 'prewindow_low_s1', 'prewindow_range_s1',
            'session_high_s1', 'session_low_s1', 'session_range_s1', 'prewindow_high_s2',
            'prewindow_low_s2', 'prewindow_range_s2', 'session_high_s2', 'session_low_s2',
            'session_range_s2', 'onr_q1', 'onr_q2', 'onr_q3', 'onr_bucket', 'entry_time',
            'exit_time', 'entry_price', 'exit_price', 'R', 'pnl', 'rs_value', 'selected_time',
            'time_bucket', 'trade_date', 'day_of_month', 'dow', 'dow_full', 'month',
            'session_index', 'is_two_stream', 'dom_blocked', 'final_allowed', 'SL'  // Hide old SL column, use StopLoss instead
          ]
          const displayableCols = cols.filter(col => {
            if (col.includes(' Points') || col.includes(' Rolling')) {
              return true
            }
            return !col.startsWith('_') && !hiddenColumns.includes(col)
          })
          
          if (displayableCols.includes('Profit') && !displayableCols.includes('Profit ($)')) {
            displayableCols.push('Profit ($)')
          }
          
          DEFAULT_COLUMNS.forEach(col => {
            if (!displayableCols.includes(col)) {
              displayableCols.push(col)
            }
          })
          
          const excludedFromDefault = ['Revised Score', 'Revised Profit ($)']
          setAvailableColumns(displayableCols)
          
          setSelectedColumns(prev => {
            const updated = { ...prev }
            let changed = false
            
            const getDefaultColumns = () => {
              return DEFAULT_COLUMNS.filter(col => !excludedFromDefault.includes(col))
            }
            
            // Remove 'SL' from selected columns if it exists (replaced by 'StopLoss')
            Object.keys(updated).forEach(tabId => {
              if (Array.isArray(updated[tabId]) && updated[tabId].includes('SL')) {
                updated[tabId] = updated[tabId].filter(col => col !== 'SL')
                changed = true
              }
            })
            
            const defaultCols = getDefaultColumns()
            if (JSON.stringify(updated['master']) !== JSON.stringify(defaultCols)) {
              updated['master'] = defaultCols
              changed = true
            }
            
            STREAMS.forEach(stream => {
              if (!updated[stream] || updated[stream].length === 0) {
                updated[stream] = getDefaultColumns()
                changed = true
              }
            })
            
            if (changed) {
              localStorage.setItem('matrix_selected_columns', JSON.stringify(updated))
            }
            return updated
          })
        }
        
        setMasterError(null)
      } else {
        // Only clear data if we had no existing data
        if (!hadExistingData) {
          setMasterData([])
          setMasterError('No data found. Click "Rebuild Matrix" to build it.')
        } else {
          // If we had existing data but new load returned empty, keep existing data and show warning
          setMasterError('Warning: Load returned no data. Previous data preserved.')
        }
      }
    } catch (error) {
      if (error.name === 'TypeError' && error.message.includes('fetch')) {
        setMasterError(`Cannot connect to backend. Make sure the dashboard backend is running on http://localhost:${API_PORT}`)
      } else {
        setMasterError('Failed to load master matrix: ' + error.message)
      }
      // Only clear data if we had no existing data (initial load failure)
      if (!hadExistingData) {
        setMasterData([])
      }
    } finally {
      setMasterLoading(false)
    }
  }, [streamFilters, workerInitData, setMasterData, setMasterLoading, setMasterError, setAvailableYearsFromAPI, availableColumns, setAvailableColumns, setSelectedColumns])
  
  // Retry loading if backend wasn't ready
  const retryLoad = useCallback(() => {
    loadMasterMatrix(false)
  }, [loadMasterMatrix])

  // Backend readiness polling - check if backend is ready before loading data
  useEffect(() => {
    let pollInterval = null
    let timeoutId = null
    let isCancelled = false
    let isReady = false // Track ready state with local variable
    
    const pollBackend = async () => {
      if (isCancelled || isReady) return false
      
      try {
        const controller = new AbortController()
        const requestTimeout = setTimeout(() => controller.abort(), 2000) // 2s timeout per request
        
        const response = await fetch(`${API_BASE}/matrix/test`, {
          method: 'GET',
          signal: controller.signal
        })
        
        clearTimeout(requestTimeout)
        
        if (response.ok) {
          // Backend is ready
          if (!isCancelled && !isReady) {
            isReady = true
            setBackendReady(true)
            setBackendConnecting(false)
            setBackendConnectionError(null)
            
            // Clear polling
            if (pollInterval) {
              clearInterval(pollInterval)
              pollInterval = null
            }
            if (timeoutId) {
              clearTimeout(timeoutId)
              timeoutId = null
            }
          }
          return true
        }
      } catch (error) {
        // Backend not ready yet, continue polling
        if (error.name !== 'AbortError') {
          console.debug('Backend not ready yet, continuing to poll...', error.message)
        }
      }
      return false
    }
    
    // Start polling immediately, then every 500ms
    pollBackend().then(ready => {
      if (!ready && !isCancelled && !isReady) {
        pollInterval = setInterval(() => {
          pollBackend()
        }, 500)
      }
    })
    
    // Set timeout for max retry window (12 seconds)
    timeoutId = setTimeout(() => {
      if (!isCancelled && !isReady) {
        setBackendConnecting(false)
        setBackendConnectionError('Backend did not respond within 12 seconds. Please check if the backend is running.')
        if (pollInterval) {
          clearInterval(pollInterval)
          pollInterval = null
        }
      }
    }, 12000)
    
    return () => {
      isCancelled = true
      if (pollInterval) {
        clearInterval(pollInterval)
      }
      if (timeoutId) {
        clearTimeout(timeoutId)
      }
    }
  }, []) // Only run once on mount

  // Update clock every second
  useEffect(() => {
    const timer = setInterval(() => {
      setCurrentTime(new Date())
    }, 1000)
    return () => clearInterval(timer)
  }, [])

  // Load master matrix on mount (don't rebuild, just load existing) - only once
  // Wait for backend to be ready before loading
  useEffect(() => {
    // Only load once on mount if we haven't loaded yet
    if (hasLoadedRef.current) return
    
    // Wait for backend to be ready
    if (!backendReady) return
    
    // Backend is ready, load the matrix data
    if (!hasLoadedRef.current) {
      hasLoadedRef.current = true
      loadMasterMatrix(false)
    }
  }, [backendReady, loadMasterMatrix]) // Wait for backend to be ready before loading
  
  // Apply filters in worker when filters or active tab changes
  useEffect(() => {
    try {
      // Breakdown tabs (time, day, dom, date, month, year) don't use data table filtering
      const breakdownTabs = ['time', 'day', 'dom', 'date', 'month', 'year', 'timetable']
      if (breakdownTabs.includes(activeTab)) {
        return // Don't run filtering for breakdown tabs
      }
      
      if (workerReady && masterData.length > 0 && workerFilter) {
        const streamId = activeTab === 'timetable' ? 'master' : activeTab
        // Request initial rows for table rendering (first 100 rows, sorted)
        const returnRows = activeTab !== 'timetable' // Return rows for data table tabs
        workerFilter(streamFilters, streamId, returnRows, true) // sortIndices = true
        
        // Calculate stats in worker
        if (activeTab !== 'timetable' && workerCalculateStats) {
          workerCalculateStats(streamFilters, streamId, masterContractMultiplier, includeFilteredExecuted)
        }
      }
    } catch (error) {
      console.error('Error in filter useEffect:', error)
    }
  }, [streamFilters, activeTab, masterContractMultiplier, workerReady, masterData.length, workerFilter, workerCalculateStats, includeFilteredExecuted])
  
  // Save filters to localStorage whenever they change
  useEffect(() => {
    saveAllFilters(streamFilters)
  }, [streamFilters])
  
  // Save stats visibility to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_show_stats', JSON.stringify(showStats))
  }, [showStats])
  
  // Save includeFilteredExecuted to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_include_filtered_executed', JSON.stringify(includeFilteredExecuted))
  }, [includeFilteredExecuted])
  
  // Save contract multiplier to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_master_contract_multiplier', masterContractMultiplier.toString())
    // Sync input value when multiplier changes (e.g., from localStorage on mount)
    setMultiplierInput(masterContractMultiplier)
  }, [masterContractMultiplier])
  
  // Use imported utility functions - wrap to pass masterContractMultiplier
  const calculateTimeProfitLocal = useCallback((data = masterData) => 
    calculateTimeProfit(data, masterContractMultiplier), [masterData, masterContractMultiplier])
  
  const calculateDOMProfitLocal = (data = masterData) => 
    calculateDOMProfit(data, masterContractMultiplier)
  
  const calculateDailyProfitLocal = (data = masterData) => 
    calculateDailyProfit(data, masterContractMultiplier)
  
  const calculateDateProfitLocal = (data = masterData) => 
    calculateDateProfit(data, masterContractMultiplier)
  
  const calculateMonthlyProfitLocal = (data = masterData) => 
    calculateMonthlyProfit(data, masterContractMultiplier)
  
  const calculateYearlyProfitLocal = (data = masterData) => 
    calculateYearlyProfit(data, masterContractMultiplier)
  
  const calculateStats = (streamId) => {
    let filtered = getFilteredData(masterData, streamId)
    
    // Apply year filter if specified
    const filters = getStreamFilters(streamId) // Uses hook's getFiltersForStream internally
    if (filters.include_years && filters.include_years.length > 0) {
      filtered = filtered.filter(row => {
        if (!row.Date) return false
        try {
          const date = new Date(row.Date)
          if (!isNaN(date.getTime())) {
            return filters.include_years.includes(date.getFullYear())
          }
          // Try to extract year from DD/MM/YYYY format
          if (typeof row.Date === 'string') {
            const match = row.Date.match(/(\d{4})/)
            if (match) {
              return filters.include_years.includes(parseInt(match[1]))
            }
          }
          return false
        } catch {
          return false
        }
      })
    }
    
    if (filtered.length === 0) {
      return null
    }
    
    const totalTrades = filtered.length
    const wins = filtered.filter(t => t.Result === 'Win').length
    const losses = filtered.filter(t => t.Result === 'Loss').length
    const breakEven = filtered.filter(t => t.Result === 'BE').length
    const noTrade = filtered.filter(t => t.Result === 'NoTrade').length
    
    const winLossTrades = wins + losses
    const winRate = winLossTrades > 0 ? (wins / winLossTrades * 100) : 0
    
    const totalProfit = filtered.reduce((sum, t) => sum + (parseFloat(t.Profit) || 0), 0)
    const avgProfit = totalProfit / totalTrades
    
    const winningTrades = filtered.filter(t => t.Result === 'Win')
    const losingTrades = filtered.filter(t => t.Result === 'Loss')
    const avgWin = winningTrades.length > 0 
      ? winningTrades.reduce((sum, t) => sum + (parseFloat(t.Profit) || 0), 0) / winningTrades.length 
      : 0
    const avgLoss = losingTrades.length > 0 
      ? Math.abs(losingTrades.reduce((sum, t) => sum + (parseFloat(t.Profit) || 0), 0) / losingTrades.length)
      : 0
    const rrRatio = avgLoss > 0 ? avgWin / avgLoss : (avgWin > 0 ? Infinity : 0)
    
    const allowedTrades = filtered.filter(t => t.final_allowed !== false).length
    const blockedTrades = totalTrades - allowedTrades
    
    // Total Days - count unique dates
    const uniqueDates = new Set(filtered.map(t => {
      // Check both Date and trade_date fields
      const date = t.Date || t.trade_date
      if (!date) return null
      try {
        if (typeof date === 'string' && date.includes('/')) {
          return date.split(' ')[0] // Get date part if includes time
        }
        const d = new Date(date)
        return isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
      } catch {
        return null
      }
    }).filter(d => d !== null))
    const totalDays = uniqueDates.size
    
    // Currency conversion - use contract values based on instrument
    const contractValues = {
      'ES': 50,
      'NQ': 10,
      'YM': 5,
      'CL': 1000,
      'NG': 10000,
      'GC': 100
    }
    
    // Helper function to get contract value for a trade (local to calculateStats)
    const getContractValueLocal = (trade) => {
      const symbol = (trade.Symbol || trade.Instrument || 'ES').toUpperCase()
      // Extract base symbol (ES1 -> ES, NQ2 -> NQ, etc.) or use as-is if no number
      const baseSymbol = symbol.replace(/\d+$/, '') || symbol
      return contractValues[baseSymbol] || 50 // Default to ES if unknown
    }
    
    // Use local version inside calculateStats
    const getContractValue = getContractValueLocal
    
    // Calculate total profit in dollars by summing each trade's profit * contract value
    // Apply contract multiplier for master stream
    const contractMultiplier = streamId === 'master' ? masterContractMultiplier : 1
    const totalProfitDollars = filtered.reduce((sum, t) => {
      const profit = parseFloat(t.Profit) || 0
      const contractValue = getContractValue(t)
      return sum + (profit * contractValue * contractMultiplier)
    }, 0)
    
    // Time Changes and Final Time - track time slot changes
    // Sort by date and time to ensure proper chronological order (important for drawdown calculation)
    const sortedByDate = [...filtered].sort((a, b) => {
      const dateA = new Date(a.Date || a.trade_date)
      const dateB = new Date(b.Date || b.trade_date)
      const dateDiff = dateA.getTime() - dateB.getTime()
      if (dateDiff !== 0) return dateDiff
      // If same date, sort by time
      const timeA = (a.Time || '').toString()
      const timeB = (b.Time || '').toString()
      return timeA.localeCompare(timeB)
    })
    
    let timeChanges = 0
    let lastTime = null
    let finalTime = null
    
    sortedByDate.forEach(trade => {
      const currentTime = trade.Time
      if (currentTime && currentTime !== 'NA' && currentTime !== '00:00') {
        if (lastTime !== null && currentTime !== lastTime) {
          timeChanges++
        }
        lastTime = currentTime
        finalTime = currentTime
      }
    })
    
    // Profit Factor = Gross Profit / Gross Loss (in dollars)
    const grossProfitDollars = winningTrades.reduce((sum, t) => {
      const profit = Math.max(0, parseFloat(t.Profit) || 0)
      return sum + (profit * getContractValue(t) * contractMultiplier)
    }, 0)
    const grossLossDollars = Math.abs(losingTrades.reduce((sum, t) => {
      const profit = Math.min(0, parseFloat(t.Profit) || 0)
      return sum + (profit * getContractValue(t) * contractMultiplier)
    }, 0))
    const profitFactor = grossLossDollars > 0 ? grossProfitDollars / grossLossDollars : (grossProfitDollars > 0 ? Infinity : 0)
    
    // Rolling Drawdown calculation (in dollars) - standard peak-to-trough calculation
    // Track peak equity and calculate drawdown as: current - peak (negative when below peak)
    let runningProfitDollars = 0
    let peakDollars = 0
    let maxDrawdownDollars = 0 // Will be the most negative value (largest decline)
    
    // Process each trade in chronological order
    sortedByDate.forEach(trade => {
      const profit = parseFloat(trade.Profit) || 0
      const contractValue = getContractValue(trade)
      const profitDollars = profit * contractValue * contractMultiplier
      
      // Update running profit (cumulative equity)
      runningProfitDollars += profitDollars
      
      // Update peak if we've reached a new high
      if (runningProfitDollars > peakDollars) {
        peakDollars = runningProfitDollars
      }
      
      // Calculate current drawdown: current equity - peak equity
      // This will be negative when below peak, zero or positive when at/above peak
      const currentDrawdown = runningProfitDollars - peakDollars
      
      // Track maximum drawdown (most negative value, which is the largest decline)
      if (currentDrawdown < maxDrawdownDollars) {
        maxDrawdownDollars = currentDrawdown
      }
    })
    
    // Convert to positive number for display (drawdown is stored as negative)
    const maxDrawdownDollarsPositive = Math.abs(maxDrawdownDollars)
    
    // Max drawdown in points (for display) - approximate conversion
    const maxDrawdown = maxDrawdownDollarsPositive > 0 ? maxDrawdownDollarsPositive / 50 : 0
    
    // Sharpe Ratio - Annualized: (Mean Daily Return / Daily Std Dev) * sqrt(252)
    // Risk-free rate assumed to be 0
    const tradingDaysPerYear = 252
    
    // For master stream, group trades by date and sum daily returns
    // For individual streams, each trade is typically one per day
    let dailyReturnsDollars = []
    
    if (streamId === 'master') {
      // Group trades by date and sum returns per day
      const tradesByDate = new Map()
      filtered.forEach(trade => {
        const dateValue = trade.Date || trade.trade_date
        if (!dateValue) return
        
        try {
          let dateKey
          if (typeof dateValue === 'string') {
            if (dateValue.includes('/')) {
              dateKey = dateValue.split(' ')[0]
            } else if (dateValue.includes('-')) {
              dateKey = dateValue.split(' ')[0].split('T')[0]
            } else {
              const d = new Date(dateValue)
              dateKey = isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
            }
          } else {
            const d = new Date(dateValue)
            dateKey = isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
          }
          
          if (dateKey) {
            if (!tradesByDate.has(dateKey)) {
              tradesByDate.set(dateKey, 0)
            }
            const profit = parseFloat(trade.Profit) || 0
            const contractValue = getContractValue(trade)
            tradesByDate.set(dateKey, tradesByDate.get(dateKey) + (profit * contractValue * contractMultiplier))
          }
        } catch {
          // Skip invalid dates
        }
      })
      
      // Convert to array of daily returns
      dailyReturnsDollars = Array.from(tradesByDate.values())
    } else {
      // Individual stream - one trade per day typically
      dailyReturnsDollars = filtered.map(t => {
        const profit = parseFloat(t.Profit) || 0
        return profit * getContractValue(t) * contractMultiplier
      })
    }
    
    // Calculate daily returns statistics
    const meanDailyReturnDollars = dailyReturnsDollars.length > 0 
      ? dailyReturnsDollars.reduce((sum, r) => sum + r, 0) / dailyReturnsDollars.length 
      : 0
    const varianceDollars = dailyReturnsDollars.length > 1 
      ? dailyReturnsDollars.reduce((sum, r) => sum + Math.pow(r - meanDailyReturnDollars, 2), 0) / (dailyReturnsDollars.length - 1)
      : 0
    const stdDevDollars = Math.sqrt(varianceDollars)
    
    // Annualized Sharpe Ratio
    const annualizedReturn = meanDailyReturnDollars * tradingDaysPerYear
    const annualizedVolatility = stdDevDollars * Math.sqrt(tradingDaysPerYear)
    const sharpeRatio = annualizedVolatility > 0 ? annualizedReturn / annualizedVolatility : 0
    
    // Sortino Ratio - Annualized: (Mean Daily Return / Downside Std Dev) * sqrt(252)
    // Only considers negative returns for downside deviation
    const downsideReturnsDollars = dailyReturnsDollars.filter(r => r < 0)
    const downsideVarianceDollars = downsideReturnsDollars.length > 1
      ? downsideReturnsDollars.reduce((sum, r) => sum + Math.pow(r, 2), 0) / (downsideReturnsDollars.length - 1)
      : 0
    const downsideDevDollars = Math.sqrt(downsideVarianceDollars)
    const annualizedDownsideVolatility = downsideDevDollars * Math.sqrt(tradingDaysPerYear)
    const sortinoRatio = annualizedDownsideVolatility > 0 ? annualizedReturn / annualizedDownsideVolatility : 0
    
    // Calmar Ratio - Annual Return / Max Drawdown (both in dollars)
    const annualReturnDollars = totalDays > 0 ? (totalProfitDollars / totalDays) * tradingDaysPerYear : 0
    const calmarRatio = maxDrawdownDollarsPositive > 0 ? annualReturnDollars / maxDrawdownDollarsPositive : 0
    
    // Best and worst trades
    const tradesWithProfit = filtered.filter(t => t.Result !== 'NoTrade')
    const bestTrade = tradesWithProfit.length > 0 
      ? tradesWithProfit.reduce((best, t) => {
          const profit = parseFloat(t.Profit) || 0
          return profit > (parseFloat(best.Profit) || 0) ? t : best
        }, tradesWithProfit[0])
      : null
    const worstTrade = tradesWithProfit.length > 0
      ? tradesWithProfit.reduce((worst, t) => {
          const profit = parseFloat(t.Profit) || 0
          return profit < (parseFloat(worst.Profit) || 0) ? t : worst
        }, tradesWithProfit[0])
      : null
    
    // Format currency
    const formatCurrency = (value) => {
      return new Intl.NumberFormat('en-US', {
        style: 'currency',
        currency: 'USD',
        minimumFractionDigits: 0,
        maximumFractionDigits: 0
      }).format(value)
    }
    
    // Average trades per day
    const avgTradesPerDay = totalDays > 0 ? totalTrades / totalDays : 0
    
    // ===== STATISTICS FOR ALL STREAMS =====
    // Calculate per-trade PnL in dollars (needed for both master and individual streams)
    // Exclude NoTrade entries from PnL calculations
    const perTradePnLDollars = sortedByDate
      .filter(trade => trade.Result !== 'NoTrade')
      .map(trade => {
        const profit = parseFloat(trade.Profit) || 0
        const contractValue = getContractValue(trade)
        return profit * contractValue * contractMultiplier
      })
    
    // Std Dev of PnL (for all streams)
    const meanPnL = perTradePnLDollars.length > 0
      ? perTradePnLDollars.reduce((sum, pnl) => sum + pnl, 0) / perTradePnLDollars.length
      : 0
    const variancePnL = perTradePnLDollars.length > 1
      ? perTradePnLDollars.reduce((sum, pnl) => sum + Math.pow(pnl - meanPnL, 2), 0) / (perTradePnLDollars.length - 1)
      : 0
    const stdDevPnL = Math.sqrt(variancePnL)
    
    // Max Consecutive Losses (for all streams) - count only trades with pnl < 0
    let maxConsecutiveLosses = 0
    let currentConsecutiveLosses = 0
    perTradePnLDollars.forEach(pnl => {
      if (pnl < 0) {
        currentConsecutiveLosses++
        maxConsecutiveLosses = Math.max(maxConsecutiveLosses, currentConsecutiveLosses)
      } else {
        currentConsecutiveLosses = 0
      }
    })
    
    // Profit per Trade (for all streams)
    const profitPerTrade = meanPnL
    
    // Rolling 30-Day Win Rate (for individual streams)
    let rolling30DayWinRate = null
    if (streamId !== 'master') {
      // Get last 30 days of trades
      const tradesByDateMap = new Map()
      sortedByDate.forEach(trade => {
        const dateValue = trade.Date || trade.trade_date
        if (!dateValue) return
        
        try {
          let dateKey
          if (typeof dateValue === 'string' && dateValue.includes('/')) {
            const parts = dateValue.split(' ')[0].split('/')
            if (parts.length === 3) {
              const dateObj = new Date(parseInt(parts[2]), parseInt(parts[1]) - 1, parseInt(parts[0]))
              if (!isNaN(dateObj.getTime())) {
                dateKey = dateObj.toISOString().split('T')[0]
              }
            }
          } else if (typeof dateValue === 'string' && dateValue.includes('-')) {
            dateKey = dateValue.split(' ')[0].split('T')[0]
          } else {
            const d = new Date(dateValue)
            dateKey = isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
          }
          
          if (dateKey) {
            if (!tradesByDateMap.has(dateKey)) {
              tradesByDateMap.set(dateKey, [])
            }
            tradesByDateMap.get(dateKey).push(trade)
          }
        } catch {
          // Skip invalid dates
        }
      })
      
      // Get unique dates sorted
      const uniqueDates = Array.from(tradesByDateMap.keys()).sort()
      
      if (uniqueDates.length > 0) {
        // Get the most recent date
        const mostRecentDate = new Date(uniqueDates[uniqueDates.length - 1])
        const thirtyDaysAgo = new Date(mostRecentDate)
        thirtyDaysAgo.setDate(thirtyDaysAgo.getDate() - 30)
        
        // Filter trades from last 30 days
        const recent30DayTrades = []
        uniqueDates.forEach(dateKey => {
          const dateObj = new Date(dateKey)
          if (dateObj >= thirtyDaysAgo) {
            recent30DayTrades.push(...tradesByDateMap.get(dateKey))
          }
        })
        
        if (recent30DayTrades.length > 0) {
          const recentWins = recent30DayTrades.filter(t => t.Result === 'Win').length
          const recentLosses = recent30DayTrades.filter(t => t.Result === 'Loss').length
          const recentWinLossTrades = recentWins + recentLosses
          rolling30DayWinRate = recentWinLossTrades > 0 ? (recentWins / recentWinLossTrades * 100) : 0
        }
      }
    }
    
    // ===== ADDITIONAL STATISTICS (Master Stream Only) =====
    // 1. Mean PnL per trade (in dollars) - same as profitPerTrade but named differently for master
    const meanPnLPerTrade = profitPerTrade
    
    // 2. Median PnL per trade (in dollars)
    // Sort the per-trade PnL array for median calculation
    const validPnL = perTradePnLDollars.filter(pnl => !isNaN(pnl) && isFinite(pnl))
    const sortedPnLForMedian = [...validPnL].sort((a, b) => a - b)
    let medianPnLPerTrade = 0
    if (sortedPnLForMedian.length > 0) {
      const midIndex = Math.floor(sortedPnLForMedian.length / 2)
      if (sortedPnLForMedian.length % 2 === 0) {
        // Even number of trades - average the two middle values
        const mid1 = sortedPnLForMedian[midIndex - 1]
        const mid2 = sortedPnLForMedian[midIndex]
        medianPnLPerTrade = (mid1 + mid2) / 2
      } else {
        // Odd number of trades - take the middle value
        medianPnLPerTrade = sortedPnLForMedian[midIndex]
      }
    }
    
    // 3. 95% VaR (per trade) - 5th percentile of per-trade PnL
    // Use the same sorted array for consistency
    const sortedPnL = sortedPnLForMedian
    const var95Index = Math.floor(sortedPnL.length * 0.05)
    const var95 = sortedPnL.length > 0 && var95Index < sortedPnL.length
      ? sortedPnL[var95Index]
      : 0
    
    // 4. Expected Shortfall (CVaR 95%) - mean of all trades worse than 5th percentile
    const cvar95Trades = sortedPnL.slice(0, var95Index + 1)
    const cvar95 = cvar95Trades.length > 0
      ? cvar95Trades.reduce((sum, pnl) => sum + pnl, 0) / cvar95Trades.length
      : 0
    
    // 7. Time-to-Recovery (Longest Drawdown Duration in days)
    // Track equity curve and find peak → trough → recovery cycles
    // Recovery = when equity reaches or exceeds the previous peak
    let timeToRecoveryDays = 0
    let drawdownStartPeak = 0
    let drawdownStartDate = null
    let inDrawdown = false
    
    // Map dates for each trade
    const tradeDates = sortedByDate.map(trade => {
      const dateValue = trade.Date || trade.trade_date
      if (!dateValue) return null
      try {
        if (typeof dateValue === 'string' && dateValue.includes('/')) {
          const parts = dateValue.split(' ')[0].split('/')
          if (parts.length === 3) {
            return new Date(parseInt(parts[2]), parseInt(parts[1]) - 1, parseInt(parts[0]))
          }
          return null
        } else if (typeof dateValue === 'string' && dateValue.includes('-')) {
          return new Date(dateValue.split(' ')[0].split('T')[0])
        } else {
          const d = new Date(dateValue)
          return isNaN(d.getTime()) ? null : d
        }
      } catch {
        return null
      }
    })
    
    let runningEquity = 0
    sortedByDate.forEach((trade, idx) => {
      const profit = parseFloat(trade.Profit) || 0
      const contractValue = getContractValue(trade)
      const profitDollars = profit * contractValue * contractMultiplier
      runningEquity += profitDollars
      
      const tradeDate = tradeDates[idx]
      if (!tradeDate || isNaN(tradeDate.getTime())) return
      
      if (runningEquity > drawdownStartPeak) {
        // New peak reached - check if we were in drawdown
        if (inDrawdown && drawdownStartDate) {
          // Calculate recovery time from peak to recovery (new peak)
          const daysDiff = Math.floor((tradeDate.getTime() - drawdownStartDate.getTime()) / (1000 * 60 * 60 * 24))
          timeToRecoveryDays = Math.max(timeToRecoveryDays, daysDiff)
          inDrawdown = false
        }
        // Update peak
        drawdownStartPeak = runningEquity
        drawdownStartDate = tradeDate
      } else if (runningEquity < drawdownStartPeak) {
        // In drawdown
        if (!inDrawdown) {
          // Just entered drawdown - record the peak date
          inDrawdown = true
        }
      }
    })
    
    // 8. Monthly Return Std Dev - group by calendar month, sum PnL per month, then std dev
    const monthlyReturns = new Map()
    sortedByDate.forEach((trade) => {
      const dateValue = trade.Date || trade.trade_date
      if (!dateValue) return
      
      try {
        let dateObj
        if (typeof dateValue === 'string' && dateValue.includes('/')) {
          const parts = dateValue.split(' ')[0].split('/')
          if (parts.length === 3) {
            // Try DD/MM/YYYY first
            dateObj = new Date(parseInt(parts[2]), parseInt(parts[1]) - 1, parseInt(parts[0]))
            if (isNaN(dateObj.getTime())) {
              // Try MM/DD/YYYY
              dateObj = new Date(parseInt(parts[2]), parseInt(parts[0]) - 1, parseInt(parts[1]))
            }
          } else {
            return
          }
        } else if (typeof dateValue === 'string' && dateValue.includes('-')) {
          dateObj = new Date(dateValue.split(' ')[0].split('T')[0])
        } else {
          dateObj = new Date(dateValue)
        }
        
        if (isNaN(dateObj.getTime())) return
        
        const monthKey = `${dateObj.getFullYear()}-${String(dateObj.getMonth() + 1).padStart(2, '0')}`
        const profit = parseFloat(trade.Profit) || 0
        const contractValue = getContractValue(trade)
        const profitDollars = profit * contractValue * contractMultiplier
        
        if (!monthlyReturns.has(monthKey)) {
          monthlyReturns.set(monthKey, 0)
        }
        monthlyReturns.set(monthKey, monthlyReturns.get(monthKey) + profitDollars)
      } catch {
        // Skip invalid dates
      }
    })
    
    const monthlyReturnsArray = Array.from(monthlyReturns.values())
    const meanMonthlyReturn = monthlyReturnsArray.length > 0
      ? monthlyReturnsArray.reduce((sum, ret) => sum + ret, 0) / monthlyReturnsArray.length
      : 0
    const monthlyReturnVariance = monthlyReturnsArray.length > 1
      ? monthlyReturnsArray.reduce((sum, ret) => sum + Math.pow(ret - meanMonthlyReturn, 2), 0) / (monthlyReturnsArray.length - 1)
      : 0
    const monthlyReturnStdDev = Math.sqrt(monthlyReturnVariance)
    
    // 5. Profit per Day - average daily profit (not total/totalDays) (master only)
    const dailyProfits = new Map()
    sortedByDate.forEach((trade, idx) => {
      const dateValue = trade.Date || trade.trade_date
      if (!dateValue) return
      
      try {
        let dateKey
        if (typeof dateValue === 'string' && dateValue.includes('/')) {
          dateKey = dateValue.split(' ')[0]
        } else if (typeof dateValue === 'string' && dateValue.includes('-')) {
          dateKey = dateValue.split(' ')[0].split('T')[0]
        } else {
          const d = new Date(dateValue)
          dateKey = isNaN(d.getTime()) ? null : d.toISOString().split('T')[0]
        }
        
        if (dateKey) {
          const profit = parseFloat(trade.Profit) || 0
          const contractValue = getContractValue(trade)
          const profitDollars = profit * contractValue * contractMultiplier
          
          if (!dailyProfits.has(dateKey)) {
            dailyProfits.set(dateKey, 0)
          }
          dailyProfits.set(dateKey, dailyProfits.get(dateKey) + profitDollars)
        }
      } catch {
        // Skip invalid dates
      }
    })
    
    const dailyProfitsArray = Array.from(dailyProfits.values())
    const profitPerDay = dailyProfitsArray.length > 0
      ? dailyProfitsArray.reduce((sum, profit) => sum + profit, 0) / dailyProfitsArray.length
      : 0
    
    // 11. Skewness (on per-trade PnL)
    const n = perTradePnLDollars.length
    let skewness = 0
    if (n > 2 && stdDevPnL > 0) {
      const skewnessSum = perTradePnLDollars.reduce((sum, pnl) => {
        return sum + Math.pow((pnl - meanPnL) / stdDevPnL, 3)
      }, 0)
      skewness = (n / ((n - 1) * (n - 2))) * skewnessSum
    }
    
    // 12. Kurtosis (on per-trade PnL)
    let kurtosis = 0
    if (n > 3 && stdDevPnL > 0) {
      const kurtosisSum = perTradePnLDollars.reduce((sum, pnl) => {
        return sum + Math.pow((pnl - meanPnL) / stdDevPnL, 4)
      }, 0)
      kurtosis = ((n * (n + 1)) / ((n - 1) * (n - 2) * (n - 3))) * kurtosisSum - (3 * (n - 1) * (n - 1)) / ((n - 2) * (n - 3))
    }
    
    return {
      totalTrades,
      totalDays,
      avgTradesPerDay: avgTradesPerDay.toFixed(2),
      wins,
      losses,
      breakEven,
      noTrade,
      winRate: winRate.toFixed(1),
      totalProfit: totalProfit.toFixed(2),
      totalProfitDollars: formatCurrency(totalProfitDollars),
      avgProfit: avgProfit.toFixed(2),
      avgWin: avgWin.toFixed(2),
      avgLoss: avgLoss.toFixed(2),
      rrRatio: rrRatio === Infinity ? '∞' : rrRatio.toFixed(2),
      profitFactor: profitFactor === Infinity ? '∞' : profitFactor.toFixed(2),
      timeChanges,
      finalTime: finalTime || 'N/A',
      sharpeRatio: sharpeRatio.toFixed(2),
      sortinoRatio: sortinoRatio.toFixed(2),
      calmarRatio: calmarRatio.toFixed(2),
      maxDrawdown: maxDrawdown.toFixed(2),
      maxDrawdownDollars: formatCurrency(maxDrawdownDollarsPositive),
      allowedTrades,
      blockedTrades,
      bestTrade,
      worstTrade,
      // Statistics for all streams
      stdDevPnL: formatCurrency(stdDevPnL),
      maxConsecutiveLosses: maxConsecutiveLosses,
      profitPerTrade: formatCurrency(profitPerTrade),
      rolling30DayWinRate: rolling30DayWinRate !== null ? rolling30DayWinRate.toFixed(1) : null,
      // Additional statistics (master stream only)
      meanPnLPerTrade: streamId === 'master' ? formatCurrency(meanPnLPerTrade) : null,
      medianPnLPerTrade: streamId === 'master' ? formatCurrency(medianPnLPerTrade) : null,
      var95: streamId === 'master' ? formatCurrency(var95) : null,
      cvar95: streamId === 'master' ? formatCurrency(cvar95) : null,
      timeToRecoveryDays: streamId === 'master' ? timeToRecoveryDays : null,
      monthlyReturnStdDev: streamId === 'master' ? formatCurrency(monthlyReturnStdDev) : null,
      profitPerDay: streamId === 'master' ? formatCurrency(profitPerDay) : null,
      skewness: streamId === 'master' ? skewness.toFixed(3) : null,
      kurtosis: streamId === 'master' ? kurtosis.toFixed(3) : null
    }
  }
  
  const toggleStats = (streamId) => {
    setShowStats(prev => ({
      ...prev,
      [streamId]: !prev[streamId]
    }))
  }
  
  // Update filter for a specific stream (with persistence)
  const updateStreamFilter = (streamId, filterType, value) => {
    setStreamFilters(prev => {
      // Create a deep copy to avoid mutations
      const updated = { ...prev }
      
      // Initialize stream filters if they don't exist
      if (!updated[streamId]) {
        updated[streamId] = getDefaultFilters()
      }
      
      // Create a new filter object for this stream (merge with defaults to ensure completeness)
      const currentFilters = {
        ...getDefaultFilters(),
        ...updated[streamId]
      }
      
      if (filterType === 'exclude_days_of_week') {
        const current = currentFilters.exclude_days_of_week || []
        if (current.includes(value)) {
          // Remove filter
          currentFilters.exclude_days_of_week = current.filter(d => d !== value)
        } else {
          // Add filter
          currentFilters.exclude_days_of_week = [...current, value]
        }
      } else if (filterType === 'exclude_days_of_month') {
        const current = currentFilters.exclude_days_of_month || []
        const numValue = typeof value === 'number' ? value : parseInt(value)
        if (current.includes(numValue)) {
          // Remove filter
          currentFilters.exclude_days_of_month = current.filter(d => d !== numValue)
        } else {
          // Add filter
          currentFilters.exclude_days_of_month = [...current, numValue]
        }
      } else if (filterType === 'exclude_times') {
        const current = currentFilters.exclude_times || []
        if (current.includes(value)) {
          // Remove filter
          currentFilters.exclude_times = current.filter(t => t !== value)
          // Debug logging disabled
        } else {
          // Add filter
          currentFilters.exclude_times = [...current, value]
          // Debug logging disabled
        }
      } else if (filterType === 'include_years') {
        const current = currentFilters.include_years || []
        const numValue = typeof value === 'number' ? value : parseInt(value)
        if (current.includes(numValue)) {
          // Remove filter
          currentFilters.include_years = current.filter(y => y !== numValue)
        } else {
          // Add filter
          currentFilters.include_years = [...current, numValue]
        }
      }
      
      // Return a completely new object to trigger re-render
      const newState = {
        ...updated,
        [streamId]: currentFilters
      }
      // Debug logging disabled for performance
      return newState
    })
  }
  
  // Optimized date parsing cache
  const parseDateCached = (() => {
    const cache = new Map()
    return (dateValue) => {
      if (!dateValue) return null
      if (cache.has(dateValue)) return cache.get(dateValue)
      const parsed = parseDateValue(dateValue)
      if (parsed) cache.set(dateValue, parsed)
      return parsed
    }
  })()

  const getFilteredData = (data, streamId = null) => {
    // Early return if no data
    if (!data || data.length === 0) return []
    
    // Single-pass filtering for better performance
    const filtered = []
    
    // Pre-compute filter sets for faster lookups (Set.has() is O(1) vs Array.includes() O(n))
    const getFilterSets = (filters) => {
      if (!filters) return null
      return {
        excludeDaysOfWeek: filters.exclude_days_of_week?.length > 0 
          ? new Set(filters.exclude_days_of_week) 
          : null,
        excludeDaysOfMonth: filters.exclude_days_of_month?.length > 0 
          ? new Set(filters.exclude_days_of_month) 
          : null,
        includeYears: filters.include_years?.length > 0 
          ? new Set(filters.include_years) 
          : null
      }
    }
    
    // Get master filters if they exist
    const masterFilterSets = getFilterSets(streamFilters['master'])
    
    // Single pass through data with all filters applied
    for (const row of data) {
      // Filter by stream first (fastest check)
      if (streamId && streamId !== 'master' && row.Stream !== streamId) {
        continue
      }
      
      // Get filters for this row's stream
      const rowStream = row.Stream
      const rowFilters = streamId && streamId !== 'master' 
        ? streamFilters[streamId] 
        : streamFilters[rowStream]
      
      const filterSets = getFilterSets(rowFilters)
      
      // Parse date once and reuse for all date-based filters
      const dateValue = row.Date || row.trade_date
      let parsedDate = null
      let dayOfWeek = null
      let dayOfMonth = null
      let year = null
      
      if (dateValue) {
        parsedDate = parseDateCached(dateValue)
        if (parsedDate) {
          dayOfWeek = parsedDate.toLocaleDateString('en-US', { weekday: 'long' })
          dayOfMonth = parsedDate.getDate()
          year = parsedDate.getFullYear()
        }
      }
      
      // Apply individual stream filters (if not master tab)
      if (streamId && streamId !== 'master' && filterSets) {
        // Day of week filter
        if (filterSets.excludeDaysOfWeek && dayOfWeek && filterSets.excludeDaysOfWeek.has(dayOfWeek)) {
          continue
        }
        
        // Day of month filter
        if (filterSets.excludeDaysOfMonth && dayOfMonth !== null && filterSets.excludeDaysOfMonth.has(dayOfMonth)) {
          continue
        }
        
        // Year filter
        if (filterSets.includeYears) {
          if (year === null || !filterSets.includeYears.has(year)) {
            continue
          }
        }
      }
      
      // Apply master tab filters (each stream's filters to its own rows)
      if (streamId === 'master' || !streamId) {
        const streamFilterSets = rowStream ? getFilterSets(streamFilters[rowStream]) : null
        
        if (streamFilterSets) {
          // Day of week filter
          if (streamFilterSets.excludeDaysOfWeek && dayOfWeek && streamFilterSets.excludeDaysOfWeek.has(dayOfWeek)) {
            continue
          }
          
          // Day of month filter
          if (streamFilterSets.excludeDaysOfMonth && dayOfMonth !== null && streamFilterSets.excludeDaysOfMonth.has(dayOfMonth)) {
            continue
          }
          
          // Year filter
          if (streamFilterSets.includeYears) {
            if (year === null || !streamFilterSets.includeYears.has(year)) {
              continue
            }
          }
        }
        
        // Apply master-specific filters on top of stream filters
        if (masterFilterSets) {
          // Day of week filter
          if (masterFilterSets.excludeDaysOfWeek && dayOfWeek && masterFilterSets.excludeDaysOfWeek.has(dayOfWeek)) {
            continue
          }
          
          // Day of month filter
          if (masterFilterSets.excludeDaysOfMonth && dayOfMonth !== null && masterFilterSets.excludeDaysOfMonth.has(dayOfMonth)) {
            continue
          }
          
          // Year filter
          if (masterFilterSets.includeYears) {
            if (year === null || !masterFilterSets.includeYears.has(year)) {
              continue
            }
          }
        }
      }
      
      // Row passed all filters
      filtered.push(row)
    }
    
    // Sort: Date (newest first), then Time (earliest first)
    filtered.sort((a, b) => {
      // Parse dates properly - handle ISO strings, Date objects, or DD/MM/YYYY strings
      const parseDate = (dateValue) => {
        if (!dateValue) return new Date(0)
        if (dateValue instanceof Date) return dateValue
        if (typeof dateValue === 'string') {
          // Try ISO format first (YYYY-MM-DD)
          if (dateValue.match(/^\d{4}-\d{2}-\d{2}/)) {
            return new Date(dateValue)
          }
          // Try DD/MM/YYYY format
          const ddmmyyyy = dateValue.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})/)
          if (ddmmyyyy) {
            const [, day, month, year] = ddmmyyyy
            return new Date(parseInt(year), parseInt(month) - 1, parseInt(day))
          }
          // Try MM/DD/YYYY format
          const mmddyyyy = dateValue.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})/)
          if (mmddyyyy) {
            const [, month, day, year] = mmddyyyy
            return new Date(parseInt(year), parseInt(month) - 1, parseInt(day))
          }
          // Fallback to standard Date parsing
          return new Date(dateValue)
        }
        return new Date(dateValue)
      }
      
      const dateA = parseDate(a.Date)
      const dateB = parseDate(b.Date)
      
      // First by date (newest first)
      const timeDiff = dateB.getTime() - dateA.getTime()
      if (timeDiff !== 0) {
        return timeDiff
      }
      
      // Then by time (latest first) - parse time as HH:MM for proper numeric comparison
      const parseTime = (timeStr) => {
        if (!timeStr || timeStr === '00:00' || timeStr === 'NA') return 0
        const parts = timeStr.split(':')
        if (parts.length === 2) {
          return parseInt(parts[0]) * 60 + parseInt(parts[1]) // Convert to minutes
        }
        return 0
      }
      
      const timeA = parseTime(a.Time)
      const timeB = parseTime(b.Time)
      if (timeA !== timeB) {
        return timeB - timeA // Latest time first (descending)
      }
      
      // Then by symbol/stream
      if (a.Symbol !== b.Symbol) {
        return (a.Symbol || '').localeCompare(b.Symbol || '')
      }
      
      return (a.Stream || '').localeCompare(b.Stream || '')
    })
    
    return filtered
  }
  
  // Get filters for a stream (with defaults) - used by filtering logic
  // Note: For 'master', this returns default filters (master filters are applied separately in getFilteredData)
  const getStreamFilters = (streamId) => {
    if (streamId === 'master' || !streamId) {
      return getDefaultFilters()
    }
    return getStreamFiltersFromStorage(streamFilters, streamId)
  }
  
  // Get available years - use API response first, fallback to extracting from data
  const getAvailableYears = () => {
    // Use years from API response if available (more reliable - includes all years from source files)
    if (availableYearsFromAPI && availableYearsFromAPI.length > 0) {
      return [...availableYearsFromAPI].sort((a, b) => b - a) // Newest first
    }
    
    // Fallback: extract from master data if API years not available
    if (!masterData || masterData.length === 0) return []
    const years = new Set()
    masterData.forEach(row => {
      if (row.Date) {
        try {
          const date = new Date(row.Date)
          if (!isNaN(date.getTime())) {
            years.add(date.getFullYear())
          } else if (typeof row.Date === 'string') {
            // Try to extract year from DD/MM/YYYY format
            const match = row.Date.match(/(\d{4})/)
            if (match) {
              years.add(parseInt(match[1]))
            }
          }
        } catch {
          // Ignore invalid dates
        }
      }
    })
    return Array.from(years).sort((a, b) => b - a) // Newest first
  }
  
  // Format worker stats for display (defined early so renderStats can use it)
  const formatWorkerStats = useCallback((rawStats, streamId) => {
    if (!rawStats) return null
    
    // Handle new structure (with sample_counts, performance_trade_metrics, performance_daily_metrics)
    const formatCurrency = (value) => {
      return new Intl.NumberFormat('en-US', {
        style: 'currency',
        currency: 'USD',
        minimumFractionDigits: 0,
        maximumFractionDigits: 0
      }).format(value)
    }
    
    // Check if new structure (has sample_counts)
    if (rawStats.sample_counts && rawStats.performance_trade_metrics && rawStats.performance_daily_metrics) {
      const sc = rawStats.sample_counts
      const ptm = rawStats.performance_trade_metrics
      const pdm = rawStats.performance_daily_metrics
      
      // Calculate avg trades per day
      // Use new structure with day_counts and behavioral metrics
      const dayCounts = rawStats.day_counts || { executed_trading_days: 0, allowed_trading_days: 0 }
      const executedTradingDays = dayCounts.executed_trading_days || 0
      const allowedTradingDays = dayCounts.allowed_trading_days || 0
      
      return {
        // Sample counts
        totalRows: sc.total_rows,
        filteredRows: sc.filtered_rows,
        allowedRows: sc.allowed_rows,
        executedTradesTotal: sc.executed_trades_total,
        executedTradesAllowed: sc.executed_trades_allowed,
        executedTradesFiltered: sc.executed_trades_filtered,
        notradeTotal: sc.notrade_total,
        totalTrades: sc.executed_trades_total, // For backwards compatibility
        allowedTrades: sc.executed_trades_allowed, // For backwards compatibility
        
        // Trade metrics
        wins: ptm.wins,
        losses: ptm.losses,
        breakEven: ptm.be,
        time: ptm.time,
        winRate: ptm.win_rate ? ptm.win_rate.toFixed(1) : '0.0',
        totalProfit: (ptm.total_profit || 0).toFixed(2),
        totalProfitDollars: formatCurrency(ptm.total_profit || 0),
        profitFactor: ptm.profit_factor ? (ptm.profit_factor === Infinity ? '∞' : ptm.profit_factor.toFixed(2)) : '0.00',
        meanPnLPerTrade: formatCurrency(ptm.mean_pnl_per_trade || 0),
        medianPnLPerTrade: streamId === 'master' ? formatCurrency(ptm.median_pnl_per_trade || 0) : null,
        stdDevPnL: formatCurrency(ptm.stddev_pnl_per_trade || 0),
        maxConsecutiveLosses: ptm.max_consecutive_losses || 0,
        maxDrawdown: (ptm.max_drawdown || 0).toFixed(2),
        maxDrawdownDollars: formatCurrency(ptm.max_drawdown || 0),
        var95: streamId === 'master' ? formatCurrency(ptm.var95 || 0) : null,
        cvar95: streamId === 'master' ? formatCurrency(ptm.cvar95 || 0) : null,
        riskReward: ptm.rr_ratio ? (ptm.rr_ratio === Infinity ? '∞' : ptm.rr_ratio.toFixed(2)) : '0.00',
        rrRatio: ptm.rr_ratio ? (ptm.rr_ratio === Infinity ? '∞' : ptm.rr_ratio.toFixed(2)) : '0.00',
        
        // Daily metrics
        // Day counts
        executedTradingDays: executedTradingDays,
        allowedTradingDays: allowedTradingDays,
        // Behavioral metrics (using allowed_trading_days)
        avgTradesPerDay: pdm.avg_trades_per_day ? pdm.avg_trades_per_day.toFixed(2) : '0.00',
        profitPerDay: streamId === 'master' ? formatCurrency(pdm.profit_per_day || 0) : null,
        profitPerWeek: streamId === 'master' ? formatCurrency(pdm.profit_per_week || 0) : null,
        profitPerMonth: streamId === 'master' ? formatCurrency(pdm.profit_per_month || 0) : null,
        profitPerYear: streamId === 'master' ? formatCurrency(pdm.profit_per_year || 0) : null,
        // Risk metrics (from daily PnL series)
        sharpeRatio: pdm.sharpe_ratio ? pdm.sharpe_ratio.toFixed(2) : '0.00',
        sortinoRatio: pdm.sortino_ratio ? pdm.sortino_ratio.toFixed(2) : '0.00',
        timeToRecoveryDays: streamId === 'master' ? pdm.time_to_recovery_days : null,
        monthlyReturnStdDev: streamId === 'master' ? formatCurrency(pdm.monthly_return_stddev || 0) : null,
        profitPerTrade: formatCurrency(ptm.mean_pnl_per_trade || 0),
        calmarRatio: pdm.calmar_ratio ? pdm.calmar_ratio.toFixed(2) : '0.00',
        
        // Not available in new structure (set to null)
        noTrade: sc.notrade_total, // For display
        skewness: null,
        kurtosis: null,
        rolling30DayWinRate: null
      }
    }
    
    // Fallback: old structure (for backwards compatibility during migration)
    return {
      totalTrades: rawStats.totalTrades || 0,
      totalProfit: (rawStats.totalProfit || 0).toFixed(2),
      totalProfitDollars: formatCurrency(rawStats.totalProfitDollars || 0),
      totalDays: rawStats.totalDays || 0,
      avgTradesPerDay: rawStats.avgTradesPerDay ? rawStats.avgTradesPerDay.toFixed(2) : '0.00',
      winRate: rawStats.winRate ? rawStats.winRate.toFixed(1) : '0.0',
      wins: rawStats.wins || 0,
      losses: rawStats.losses || 0,
      breakEven: rawStats.breakEven || 0,
      noTrade: rawStats.noTrade || 0,
      profitFactor: rawStats.profitFactor ? (rawStats.profitFactor === Infinity ? '∞' : rawStats.profitFactor.toFixed(2)) : '0.00',
      sharpeRatio: rawStats.sharpeRatio ? rawStats.sharpeRatio.toFixed(2) : '0.00',
      sortinoRatio: rawStats.sortinoRatio ? rawStats.sortinoRatio.toFixed(2) : '0.00',
      calmarRatio: rawStats.calmarRatio ? rawStats.calmarRatio.toFixed(2) : '0.00',
      maxDrawdown: rawStats.maxDrawdown ? rawStats.maxDrawdown.toFixed(2) : '0.00',
      maxDrawdownDollars: formatCurrency(rawStats.maxDrawdownDollars || 0),
      riskReward: rawStats.riskReward ? (rawStats.riskReward === Infinity ? '∞' : rawStats.riskReward.toFixed(2)) : '0.00',
      rrRatio: rawStats.riskReward ? (rawStats.riskReward === Infinity ? '∞' : rawStats.riskReward.toFixed(2)) : '0.00',
      meanPnLPerTrade: streamId === 'master' ? formatCurrency(rawStats.meanPnLPerTrade || 0) : null,
      medianPnLPerTrade: streamId === 'master' ? formatCurrency(rawStats.medianPnLPerTrade || 0) : null,
      stdDevPnL: formatCurrency(rawStats.stdDevPnL || 0),
      maxConsecutiveLosses: rawStats.maxConsecutiveLosses || 0,
      var95: streamId === 'master' ? formatCurrency(rawStats.var95 || 0) : null,
      cvar95: streamId === 'master' ? formatCurrency(rawStats.cvar95 || 0) : null,
      timeToRecoveryDays: streamId === 'master' ? (rawStats.timeToRecovery || 0) : null,
      monthlyReturnStdDev: streamId === 'master' ? formatCurrency(rawStats.monthlyReturnStdDev || 0) : null,
      profitPerDay: streamId === 'master' ? formatCurrency(rawStats.profitPerDay || 0) : null,
      profitPerWeek: streamId === 'master' ? formatCurrency(rawStats.profitPerWeek || 0) : null,
      profitPerMonth: streamId === 'master' ? formatCurrency(rawStats.profitPerMonth || 0) : null,
      profitPerYear: streamId === 'master' ? formatCurrency(rawStats.profitPerYear || 0) : null,
      profitPerTrade: formatCurrency(rawStats.meanPnLPerTrade || 0),
      skewness: streamId === 'master' ? (rawStats.skewness || 0).toFixed(3) : null,
      kurtosis: streamId === 'master' ? (rawStats.kurtosis || 0).toFixed(3) : null,
      rolling30DayWinRate: null
    }
  }, [])
  
  const renderStats = (streamId, precomputedStats = null) => {
    // Always use worker stats when available, only fallback if worker not ready
    let stats = null
    try {
      if (precomputedStats) {
        stats = precomputedStats
      } else if (workerReady && workerStats && formatWorkerStats) {
        // Use worker stats (much faster - computed off main thread)
        stats = formatWorkerStats(workerStats, streamId)
      } else if (!workerReady) {
        // Only fallback to main thread if worker not ready yet
        stats = calculateStats(streamId)
      } else {
        // Worker ready but no stats yet - show loading or empty
        return (
          <div className="bg-gray-900 rounded-lg p-4 mb-4">
            <p className="text-gray-400 text-sm">Calculating statistics...</p>
          </div>
        )
      }
    } catch (error) {
      console.error('Error in renderStats:', error)
      // Only fallback if worker not ready
      if (!workerReady) {
        stats = calculateStats(streamId)
      } else {
        return (
          <div className="bg-gray-900 rounded-lg p-4 mb-4">
            <p className="text-red-400 text-sm">Error calculating statistics</p>
          </div>
        )
      }
    }
    // Debug logging disabled for performance
    if (!stats) {
      const filtered = getFilteredData(masterData, streamId)
      return (
        <div className="bg-gray-900 rounded-lg p-4 mb-4">
          <p className="text-gray-400 text-sm">No data available for statistics</p>
          {filtered.length === 0 && (
            <p className="text-gray-500 text-xs mt-2">No filtered data found for {streamId}</p>
          )}
        </div>
      )
    }
    
    // Render different stats for master vs individual streams
    if (streamId === 'master') {
      // Master stream - show all 4 sections
      return (
        <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
          {/* Toggle for including filtered executed trades */}
          <div className="mb-4 pb-3 border-b border-gray-700 flex items-center justify-between">
            <div>
              <h4 className="text-sm font-semibold text-gray-300 mb-1">Statistics Settings</h4>
              <p className="text-xs text-gray-500">Performance stats are computed on executed trades only (Win, Loss, BE, TIME)</p>
            </div>
            <div className="flex items-center">
              <span className="text-sm text-gray-400 mr-3">Include filtered executed trades</span>
              <div 
                className="relative cursor-pointer"
                onClick={() => setIncludeFilteredExecuted(!includeFilteredExecuted)}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault()
                    setIncludeFilteredExecuted(!includeFilteredExecuted)
                  }
                }}
              >
                <input
                  type="checkbox"
                  className="sr-only"
                  checked={includeFilteredExecuted}
                  onChange={(e) => setIncludeFilteredExecuted(e.target.checked)}
                  readOnly
                />
                <div className={`block w-14 h-8 rounded-full ${includeFilteredExecuted ? 'bg-green-500' : 'bg-gray-600'}`}></div>
                <div className={`absolute left-1 top-1 bg-white w-6 h-6 rounded-full transition transform ${includeFilteredExecuted ? 'translate-x-6' : ''}`}></div>
              </div>
            </div>
          </div>
          
          {/* Section 1: Core Performance */}
          <div className="mb-4">
            <h4 className="text-sm font-semibold mb-3 text-gray-300">Core Performance</h4>
            <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-4">
              <div>
                <div className="text-xs text-gray-400 mb-1">Total Profit ($)</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.totalProfit) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.totalProfitDollars}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Executed Trades</div>
                <div className="text-lg font-semibold">{stats.executedTradesTotal !== undefined ? stats.executedTradesTotal : (stats.allowedTrades || stats.totalTrades)}</div>
                {stats.executedTradesAllowed !== undefined && stats.executedTradesFiltered !== undefined && (
                  <div className="text-xs text-gray-500 mt-1">
                    Allowed: {stats.executedTradesAllowed} | Filtered: {stats.executedTradesFiltered}
                  </div>
                )}
                {stats.totalRows !== undefined && stats.filteredRows !== undefined && (
                  <div className="text-xs text-gray-400 mt-1">
                    Total Rows: {stats.totalRows} | Filtered Rows: {stats.filteredRows}
                  </div>
                )}
                {stats.notradeTotal !== undefined && (
                  <div className="text-xs text-gray-500 mt-1">
                    NoTrade: {stats.notradeTotal}
                  </div>
                )}
              </div>
              {stats.executedTradingDays !== undefined && stats.allowedTradingDays !== undefined && (
                <div>
                  <div className="text-xs text-gray-400 mb-1">Trading Days</div>
                  <div className="text-lg font-semibold">
                    Executed: {stats.executedTradingDays}
                    <span className="text-xs text-gray-500 ml-2">Allowed: {stats.allowedTradingDays}</span>
                  </div>
                </div>
              )}
              <div>
                <div className="text-xs text-gray-400 mb-1">Avg Trades per Active Day</div>
                <div className="text-lg font-semibold">{stats.avgTradesPerDay}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit per Active Day</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.profitPerDay?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.profitPerDay || 'N/A'}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit per Week</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.profitPerWeek?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.profitPerWeek || 'N/A'}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit per Month</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.profitPerMonth?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.profitPerMonth || 'N/A'}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit per Year</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.profitPerYear?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.profitPerYear || 'N/A'}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit per Trade (Mean PnL)</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.profitPerTrade?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.profitPerTrade || 'N/A'}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Win Rate</div>
                <div className="text-lg font-semibold text-green-400">{stats.winRate}%</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Wins</div>
                <div className="text-lg font-semibold text-green-400">{stats.wins}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Losses</div>
                <div className="text-lg font-semibold text-red-400">{stats.losses}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Break-Even</div>
                <div className="text-lg font-semibold">{stats.breakEven}</div>
              </div>
              {stats.time !== undefined && (
                <div>
                  <div className="text-xs text-gray-400 mb-1">TIME</div>
                  <div className="text-lg font-semibold">{stats.time}</div>
                </div>
              )}
            </div>
          </div>
          
          {/* Section 2: Risk-Adjusted Performance */}
          <div className="mb-4 pt-4 border-t border-gray-700">
            <h4 className="text-sm font-semibold mb-3 text-gray-300">Risk-Adjusted Performance</h4>
            <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-4">
              <div>
                <div className="text-xs text-gray-400 mb-1">Sharpe Ratio</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.sharpeRatio) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.sharpeRatio}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Sortino Ratio</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.sortinoRatio) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.sortinoRatio}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Calmar Ratio</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.calmarRatio) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.calmarRatio}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit Factor</div>
                <div className="text-lg font-semibold">{stats.profitFactor}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Risk-Reward</div>
                <div className="text-lg font-semibold">{stats.rrRatio}</div>
              </div>
            </div>
          </div>
          
          {/* Section 3: Drawdowns & Stability */}
          <div className="mb-4 pt-4 border-t border-gray-700">
            <h4 className="text-sm font-semibold mb-3 text-gray-300">Drawdowns & Stability</h4>
            <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
              <div>
                <div className="text-xs text-gray-400 mb-1">Max Drawdown ($)</div>
                <div className="text-lg font-semibold text-red-400">{stats.maxDrawdownDollars}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Time-to-Recovery (Days)</div>
                <div className="text-lg font-semibold">{stats.timeToRecoveryDays ?? 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Max Consecutive Losses</div>
                <div className="text-lg font-semibold text-red-400">{stats.maxConsecutiveLosses ?? 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Monthly Return Std Dev</div>
                <div className="text-lg font-semibold">{stats.monthlyReturnStdDev || 'N/A'}</div>
              </div>
            </div>
          </div>
          
          {/* Section 4: PnL Distribution & Tail Risk */}
          <div className="pt-4 border-t border-gray-700">
            <h4 className="text-sm font-semibold mb-3 text-gray-300">PnL Distribution & Tail Risk</h4>
            <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
              <div>
                <div className="text-xs text-gray-400 mb-1">Median PnL per Trade</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.medianPnLPerTrade?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.medianPnLPerTrade || 'N/A'}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Std Dev of PnL</div>
                <div className="text-lg font-semibold">{stats.stdDevPnL || 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">95% VaR (per trade)</div>
                <div className="text-lg font-semibold text-red-400">{stats.var95 || 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Expected Shortfall (CVaR 95%)</div>
                <div className="text-lg font-semibold text-red-400">{stats.cvar95 || 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Skewness</div>
                <div className="text-lg font-semibold">{stats.skewness ?? 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Kurtosis</div>
                <div className="text-lg font-semibold">{stats.kurtosis ?? 'N/A'}</div>
              </div>
            </div>
          </div>
        </div>
      )
    } else {
      // Individual streams - show 3 sections with 8 stats total
      return (
        <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
          {/* Section 1: Core Performance */}
          <div className="mb-4">
            <h4 className="text-sm font-semibold mb-3 text-gray-300">Core Performance</h4>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
              <div>
                <div className="text-xs text-gray-400 mb-1">Trades</div>
                <div className="text-lg font-semibold">{stats.totalTrades}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit ($)</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.totalProfit) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.totalProfitDollars}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Win Rate</div>
                <div className="text-lg font-semibold text-green-400">{stats.winRate}%</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit per Trade</div>
                <div className={`text-lg font-semibold ${parseFloat(stats.profitPerTrade?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {stats.profitPerTrade || 'N/A'}
                </div>
              </div>
            </div>
          </div>
          
          {/* Section 2: Risk & Volatility */}
          <div className="mb-4 pt-4 border-t border-gray-700">
            <h4 className="text-sm font-semibold mb-3 text-gray-300">Risk & Volatility</h4>
            <div className="grid grid-cols-2 md:grid-cols-2 gap-4">
              <div>
                <div className="text-xs text-gray-400 mb-1">Std Dev of PnL</div>
                <div className="text-lg font-semibold">{stats.stdDevPnL || 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Max Consecutive Losses</div>
                <div className="text-lg font-semibold text-red-400">{stats.maxConsecutiveLosses ?? 'N/A'}</div>
              </div>
            </div>
          </div>
          
          {/* Section 3: Efficiency */}
          <div className="pt-4 border-t border-gray-700">
            <h4 className="text-sm font-semibold mb-3 text-gray-300">Efficiency</h4>
            <div className="grid grid-cols-2 md:grid-cols-2 gap-4">
              <div>
                <div className="text-xs text-gray-400 mb-1">Profit Factor</div>
                <div className="text-lg font-semibold">{stats.profitFactor}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Rolling 30-Day Win Rate</div>
                <div className="text-lg font-semibold text-green-400">
                  {stats.rolling30DayWinRate !== null ? `${stats.rolling30DayWinRate}%` : 'N/A'}
                </div>
              </div>
            </div>
          </div>
        </div>
      )
    }
  }
  
  const renderFilters = (streamId) => {
    // Get filters for this stream (with defaults if not exists)
    // This ensures UI always has complete filter structure and reflects saved filters
    const filters = getStreamFiltersFromStorage(streamFilters, streamId)
    const availableYears = getAvailableYears()
    const hasFilters = (filters.exclude_days_of_week && filters.exclude_days_of_week.length > 0) || 
                      (filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0) || 
                      (filters.exclude_times && filters.exclude_times.length > 0) ||
                      (filters.include_years && filters.include_years.length > 0)
    
    return (
      <div className="bg-gray-800 rounded-lg p-4 mb-4">
        <div className="flex items-center justify-between mb-3">
          <h4 className="font-medium text-sm">Filters for {streamId}</h4>
          {hasFilters && (
            <span className="text-xs bg-blue-600 px-2 py-1 rounded">Active</span>
          )}
        </div>
        
        <div className={`grid grid-cols-1 md:grid-cols-4 gap-6`}>
          {/* Years Filter */}
          <div className="space-y-2">
            <div className="flex items-center justify-between mb-2">
              <label className="block text-xs font-medium text-gray-400">Isolate Years</label>
              {filters.include_years && filters.include_years.length > 0 && (
                <button
                  type="button"
                  onClick={(e) => {
                    e.preventDefault()
                    e.stopPropagation()
                    // Clear all year filters
                    setStreamFilters(prev => {
                      const updated = { ...prev }
                      if (updated[streamId]) {
                        updated[streamId] = {
                          ...updated[streamId],
                          include_years: []
                        }
                      } else {
                        updated[streamId] = {
                          exclude_days_of_week: [],
                          exclude_days_of_month: [],
                          exclude_times: [],
                          include_years: []
                        }
                      }
                      return updated
                    })
                  }}
                  className="text-xs text-blue-400 hover:text-blue-300 underline"
                  title="Show all years"
                >
                  Show All
                </button>
              )}
            </div>
            <div className="flex flex-wrap gap-1 max-h-32 overflow-y-auto">
              {availableYears.length > 0 ? (
                availableYears.map(year => {
                  const isSelected = filters.include_years && filters.include_years.includes(year)
                  return (
                    <button
                      key={year}
                      type="button"
                      onClick={(e) => {
                        e.preventDefault()
                        e.stopPropagation()
                        updateStreamFilter(streamId, 'include_years', year)
                      }}
                      className={`px-2 py-1 text-xs rounded cursor-pointer ${
                        isSelected
                          ? 'bg-green-600 text-white'
                          : 'bg-gray-700 hover:bg-gray-600'
                      }`}
                    >
                      {year}
                    </button>
                  )
                })
              ) : (
                <span className="text-xs text-gray-500">No years available</span>
              )}
            </div>
            {filters.include_years && filters.include_years.length > 0 && (
              <div className="mt-2 text-xs text-gray-400">
                Selected: {filters.include_years.sort((a, b) => b - a).join(', ')}
              </div>
            )}
          </div>
          
          {/* Days of Week */}
          <div className="space-y-2">
            <label className="block text-xs font-medium mb-2 text-gray-400">Exclude Days</label>
            <div className="flex flex-wrap gap-1">
              {DAYS_OF_WEEK.map(dow => {
                const isExcluded = filters.exclude_days_of_week && filters.exclude_days_of_week.includes(dow)
                return (
                  <button
                    key={dow}
                    type="button"
                    onClick={(e) => {
                      e.preventDefault()
                      e.stopPropagation()
                        updateStreamFilter(streamId, 'exclude_days_of_week', dow)
                    }}
                    className={`px-2 py-1 text-xs rounded cursor-pointer transition-colors ${
                      isExcluded
                        ? 'bg-red-600 text-white'
                        : 'bg-gray-700 hover:bg-gray-600'
                    }`}
                  >
                    {dow.substring(0, 3)}
                  </button>
                )
              })}
            </div>
          </div>
          
          {/* Days of Month */}
          <div className="relative dom-dropdown-container space-y-2">
            <label className="block text-xs font-medium mb-2 text-gray-400">Exclude DOM</label>
            <button
              type="button"
              onClick={(e) => {
                e.preventDefault()
                e.stopPropagation()
                const currentShow = streamFilters[streamId]?._showDomDropdown || false
                setStreamFilters(prev => {
                  const currentFilters = prev[streamId] || {
                    exclude_days_of_week: [],
                    exclude_days_of_month: [],
                    exclude_times: [],
                    include_years: []
                  }
                  return {
                    ...prev,
                    [streamId]: {
                      ...currentFilters,
                      _showDomDropdown: !currentShow
                    }
                  }
                })
              }}
              className="w-full px-2 py-1 text-xs bg-gray-700 border border-gray-600 rounded text-gray-100 text-left flex items-center justify-between hover:bg-gray-600"
            >
              <span>
                {filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0
                  ? `Selected: ${filters.exclude_days_of_month.sort((a, b) => a - b).join(', ')}`
                  : 'Select days to exclude'}
              </span>
              <span className="ml-2">{streamFilters[streamId]?._showDomDropdown ? '▲' : '▼'}</span>
            </button>
            {streamFilters[streamId]?._showDomDropdown && (
              <div 
                className="absolute z-10 w-full mt-1 bg-gray-800 border border-gray-600 rounded shadow-lg max-h-48 overflow-y-auto"
                onClick={(e) => e.stopPropagation()}
                onMouseDown={(e) => e.stopPropagation()}
              >
                <div className="p-2 grid grid-cols-5 gap-1">
                  {Array.from({length: 31}, (_, i) => i + 1).map(day => {
                    const isSelected = filters.exclude_days_of_month?.includes(day)
                    return (
                      <label
                        key={day}
                        onClick={(e) => {
                          e.preventDefault()
                          e.stopPropagation()
                          updateStreamFilter(streamId, 'exclude_days_of_month', day)
                        }}
                        className={`px-2 py-1 text-xs rounded cursor-pointer text-center transition-colors ${
                          isSelected
                            ? 'bg-red-600 text-white'
                            : 'bg-gray-700 text-gray-200 hover:bg-gray-600'
                        }`}
                      >
                        <input
                          type="checkbox"
                          checked={isSelected || false}
                          onChange={() => {}}
                          onClick={(e) => {
                            e.preventDefault()
                            e.stopPropagation()
                            updateStreamFilter(streamId, 'exclude_days_of_month', day)
                          }}
                          className="hidden"
                        />
                        {day}
                      </label>
                    )
                  })}
                </div>
              </div>
            )}
          </div>
          
          {/* Times */}
          <div className="space-y-2">
            <label className="block text-xs font-medium mb-2 text-gray-400">Exclude Times</label>
            <div className="flex flex-wrap gap-1">
              {/* Only show relevant times for this stream */}
              {(() => {
                const relevantTimes = getRelevantTimeSlots(streamId) || AVAILABLE_TIMES
                const excludeTimes = filters.exclude_times || []
                return relevantTimes.map(time => {
                  const isExcluded = excludeTimes.includes(time)
                  return (
                    <button
                      key={time}
                      type="button"
                      onClick={(e) => {
                        e.preventDefault()
                        e.stopPropagation()
                        updateStreamFilter(streamId, 'exclude_times', time)
                      }}
                      className={`px-2 py-1 text-xs rounded font-mono cursor-pointer transition-colors ${
                        isExcluded
                          ? 'bg-red-600 text-white'
                          : 'bg-gray-700 hover:bg-gray-600'
                      }`}
                    >
                      {time}
                    </button>
                  )
                })
              })()}
            </div>
          </div>
        </div>
      </div>
    )
  }
  
  // Sort columns to maintain DEFAULT_COLUMNS order, then time slots sorted by time, then extras
  // TradeID always comes first if selected
  const sortColumnsByDefaultOrder = (columns) => {
    const sorted = []
    const timeSlotColumns = []
    const extras = []
    
    // TradeID always comes first if it's selected
    if (columns.includes('TradeID')) {
      sorted.push('TradeID')
    }
    
    // Then add DEFAULT_COLUMNS in exact order (excluding TradeID if it was already added)
    DEFAULT_COLUMNS.forEach(defaultCol => {
      if (columns.includes(defaultCol)) {
        sorted.push(defaultCol)
      }
    })
    
    // Extract time slot columns (format: "HH:MM Rolling" or "HH:MM Points")
    const timeSlotRegex = /^(\d{2}:\d{2})\s+(Rolling|Points)$/
    columns.forEach(col => {
      const match = col.match(timeSlotRegex)
      if (match) {
        timeSlotColumns.push({ name: col, time: match[1], type: match[2] })
      } else if (col !== 'TradeID' && !DEFAULT_COLUMNS.includes(col) && !sorted.includes(col)) {
        extras.push(col)
      }
    })
    
    // Sort time slot columns: by time first, then by type (Points before Rolling)
    timeSlotColumns.sort((a, b) => {
      if (a.time !== b.time) {
        return a.time.localeCompare(b.time) // Sort by time
      }
      // Same time: Points comes before Rolling
      if (a.type === 'Points' && b.type === 'Rolling') return -1
      if (a.type === 'Rolling' && b.type === 'Points') return 1
      return 0
    })
    
    return [...sorted, ...timeSlotColumns.map(t => t.name), ...extras]
  }
  
  const toggleColumn = (col) => {
    setSelectedColumns(prev => {
      const currentTab = activeTab
      const currentCols = prev[currentTab] || DEFAULT_COLUMNS
      
      // Toggle column
      const newCols = currentCols.includes(col)
        ? currentCols.filter(c => c !== col)
        : [...currentCols, col]
      
      // Sort to maintain DEFAULT_COLUMNS order
      const sortedCols = sortColumnsByDefaultOrder(newCols)
      
      const updated = {
        ...prev,
        [currentTab]: sortedCols
      }
      localStorage.setItem('matrix_selected_columns', JSON.stringify(updated))
      return updated
    })
  }
  
  const getSelectedColumnsForTab = (tabId) => {
    const cols = selectedColumns[tabId] || DEFAULT_COLUMNS
    // Ensure columns are always in the correct order
    return sortColumnsByDefaultOrder(cols)
  }
  // Get relevant time slots for a stream (S1 or S2)
  const getRelevantTimeSlots = (streamId) => {
    if (!streamId || streamId === 'master') return null
    // Stream 1 (ES1, GC1, CL1, NQ1, NG1, YM1) -> S1 times: 07:30, 08:00, 09:00
    // Stream 2 (ES2, GC2, CL2, NQ2, NG2, YM2) -> S2 times: 09:30, 10:00, 10:30, 11:00
    const isStream1 = streamId.endsWith('1')
    if (isStream1) {
      return ['07:30', '08:00', '09:00']
    } else {
      return ['09:30', '10:00', '10:30', '11:00']
    }
  }

  // Filter columns based on stream (only show relevant time slot columns)
  const getFilteredColumns = (columns, streamId) => {
    if (!streamId || streamId === 'master') {
      return columns // Master shows all columns
    }
    
    const relevantTimes = getRelevantTimeSlots(streamId)
    if (!relevantTimes) return columns
    
    return columns.filter(col => {
      // Always include non-time-slot columns
      if (!col.includes(' Points') && !col.includes(' Rolling')) {
        return true
      }
      // For time slot columns, only include if time matches stream's session
      return relevantTimes.some(time => col.startsWith(time))
    })
  }

  const renderColumnSelector = () => {
    if (!showColumnSelector || availableColumns.length === 0) return null
    
    // Filter columns based on active tab (stream)
    const filteredColumns = getFilteredColumns(availableColumns, activeTab)
    // Sort columns to maintain DEFAULT_COLUMNS order in the selector too
    const sortedColumns = sortColumnsByDefaultOrder(filteredColumns)
    
    return (
      <div className="mb-4 bg-gray-900 rounded-lg p-4 border border-gray-700">
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-lg font-semibold">Select Columns</h3>
          <button
            onClick={() => setShowColumnSelector(false)}
            className="text-gray-400 hover:text-gray-300"
          >
            ✕
          </button>
        </div>
        <div className="flex flex-col gap-2 max-h-64 overflow-y-auto">
          {sortedColumns.map(col => {
            const currentCols = getSelectedColumnsForTab(activeTab)
            // Map column names to display names
            const displayName = col === 'StopLoss' ? 'Stop Loss' : col
            return (
              <label key={col} className="flex items-center space-x-2 cursor-pointer hover:bg-gray-900 p-2 rounded">
                <input
                  type="checkbox"
                  checked={currentCols.includes(col)}
                  onChange={() => toggleColumn(col)}
                  className="w-4 h-4 text-blue-600 bg-gray-800 border-gray-700 rounded focus:ring-blue-500"
                />
                <span className="text-sm text-gray-300">{displayName}</span>
              </label>
            )
          })}
        </div>
        <div className="mt-3 flex gap-2">
          <button
            onClick={() => {
              setSelectedColumns(prev => {
                const updated = {
                  ...prev,
                  [activeTab]: sortColumnsByDefaultOrder(DEFAULT_COLUMNS)
                }
                localStorage.setItem('matrix_selected_columns', JSON.stringify(updated))
                return updated
              })
            }}
            className="px-3 py-1 text-sm bg-gray-800 hover:bg-gray-800 rounded"
          >
            Reset to Default
          </button>
          <button
            onClick={() => {
              // Select all filtered columns for current stream
              const filteredCols = getFilteredColumns(availableColumns, activeTab)
              const sortedCols = sortColumnsByDefaultOrder(filteredCols)
              setSelectedColumns(prev => {
                const updated = {
                  ...prev,
                  [activeTab]: sortedCols
                }
                localStorage.setItem('matrix_selected_columns', JSON.stringify(updated))
                return updated
              })
            }}
            className="px-3 py-1 text-sm bg-gray-800 hover:bg-gray-800 rounded"
          >
            Select All
          </button>
        </div>
      </div>
    )
  }
  
  // Virtualized row renderer for react-window v2
  // In v2, rowProps are spread directly into the component props
  const Row = useCallback(({ index, style, rows, columnsToShow, streamId, getColumnWidth, totalFiltered, workerFilteredIndices }) => {
    // Safety checks - ensure all required props are defined
    if (!rows || !Array.isArray(rows)) return null
    if (!columnsToShow || !Array.isArray(columnsToShow)) return null
    if (typeof getColumnWidth !== 'function') return null
    if (typeof index !== 'number' || index < 0) return null
    
    // If row not loaded yet, show loading placeholder
    if (index >= rows.length && workerFilteredIndices && Array.isArray(workerFilteredIndices) && index < totalFiltered) {
      return (
        <div style={style} className="flex border-b border-gray-700 bg-gray-800">
          {columnsToShow.map(col => (
            <div 
              key={col} 
              className="p-2 border-r border-gray-700 flex-shrink-0 text-left text-sm" 
              style={{ width: `${getColumnWidth(col)}px` }}
            >
              <span className="text-gray-500">...</span>
            </div>
          ))}
        </div>
      )
    }
    
    const row = rows[index]
    if (!row) return null
    
    // Check if row is filtered
    const isFiltered = row.final_allowed === false
    // Check if filtered due to DOW (for DOW cell highlighting)
    const filterReasons = row.filter_reasons || ''
    const isFilteredByDOW = isFiltered && filterReasons.toLowerCase().includes('dow_filter')
    
    return (
      <div 
        style={style} 
        className={`flex border-b border-gray-700 hover:bg-gray-900 ${
          isFiltered ? 'bg-red-900/20 opacity-75' : ''
        }`}
      >
        {columnsToShow.map(col => {
          let value = row[col]
          // Handle column name variations
          if (col === 'Symbol' && !value) {
            value = row['Instrument'] || ''
          }
          if (col === 'Date' && value) {
            value = new Date(value).toLocaleDateString()
          }
          // Calculate DOW (Day of Week) from Date
          if (col === 'DOW') {
            const dateValue = row['Date']
            if (dateValue) {
              try {
                const date = new Date(dateValue)
                if (!isNaN(date.getTime())) {
                  value = date.toLocaleDateString('en-US', { weekday: 'short' }).toUpperCase()
                } else {
                  value = '-'
                }
              } catch {
                value = '-'
              }
            } else {
              value = '-'
            }
          }
          // For filtered rows, show "—" for Profit columns
          if (isFiltered && (col === 'Profit' || col === 'Profit ($)')) {
            value = '—'
          } else if (col === 'Profit ($)') {
            // Calculate Profit ($) from Profit column (only for allowed rows)
            const profitValue = parseFloat(row.Profit) || 0
            const symbol = row.Symbol || row.Instrument || 'ES'
            const contractValues = {
              'ES': 50, 'NQ': 10, 'YM': 5, 'CL': 1000, 'NG': 10000, 'GC': 100
            }
            const contractValue = contractValues[symbol.toUpperCase()] || 50
            const dollarValue = profitValue * contractValue
            value = new Intl.NumberFormat('en-US', {
              style: 'currency',
              currency: 'USD',
              minimumFractionDigits: 0,
              maximumFractionDigits: 0
            }).format(dollarValue)
          }
          // Format numeric columns (skip Profit formatting for filtered rows - already set to "—")
          if (!isFiltered && ['Profit', 'Peak', 'Target', 'Range', 'StopLoss'].includes(col)) {
            const symbol = (row.Symbol || row.Instrument || '').toUpperCase()
            const baseSymbol = symbol.replace(/\d+$/, '') || symbol
            const isNG = baseSymbol === 'NG'
            const decimalPlaces = isNG ? 3 : 2
            
            if (col === 'StopLoss') {
              if (value === null || value === undefined) {
                value = '-'
              } else {
                const numValue = typeof value === 'number' ? value : parseFloat(value)
                if (numValue === 0 || numValue === '0' || numValue === 0.0) {
                  value = isNG ? '0.000' : '0.00'
                } else if (!isNaN(numValue) && isFinite(numValue)) {
                  value = numValue.toFixed(decimalPlaces)
                } else {
                  value = '-'
                }
              }
            } else if (value !== null && value !== undefined) {
              const numValue = parseFloat(value)
              if (!isNaN(numValue)) {
                value = numValue.toFixed(decimalPlaces)
              }
            }
          }
          // Time Change - format for better visual alignment
          if (col === 'Time Change') {
            if (value && typeof value === 'string' && value.trim() !== '') {
              // Normalize the arrow to a consistent format: handle both -> and → with any spacing
              value = value.trim()
                .replace(/\s*->\s*/g, ' → ') // Replace -> with → 
                .replace(/\s*→\s*/g, ' → ') // Normalize any existing → to consistent spacing
                .trim()
            } else {
              value = ''
            }
          }
          // Format time slot columns
          if (col.includes(' Rolling') && value !== null && value !== undefined) {
            const numValue = parseFloat(value)
            if (!isNaN(numValue)) value = numValue.toFixed(2)
          }
          if (col.includes(' Points') && value !== null && value !== undefined) {
            const numValue = parseFloat(value)
            if (!isNaN(numValue)) value = numValue.toFixed(0)
          }
          
          // Determine cell styling
          let cellClassName = `p-2 border-r border-gray-700 flex-shrink-0 text-sm ${col === 'Time Change' ? 'text-center whitespace-nowrap' : 'text-left'}`
          // Highlight DOW cell if filtered due to DOW
          if (col === 'DOW' && isFilteredByDOW) {
            cellClassName += ' bg-red-600/30'
          }
          
          // Add filter reasons badge/tooltip for filtered rows
          const showFilterBadge = isFiltered && col === 'Date' && filterReasons
          
          return (
            <div 
              key={col} 
              className={cellClassName}
              style={{ width: `${getColumnWidth(col)}px` }}
              title={isFiltered && filterReasons ? `Filtered: ${filterReasons}` : undefined}
            >
              {col === 'Time Change' ? (
                value && value.trim() ? (
                  <span className="font-mono tabular-nums">
                    {(() => {
                      // Split on arrow to style separately
                      const parts = value.split('→')
                      if (parts.length === 2) {
                        return (
                          <>
                            <span className="text-gray-200">{parts[0].trim()}</span>
                            <span className="text-gray-500 mx-1">→</span>
                            <span className="text-gray-200">{parts[1].trim()}</span>
                          </>
                        )
                      }
                      return <span className="text-gray-200">{value}</span>
                    })()}
                  </span>
                ) : (
                  ''
                )
              ) : (
                <>
                  {value !== null && value !== undefined ? String(value) : '-'}
                  {showFilterBadge && (
                    <span className="ml-1 text-xs text-red-400" title={filterReasons}>
                      ⚠
                    </span>
                  )}
                </>
              )}
            </div>
          )
        })}
      </div>
    )
  }, [])
  
  // State to track loaded rows for incremental loading
  const [loadedRows, setLoadedRows] = useState([])
  const [loadingMoreRows, setLoadingMoreRows] = useState(false)
  
  // Update loaded rows when worker filtered rows change
  // Reset loadedRows when stream/activeTab changes to prevent stale data
  const prevActiveTabRef = useRef(activeTab)
  useEffect(() => {
    // Reset loadedRows when activeTab changes (stream switch)
    if (prevActiveTabRef.current !== activeTab) {
      setLoadedRows([])
      prevActiveTabRef.current = activeTab
    }
    
    if (workerReady && workerFilteredRows && workerFilteredRows.length > 0) {
      // Set loadedRows when we get new filtered rows (worker re-filtered)
      setLoadedRows(workerFilteredRows)
    }
  }, [workerReady, workerFilteredRows, activeTab])
  
  // Auto-load ALL rows when filtered indices change (no limit)
  useEffect(() => {
    if (workerReady && workerFilteredIndices && workerFilteredIndices.length > 0 && workerGetRows) {
      const currentLoaded = loadedRows.length
      if (currentLoaded < workerFilteredIndices.length && !loadingMoreRows) {
        // Load all remaining rows automatically in chunks
        setLoadingMoreRows(true)
        const neededIndices = workerFilteredIndices.slice(currentLoaded)
        
        if (neededIndices.length > 0) {
          // Load in chunks to avoid blocking UI
          const CHUNK_SIZE = 1000
          const chunks = []
          for (let i = 0; i < neededIndices.length; i += CHUNK_SIZE) {
            chunks.push(neededIndices.slice(i, i + CHUNK_SIZE))
          }
          
          let loadedChunks = 0
          const loadChunk = (chunkIndex) => {
            if (chunkIndex >= chunks.length) {
              setLoadingMoreRows(false)
              return
            }
            
            workerGetRows(chunks[chunkIndex], (newRows) => {
              setLoadedRows(prev => {
                const existingLength = prev.length
                if (existingLength >= currentLoaded + loadedChunks * CHUNK_SIZE + newRows.length) {
                  return prev
                }
                return [...prev, ...newRows]
              })
              loadedChunks++
              // Load next chunk with small delay to yield to UI
              setTimeout(() => loadChunk(chunkIndex + 1), 10)
            })
          }
          
          loadChunk(0)
        } else {
          setLoadingMoreRows(false)
        }
      }
    }
  }, [workerReady, workerFilteredIndices?.length, workerGetRows, loadedRows.length, loadingMoreRows])
  
  // Load more rows function - use ref to access current loadedRows without dependency
  const loadedRowsRef = useRef([])
  useEffect(() => {
    loadedRowsRef.current = loadedRows
  }, [loadedRows])
  
  const loadMoreRows = useCallback((startIndex, stopIndex) => {
    if (!workerReady || !workerGetRows || !workerFilteredIndices || loadingMoreRows) {
      return
    }
    
    // Get current loaded length from ref to avoid stale closure
    const currentLoaded = loadedRowsRef.current.length > 0 
      ? loadedRowsRef.current 
      : (workerFilteredRows || [])
    const currentLoadedLength = currentLoaded.length
    
    // REMOVED LIMIT: Load ALL remaining rows, not just chunks
    // Load all remaining rows if we're near the end or scrolling
    const shouldLoad = workerFilteredIndices.length > currentLoadedLength
    
    if (shouldLoad) {
      setLoadingMoreRows(true)
      // Load ALL remaining rows - no limit
      const neededIndices = workerFilteredIndices.slice(currentLoadedLength)
      
      if (neededIndices.length > 0) {
        workerGetRows(neededIndices, (newRows) => {
          setLoadedRows(prev => {
            // Avoid duplicates - check if we already have these rows
            const existingLength = prev.length
            if (existingLength >= currentLoadedLength + newRows.length) {
              // Already loaded, don't add again
              return prev
            }
            return [...prev, ...newRows]
          })
          setLoadingMoreRows(false)
        })
      } else {
        setLoadingMoreRows(false)
      }
    }
  }, [workerReady, workerGetRows, workerFilteredIndices, loadingMoreRows, workerFilteredRows])
  
  const renderDataTable = (data, streamId = null) => {
    // Use worker filtered data if available (much faster - off main thread)
    let filtered = []
    let totalFiltered = 0
    
    if (workerReady && workerFilteredIndices.length > 0) {
      // Use loaded rows (incrementally loaded as user scrolls)
      // Start with workerFilteredRows, then use loadedRows as they accumulate
      let baseFiltered = loadedRows.length > 0 ? loadedRows : (workerFilteredRows || [])
      
      // Trigger initial load if we have indices but no loaded rows yet
      if (loadedRows.length === 0 && workerFilteredRows && workerFilteredRows.length > 0) {
        // Already loaded via useEffect, but ensure state is set
        setLoadedRows(workerFilteredRows)
        baseFiltered = workerFilteredRows
      }
      
      // IMPORTANT: Apply stream filter when using worker data
      // The worker filters by stream filters but may return multiple streams when streamId is 'master'
      // When viewing a specific stream tab (like 'ES1'), we need to filter to that stream only
      if (streamId && streamId !== 'master') {
        filtered = baseFiltered.filter(row => row.Stream === streamId)
        // Use the actual filtered length, not the worker indices length
        totalFiltered = filtered.length
      } else {
        filtered = baseFiltered
        // For master, we can use filteredLength or the actual array length
        totalFiltered = filteredLength || filtered.length
      }
    } else {
      // Fallback to main thread filtering only if worker not ready
      filtered = getFilteredData(data, streamId)
      totalFiltered = filtered.length
    }
    
    // Apply "Show/Hide Filtered Days" toggle
    if (!showFilteredDays) {
      filtered = filtered.filter(row => row.final_allowed !== false)
      totalFiltered = filtered.length
    }
    
    if (totalFiltered === 0) {
      return (
        <div className="text-center py-8 text-gray-400">
          No data available{streamId ? ` for ${streamId}` : ''}
        </div>
      )
    }
    
    // Use selected columns for the current tab, fallback to default if none selected
    const tabId = streamId || 'master'
    let columnsToShow = getSelectedColumnsForTab(tabId)
    
    // Ensure columnsToShow is always an array
    if (!Array.isArray(columnsToShow)) {
      columnsToShow = []
    }
    
    // Filter columns based on stream (only show relevant time slot columns)
    columnsToShow = getFilteredColumns(columnsToShow, streamId)
    
    // Remove 'SL' column if present (replaced by 'StopLoss')
    columnsToShow = columnsToShow.filter(col => col !== 'SL')
    
    // Ensure filtered is always an array
    if (!Array.isArray(filtered)) {
      filtered = []
    }
    
    // Ensure workerFilteredIndices is always an array (can be undefined)
    const safeWorkerFilteredIndices = Array.isArray(workerFilteredIndices) ? workerFilteredIndices : []
    
    // Ensure totalFiltered is a valid number
    const safeTotalFiltered = typeof totalFiltered === 'number' && totalFiltered > 0 ? totalFiltered : 0
    
    // Calculate column widths based on content
    const getColumnWidth = (col) => {
      const widths = {
        'Date': 110,
        'DOW': 60,
        'Time': 70,
        'EntryTime': 130,
        'ExitTime': 130,
        'Instrument': 90,
        'Stream': 70,
        'Session': 60,
        'Direction': 70,
        'Target': 80,
        'Range': 80,
        'StopLoss': 70,
        'Peak': 70,
        'Result': 70,
        'Profit': 80,
        'Time Change': 120, // Increased width for better spacing with monospaced font
        'Profit ($)': 120
      }
      return widths[col] || 120
    }
    
    const totalWidth = columnsToShow.reduce((sum, col) => sum + getColumnWidth(col), 0)
    
    // Don't render List if we don't have valid data
    if (safeTotalFiltered === 0 || columnsToShow.length === 0 || filtered.length === 0) {
      return (
        <div className="text-center py-8 text-gray-400">
          No data available{streamId ? ` for ${streamId}` : ''}
        </div>
      )
    }
    
    // Prepare rowProps object - ensure all values are defined
    const rowProps = {
      rows: filtered,
      columnsToShow: columnsToShow,
      streamId: streamId || null,
      getColumnWidth: getColumnWidth,
      totalFiltered: safeTotalFiltered,
      workerFilteredIndices: safeWorkerFilteredIndices
    }
    
    return (
      <div style={{ overflowX: 'auto', overflowY: 'visible' }}>
        {/* Table header - sticky */}
        <div className="flex bg-gray-800 sticky top-0 z-10 border-b border-gray-700" style={{ width: `${totalWidth}px` }}>
          {columnsToShow.map(col => {
            // Map column names to display names
            const displayName = col === 'StopLoss' ? 'Stop Loss' : col
            const isTimeChange = col === 'Time Change'
            return (
              <div 
                key={col} 
                className={`p-2 font-medium border-r border-gray-700 flex-shrink-0 text-sm ${isTimeChange ? 'text-center' : 'text-left'}`}
                style={{ width: `${getColumnWidth(col)}px` }}
              >
                {displayName}
              </div>
            )
          })}
        </div>
        {/* Virtualized table body */}
        <List
          rowCount={safeTotalFiltered} // Use total filtered count, not just loaded rows
          rowHeight={35} // Fixed row height
          rowComponent={Row}
          rowProps={rowProps}
          overscanCount={10} // Render 10 extra rows for smooth scrolling
          style={{ height: 600, width: `${totalWidth}px` }} // Fixed height and width for virtual list
          onRowsRendered={({ startIndex, stopIndex }) => {
            // Load more rows when user scrolls near the end
            // Only call if we have more data to load (filtered.length < totalFiltered)
            if (workerReady && workerFilteredIndices && workerFilteredIndices.length > 0 && filtered.length < totalFiltered) {
              loadMoreRows(startIndex, stopIndex)
            }
          }}
        />
        {filtered.length < totalFiltered && (
          <div className="text-center py-4 text-gray-400 text-sm">
            Showing {filtered.length} of {totalFiltered} rows {loadingMoreRows && '(loading more...)'}
          </div>
        )}
      </div>
    )
  }
  
  // Helper function to get ordinal suffix (1st, 2nd, 3rd, etc.)
  const getOrdinalSuffix = (num) => {
    const n = parseInt(num)
    const lastDigit = n % 10
    const lastTwoDigits = n % 100
    
    if (lastTwoDigits >= 11 && lastTwoDigits <= 13) {
      return 'th'
    }
    
    switch (lastDigit) {
      case 1: return 'st'
      case 2: return 'nd'
      case 3: return 'rd'
      default: return 'th'
    }
  }
  
  // Render profit breakdown tables
  const renderProfitTable = (data, periodType) => {
    // Wrap in try-catch to prevent white screen on errors
    try {
    // Ensure data is always an object (never undefined or null)
    if (!data || typeof data !== 'object' || Array.isArray(data)) {
      return (
        <div className="text-center py-8 text-gray-400">
          No data available
        </div>
      )
    }
    
    // Ensure data has keys (is not an empty object, or handle empty object gracefully)
    const dataKeys = Object.keys(data)
    if (dataKeys.length === 0) {
      return (
        <div className="text-center py-8 text-gray-400">
          No data available
        </div>
      )
    }
    
    const formatCurrency = (value) => {
      return new Intl.NumberFormat('en-US', {
        style: 'currency',
        currency: 'USD',
        minimumFractionDigits: 0,
        maximumFractionDigits: 0
      }).format(value)
    }
    
    // Get all unique streams from data
    const allStreams = new Set()
    Object.values(data).forEach(periodData => {
      if (periodData && typeof periodData === 'object') {
        Object.keys(periodData).forEach(stream => allStreams.add(stream))
      }
    })
    const sortedStreams = Array.from(allStreams).sort()
    
    // Get sorted periods
    // For DOM (Day of Month), sort numerically; for month/year/date/day, sort with latest first; otherwise sort as strings
    const sortedPeriods = periodType === 'dom' 
      ? Object.keys(data).sort((a, b) => parseInt(a) - parseInt(b))
      : periodType === 'month'
      ? Object.keys(data).sort((a, b) => {
          return b.localeCompare(a)
        })
      : periodType === 'date'
      ? Object.keys(data).sort((a, b) => {
          return b.localeCompare(a) // Latest first (YYYY-MM-DD format)
        })
      : periodType === 'day'
      ? Object.keys(data).sort((a, b) => {
          // Sort day of week in order: Monday, Tuesday, Wednesday, Thursday, Friday
          const dowOrder = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
          return dowOrder.indexOf(a) - dowOrder.indexOf(b)
        })
      : periodType === 'year'
      ? Object.keys(data).sort((a, b) => {
          return parseInt(b) - parseInt(a)
        })
      : Object.keys(data).sort()
    
    if (sortedPeriods.length === 0) {
      return (
        <div className="text-center py-8 text-gray-400">
          No data available
        </div>
      )
    }
    
    // IMPORTANT: Use List component ONLY for 'date' tab (calendar dates with many rows)
    // Return regular table immediately for other period types (day=DOW, time, dom, month, year)
    // This prevents any List-related code from executing for tabs with fewer rows
    if (periodType !== 'date') {
      // Regular table for other period types (fewer rows)
      return (
        <div className="overflow-x-auto">
          <table className="w-full border-collapse border border-gray-700">
            <thead>
              <tr className="bg-gray-800">
                <th className="p-3 border border-gray-700 text-left font-semibold bg-gray-800">
                  {periodType === 'time' ? 'Time' : periodType === 'day' ? 'DOW' : periodType === 'dom' ? 'Day of Month' : periodType === 'month' ? 'Month' : 'Year'}
                </th>
                {sortedStreams.map(stream => (
                  <th key={stream} className="p-3 border border-gray-700 text-right font-semibold bg-gray-800">
                    {stream}
                  </th>
                ))}
                <th className="p-3 border border-gray-700 text-right font-semibold bg-gray-700">
                  Total
                </th>
              </tr>
            </thead>
            <tbody>
              {sortedPeriods.map(period => {
                const periodData = data[period] || {}
                if (!periodData || typeof periodData !== 'object') return null
                let total = 0
                
                return (
                  <tr key={period} className="hover:bg-gray-900">
                    <td className="p-3 border border-gray-700 font-medium">
                    {periodType === 'time'
                      ? period
                      : periodType === 'day'
                      ? period // Day of week name (Monday, Tuesday, etc.)
                      : periodType === 'dom'
                        ? `${period}${getOrdinalSuffix(period)}`
                        : periodType === 'month'
                        ? new Date(period + '-01').toLocaleDateString('en-US', { year: 'numeric', month: 'long' })
                        : period}
                    </td>
                    {sortedStreams.map(stream => {
                      const profit = (periodData && periodData[stream]) || 0
                      total += profit
                      return (
                        <td key={stream} className={`p-3 border border-gray-700 text-right ${
                          profit > 0 ? 'text-green-400' : profit < 0 ? 'text-red-400' : 'text-gray-400'
                        }`}>
                          {formatCurrency(profit)}
                        </td>
                      )
                    })}
                    <td className={`p-3 border border-gray-700 text-right font-semibold bg-gray-800 ${
                      total > 0 ? 'text-green-400' : total < 0 ? 'text-red-400' : 'text-gray-400'
                    }`}>
                      {formatCurrency(total)}
                    </td>
                  </tr>
                )
              })}
              <tr className="bg-gray-800 font-semibold">
                <td className="p-3 border border-gray-700">Total</td>
                {sortedStreams.map(stream => {
                  const streamTotal = sortedPeriods.reduce((sum, period) => {
                    const periodData = data[period]
                    return sum + ((periodData && periodData[stream]) || 0)
                  }, 0)
                  return (
                    <td key={stream} className={`p-3 border border-gray-700 text-right ${
                      streamTotal > 0 ? 'text-green-400' : streamTotal < 0 ? 'text-red-400' : 'text-gray-400'
                    }`}>
                      {formatCurrency(streamTotal)}
                    </td>
                  )
                })}
                <td className={`p-3 border border-gray-700 text-right bg-gray-700 ${
                  sortedPeriods.reduce((sum, period) => {
                    const periodData = data[period]
                    if (!periodData || typeof periodData !== 'object') return sum
                    return sum + Object.values(periodData).reduce((s, v) => s + (v || 0), 0)
                  }, 0) > 0 ? 'text-green-400' : 'text-red-400'
                }`}>
                  {formatCurrency(
                    sortedPeriods.reduce((sum, period) => {
                      const periodData = data[period]
                      if (!periodData || typeof periodData !== 'object') return sum
                      return sum + Object.values(periodData).reduce((s, v) => s + (v || 0), 0)
                    }, 0)
                  )}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      )
    }
    
    // Simple table for date tab - shows profit for each calendar day
    if (periodType === 'date') {
      return (
        <div className="overflow-x-auto">
          <table className="w-full border-collapse border border-gray-700">
            <thead>
              <tr className="bg-gray-800">
                <th className="p-3 border border-gray-700 text-left font-semibold bg-gray-800">Day</th>
                {sortedStreams.map(stream => (
                  <th key={stream} className="p-3 border border-gray-700 text-right font-semibold bg-gray-800">
                    {stream}
                  </th>
                ))}
                <th className="p-3 border border-gray-700 text-right font-semibold bg-gray-700">Total</th>
              </tr>
            </thead>
            <tbody>
              {sortedPeriods.map(period => {
                const periodData = data[period] || {}
                if (!periodData || typeof periodData !== 'object') return null
                let total = 0
                
                return (
                  <tr key={period} className="hover:bg-gray-900">
                    <td className="p-3 border border-gray-700 font-medium">
                      {new Date(period + 'T00:00:00').toLocaleDateString('en-US', { 
                        year: 'numeric', 
                        month: 'short', 
                        day: 'numeric', 
                        weekday: 'short' 
                      })}
                    </td>
                    {sortedStreams.map(stream => {
                      const profit = (periodData && periodData[stream]) || 0
                      total += profit
                      return (
                        <td key={stream} className={`p-3 border border-gray-700 text-right ${
                          profit > 0 ? 'text-green-400' : profit < 0 ? 'text-red-400' : 'text-gray-400'
                        }`}>
                          {formatCurrency(profit)}
                        </td>
                      )
                    })}
                    <td className={`p-3 border border-gray-700 text-right font-semibold bg-gray-800 ${
                      total > 0 ? 'text-green-400' : total < 0 ? 'text-red-400' : 'text-gray-400'
                    }`}>
                      {formatCurrency(total)}
                    </td>
                  </tr>
                )
              })}
              {/* Totals row */}
              <tr className="bg-gray-800 font-semibold">
                <td className="p-3 border border-gray-700">Total</td>
                {sortedStreams.map(stream => {
                  const streamTotal = sortedPeriods.reduce((sum, period) => {
                    const periodData = data[period]
                    return sum + ((periodData && periodData[stream]) || 0)
                  }, 0)
                  return (
                    <td key={stream} className={`p-3 border border-gray-700 text-right ${
                      streamTotal > 0 ? 'text-green-400' : streamTotal < 0 ? 'text-red-400' : 'text-gray-400'
                    }`}>
                      {formatCurrency(streamTotal)}
                    </td>
                  )
                })}
                <td className={`p-3 border border-gray-700 text-right bg-gray-700 ${
                  sortedPeriods.reduce((sum, period) => {
                    const periodData = data[period]
                    if (!periodData || typeof periodData !== 'object') return sum
                    return sum + Object.values(periodData).reduce((s, v) => s + (v || 0), 0)
                  }, 0) > 0 ? 'text-green-400' : 'text-red-400'
                }`}>
                  {formatCurrency(
                    sortedPeriods.reduce((sum, period) => {
                      const periodData = data[period]
                      if (!periodData || typeof periodData !== 'object') return sum
                      return sum + Object.values(periodData).reduce((s, v) => s + (v || 0), 0)
                    }, 0)
                  )}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      )
    }
    } catch (error) {
      console.error('Error rendering profit table:', error, { periodType, dataKeys: data ? Object.keys(data).length : 0 })
      return (
        <div className="text-center py-8 text-red-400">
          Error rendering table: {error.message}
        </div>
      )
    }
  }
  
  
  // Use worker stats instead of calculating in React
  const memoizedActiveTabStats = useMemo(() => {
    if (!workerStats) return null
    return formatWorkerStats(workerStats, activeTab === 'timetable' ? 'master' : activeTab)
  }, [workerStats, activeTab, formatWorkerStats])
  
  // Get filtered data length from worker
  const filteredDataLength = filteredLength || 0
  
  // Note: Profit breakdown calculations still use old filtering for now
  // TODO: Move these to worker for better performance
  const memoizedMasterFilteredData = useMemo(() => {
    return getFilteredData(masterData, 'master')
  }, [masterData, streamFilters])
  
  const memoizedActiveTabFilteredData = useMemo(() => {
    if (activeTab === 'master') return memoizedMasterFilteredData
    return getFilteredData(masterData, activeTab)
  }, [masterData, activeTab, streamFilters, memoizedMasterFilteredData])
  
  // Profit breakdown calculations - now using worker for better performance
  const [profitBreakdowns, setProfitBreakdowns] = useState({})
  
  // Store profit breakdowns from worker as they arrive
  useEffect(() => {
    if (workerProfitBreakdown && workerBreakdownType) {
      // workerBreakdownType includes the suffix like 'time_before' or 'time_after'
      setProfitBreakdowns(prev => ({
        ...prev,
        [workerBreakdownType]: workerProfitBreakdown
      }))
    }
  }, [workerProfitBreakdown, workerBreakdownType])
  
  // Calculate profit breakdowns in worker when needed (lazy - only when tab is active)
  useEffect(() => {
    if (workerReady && masterData.length > 0 && calculateProfitBreakdown) {
      const activeBreakdownTabs = ['time', 'day', 'dom', 'date', 'month', 'year']
      
      // Only calculate if we're on a breakdown tab
      if (activeBreakdownTabs.includes(activeTab)) {
        const streamId = 'master' // Breakdowns always use master stream
        
        // Calculate "before filters" (using all data)
        // The worker will send back with breakdownType: `${activeTab}_before`
        calculateProfitBreakdown(streamFilters, streamId, masterContractMultiplier, `${activeTab}_before`, false)
        
        // Calculate "after filters" (using filtered data)
        // The worker will send back with breakdownType: `${activeTab}_after`
        calculateProfitBreakdown(streamFilters, streamId, masterContractMultiplier, `${activeTab}_after`, true)
      }
    }
  }, [workerReady, masterData.length, masterContractMultiplier, streamFilters, activeTab, calculateProfitBreakdown])
  
  // Memoized profit breakdowns (fallback to main thread if worker not ready)
  const memoizedTimeProfitBefore = useMemo(() => {
    if (profitBreakdowns['time_before']) return profitBreakdowns['time_before']
    if (!workerReady) return calculateTimeProfitLocal(masterData)
    return {}
  }, [profitBreakdowns, workerReady, masterData, masterContractMultiplier])
  
  const memoizedTimeProfitAfter = useMemo(() => {
    if (profitBreakdowns['time_after']) return profitBreakdowns['time_after']
    if (!workerReady) return calculateTimeProfitLocal(memoizedMasterFilteredData)
    return {}
  }, [profitBreakdowns, workerReady, memoizedMasterFilteredData, masterContractMultiplier])
  
  const memoizedDayProfitBefore = useMemo(() => {
    if (profitBreakdowns['day_before']) return profitBreakdowns['day_before'] || {}
    if (!workerReady) return calculateDailyProfitLocal(masterData) || {}
    return {}
  }, [profitBreakdowns, workerReady, masterData, masterContractMultiplier])
  
  const memoizedDayProfitAfter = useMemo(() => {
    if (profitBreakdowns['day_after']) return profitBreakdowns['day_after'] || {}
    if (!workerReady) return calculateDailyProfitLocal(memoizedMasterFilteredData) || {}
    return {}
  }, [profitBreakdowns, workerReady, memoizedMasterFilteredData, masterContractMultiplier])
  
  const memoizedDOMProfitBefore = useMemo(() => {
    if (profitBreakdowns['dom_before']) return profitBreakdowns['dom_before']
    if (!workerReady) return calculateDOMProfitLocal(masterData)
    return {}
  }, [profitBreakdowns, workerReady, masterData, masterContractMultiplier])
  
  const memoizedDOMProfitAfter = useMemo(() => {
    if (profitBreakdowns['dom_after']) return profitBreakdowns['dom_after']
    if (!workerReady) return calculateDOMProfitLocal(memoizedMasterFilteredData)
    return {}
  }, [profitBreakdowns, workerReady, memoizedMasterFilteredData, masterContractMultiplier])
  
  const memoizedMonthProfitBefore = useMemo(() => {
    if (profitBreakdowns['month_before']) return profitBreakdowns['month_before']
    if (!workerReady) return calculateMonthlyProfitLocal(masterData)
    return {}
  }, [profitBreakdowns, workerReady, masterData, masterContractMultiplier])
  
  const memoizedMonthProfitAfter = useMemo(() => {
    if (profitBreakdowns['month_after']) return profitBreakdowns['month_after']
    if (!workerReady) return calculateMonthlyProfitLocal(memoizedMasterFilteredData)
    return {}
  }, [profitBreakdowns, workerReady, memoizedMasterFilteredData, masterContractMultiplier])
  
  const memoizedYearProfitBefore = useMemo(() => {
    if (profitBreakdowns['year_before']) return profitBreakdowns['year_before']
    if (!workerReady) return calculateYearlyProfitLocal(masterData)
    return {}
  }, [profitBreakdowns, workerReady, masterData, masterContractMultiplier])
  
  const memoizedYearProfitAfter = useMemo(() => {
    if (profitBreakdowns['year_after']) return profitBreakdowns['year_after']
    if (!workerReady) return calculateYearlyProfitLocal(memoizedMasterFilteredData)
    return {}
  }, [profitBreakdowns, workerReady, memoizedMasterFilteredData, masterContractMultiplier])
  
  const memoizedDateProfitBefore = useMemo(() => {
    if (profitBreakdowns['date_before']) return profitBreakdowns['date_before']
    if (!workerReady) return calculateDateProfitLocal(masterData)
    return {}
  }, [profitBreakdowns, workerReady, masterData, masterContractMultiplier])
  
  const memoizedDateProfitAfter = useMemo(() => {
    if (profitBreakdowns['date_after']) return profitBreakdowns['date_after']
    if (!workerReady) return calculateDateProfitLocal(memoizedMasterFilteredData)
    return {}
  }, [profitBreakdowns, workerReady, memoizedMasterFilteredData, masterContractMultiplier])

  // Calculate current trading day (for filtering timetable) - only update when date changes, not every second
  const [currentTradingDay, setCurrentTradingDay] = useState(() => {
    let tradingDay = new Date()
    const dayOfWeek = tradingDay.getDay()
    if (dayOfWeek === 0) { // Sunday
      tradingDay.setDate(tradingDay.getDate() + 1) // Monday
    } else if (dayOfWeek === 6) { // Saturday
      tradingDay.setDate(tradingDay.getDate() + 2) // Monday
    }
    return tradingDay
  })
  
  // Update trading day only when the date changes (not every second)
  useEffect(() => {
    const updateTradingDay = () => {
      let tradingDay = new Date()
      const dayOfWeek = tradingDay.getDay()
      if (dayOfWeek === 0) { // Sunday
        tradingDay.setDate(tradingDay.getDate() + 1) // Monday
      } else if (dayOfWeek === 6) { // Saturday
        tradingDay.setDate(tradingDay.getDate() + 2) // Monday
      }
      
      // Only update if the date string changed (not the time)
      const newDateStr = tradingDay.toISOString().split('T')[0]
      const currentDateStr = currentTradingDay.toISOString().split('T')[0]
      if (newDateStr !== currentDateStr) {
        setCurrentTradingDay(tradingDay)
      }
    }
    
    // Check every minute instead of every second
    const interval = setInterval(updateTradingDay, 60000)
    updateTradingDay() // Check immediately
    
    return () => clearInterval(interval)
  }, [currentTradingDay])
  
  // Calculate timetable in worker when needed
  useEffect(() => {
    if (workerReady && masterData.length > 0 && workerCalculateTimetable && activeTab === 'timetable') {
      // Calculate timetable when on timetable tab or when filters change
      // Pass current trading day so worker can filter rows
      workerCalculateTimetable(streamFilters, currentTradingDay)
    }
  }, [workerReady, masterData.length, streamFilters, activeTab, workerCalculateTimetable, currentTradingDay])
  
  // Show backend connection state if not ready
  if (backendConnecting) {
    return (
      <div className="min-h-screen bg-black text-gray-100 flex items-center justify-center">
        <div className="text-center">
          <div className="text-xl font-semibold text-blue-400 mb-4">Connecting to backend...</div>
          <div className="text-sm text-gray-400">Waiting for API to be ready</div>
        </div>
      </div>
    )
  }
  
  if (backendConnectionError) {
    return (
      <div className="min-h-screen bg-black text-gray-100 flex items-center justify-center">
        <div className="text-center max-w-md">
          <div className="text-xl font-semibold text-red-400 mb-4">Backend Connection Error</div>
          <div className="text-sm text-gray-400 mb-6">{backendConnectionError}</div>
          <button
            onClick={() => {
              setBackendConnecting(true)
              setBackendConnectionError(null)
              setBackendReady(false)
              // Trigger re-polling by resetting state
              window.location.reload()
            }}
            className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded text-white"
          >
            Retry Connection
          </button>
        </div>
      </div>
    )
  }

  return (
    <div className="min-h-screen bg-black text-gray-100">
      <div className="container mx-auto px-4 py-8">
        {/* Sticky Header with Title and Tabs */}
        <div className="sticky top-0 z-20 bg-black pt-4 pb-2 -mx-4 px-4">
          {/* Tabs */}
          <div className="flex gap-2 mb-6 border-b border-gray-700 overflow-x-auto">
          <button
            onClick={() => setActiveTab('timetable')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'timetable'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Timetable
          </button>
          <button
            onClick={() => setActiveTab('master')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'master'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Masterstream
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
          <button
            onClick={() => setActiveTab('time')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'time'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Time
          </button>
          <button
            onClick={() => setActiveTab('day')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'day'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            DOW
          </button>
          <button
            onClick={() => setActiveTab('dom')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'dom'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            DOM
          </button>
          <button
            onClick={() => setActiveTab('date')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'date'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Day
          </button>
          <button
            onClick={() => setActiveTab('month')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'month'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Month
          </button>
          <button
            onClick={() => setActiveTab('year')}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'year'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Year
          </button>
          </div>
        </div>
        
        {/* Content */}
        {activeTab === 'timetable' ? (
          <div className="space-y-6">
            <div className="bg-gray-900 rounded-lg p-6">
              <div className="flex items-center justify-between mb-6">
                <h2 className="text-xl font-semibold">Trading Timetable</h2>
                <div className="text-center">
                  <div className="text-lg font-semibold text-gray-300">
                    {currentTradingDay.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })}
                  </div>
                </div>
                <div className="text-right">
                  <div className="text-lg font-mono font-semibold text-gray-300">
                    {currentTime.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })}
                  </div>
                  <div className="text-2xl font-mono font-bold text-blue-400">
                    {currentTime.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })}
                  </div>
                  {lastMergeTime && (
                    <div className="mt-2 text-sm font-mono text-gray-400">
                      Last Merge: {lastMergeTime.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })}
                    </div>
                  )}
                </div>
              </div>
              
              {masterLoading ? (
                <div className="text-center py-8">Loading data...</div>
              ) : masterError ? (
                <div className="text-center py-8 text-red-400">
                  <div className="mb-4">{masterError}</div>
                  <button
                    onClick={retryLoad}
                    className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded"
                  >
                    Retry Load
                  </button>
                </div>
              ) : workerTimetableLoading ? (
                <div className="text-center py-8 text-gray-400">Calculating timetable...</div>
              ) : !workerTimetable || workerTimetable.length === 0 ? (
                <div className="text-center py-8 text-gray-400">No timetable data available</div>
              ) : (
                  <div className="overflow-x-auto">
                    <table className="w-full border-collapse">
                      <thead>
                        <tr className="bg-gray-800">
                          <th className="px-4 py-3 text-left font-semibold">Stream</th>
                          <th className="px-4 py-3 text-left font-semibold">Time</th>
                        </tr>
                      </thead>
                      <tbody>
                        {workerTimetable.map((row, idx) => (
                          <tr key={`${row.Stream}-${idx}`} className="border-b border-gray-700 hover:bg-gray-750">
                            <td className="px-4 py-3">{row.Stream}</td>
                            <td className="px-4 py-3 font-mono">{row.Time}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
              )}
            </div>
          </div>
        ) : activeTab === 'time' || activeTab === 'day' || activeTab === 'dom' || activeTab === 'date' || activeTab === 'month' || activeTab === 'year' ? (
          <div className="space-y-6">
            <div className="bg-gray-900 rounded-lg p-6">
              <h2 className="text-xl font-semibold mb-6">
                Profit by {activeTab === 'time' ? 'Time' : activeTab === 'day' ? 'Day of Week (DOW)' : activeTab === 'dom' ? 'Day of Month' : activeTab === 'date' ? 'Day' : activeTab === 'month' ? 'Month' : 'Year'} - All Streams
              </h2>
              {masterLoading ? (
                <div className="text-center py-8">Loading data...</div>
              ) : masterError ? (
                <div className="text-center py-8 text-red-400">
                  <div className="mb-4">{masterError}</div>
                  <button
                    onClick={retryLoad}
                    className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded"
                  >
                    Retry Load
                  </button>
                </div>
              ) : (
                <>
                  {/* After Filters */}
                  <div className="mb-8">
                    <h3 className="text-lg font-semibold mb-4 text-green-400">After Filters</h3>
                    {renderProfitTable(
                      (() => {
                        const data = activeTab === 'time' ? memoizedTimeProfitAfter :
                          activeTab === 'day' ? memoizedDayProfitAfter :
                          activeTab === 'dom' ? memoizedDOMProfitAfter :
                          activeTab === 'date' ? memoizedDateProfitAfter :
                          activeTab === 'month' ? memoizedMonthProfitAfter :
                          memoizedYearProfitAfter
                        return data || {}
                      })(),
                      activeTab
                    )}
                  </div>
                  
                  {/* Before Filters */}
                  <div>
                    <h3 className="text-lg font-semibold mb-4 text-blue-400">Before Filters</h3>
                    {renderProfitTable(
                      (() => {
                        const data = activeTab === 'time' ? memoizedTimeProfitBefore :
                          activeTab === 'day' ? memoizedDayProfitBefore :
                          activeTab === 'dom' ? memoizedDOMProfitBefore :
                          activeTab === 'date' ? memoizedDateProfitBefore :
                          activeTab === 'month' ? memoizedMonthProfitBefore :
                          memoizedYearProfitBefore
                        return data || {}
                      })(),
                      activeTab
                    )}
                  </div>
                </>
              )}
            </div>
          </div>
        ) : activeTab === 'master' ? (
          <div className="space-y-4">
            <div className="bg-gray-900 rounded-lg p-6">
              <div className="flex items-center justify-between mb-4">
                <div>
                  <h2 className="text-xl font-semibold">All Streams Combined</h2>
                  <p className="text-sm text-gray-400 mt-1">
                    Sorted by: Date (newest first), Time (earliest first)
                  </p>
                </div>
                <div className="flex gap-2">
                  <button
                    onClick={() => setShowColumnSelector(!showColumnSelector)}
                    className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-800"
                  >
                    {showColumnSelector ? 'Hide' : 'Show'} Columns
                  </button>
                  <button
                    onClick={() => setShowFilteredDays(!showFilteredDays)}
                    className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-800"
                    title={showFilteredDays ? "Hide filtered days (DOW/DOM excluded)" : "Show filtered days"}
                  >
                    {showFilteredDays ? '✓' : ''} Filtered Days
                  </button>
                  <button
                    onClick={() => loadMasterMatrix(true)}
                    disabled={masterLoading}
                    className={`px-4 py-2 rounded font-medium text-sm ${
                      masterLoading
                        ? 'bg-gray-700 cursor-not-allowed'
                        : 'bg-blue-600 hover:bg-blue-700'
                    }`}
                  >
                    {masterLoading ? 'Loading...' : 'Rebuild Matrix'}
                  </button>
                </div>
              </div>
              
              {renderColumnSelector()}
              
              {/* Stats Toggle for Master */}
              <div className="mb-4">
                <button
                  onClick={() => toggleStats('master')}
                  className="flex items-center justify-between w-full px-4 py-2 bg-gray-800 hover:bg-gray-800 rounded text-left"
                >
                  <span className="font-medium">Statistics (All Streams)</span>
                  <span>{showStats['master'] ? '▼' : '▶'}</span>
                </button>
                {showStats['master'] && renderStats('master')}
              </div>
              
              {/* Contract Multiplier for Master */}
              <div className="mb-4 bg-gray-800 rounded-lg p-4">
                <label className="block text-sm font-medium mb-2">
                  Contract Size Multiplier
                </label>
                <div className="flex items-center gap-3">
                  <input
                    type="number"
                    min="0.1"
                    max="100"
                    step="0.1"
                    value={multiplierInput}
                    onChange={(e) => {
                      const value = e.target.value
                      // Allow free typing - just update the input state
                      if (value === '' || value === '-') {
                        setMultiplierInput(value)
                      } else {
                        const numValue = parseFloat(value)
                        if (!isNaN(numValue)) {
                          setMultiplierInput(value)
                        }
                      }
                    }}
                    onBlur={(e) => {
                      // Validate on blur
                      const value = parseFloat(e.target.value)
                      if (isNaN(value) || value <= 0) {
                        setMultiplierInput(masterContractMultiplier)
                      } else {
                        const clamped = Math.max(0.1, Math.min(100, value))
                        setMultiplierInput(clamped)
                      }
                    }}
                    className="w-24 px-3 py-2 bg-gray-800 border border-gray-700 rounded text-white focus:outline-none focus:border-blue-500"
                  />
                  <button
                    onClick={() => {
                      const value = parseFloat(multiplierInput)
                      if (!isNaN(value) && value > 0) {
                        const clamped = Math.max(0.1, Math.min(100, value))
                        setMasterContractMultiplier(clamped)
                        setMultiplierInput(clamped)
                      } else {
                        // Reset to current value if invalid
                        setMultiplierInput(masterContractMultiplier)
                      }
                    }}
                    className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded text-sm font-medium transition-colors"
                    title="Apply multiplier changes"
                  >
                    Apply
                  </button>
                  <span className="text-sm text-gray-400">
                    (Default: 1 contract. All dollar calculations are multiplied by this value)
                  </span>
                </div>
              </div>
              
              {/* Filters for Master */}
              {renderFilters('master')}
              
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
                    Showing {filteredDataLength || 0} of {filteredDataLength || 0} trades
                  </div>
                  {renderDataTable(masterData, 'master')}
                </>
              )}
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            <div className="bg-gray-900 rounded-lg p-6">
              <div className="flex items-center justify-between mb-4">
                <h2 className="text-xl font-semibold">Stream: {activeTab}</h2>
                <div className="flex gap-2">
                  <button
                    onClick={() => setShowColumnSelector(!showColumnSelector)}
                    className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-800"
                  >
                    {showColumnSelector ? 'Hide' : 'Show'} Columns
                  </button>
                  <button
                    onClick={() => setShowFilteredDays(!showFilteredDays)}
                    className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-800"
                    title={showFilteredDays ? "Hide filtered days (DOW/DOM excluded)" : "Show filtered days"}
                  >
                    {showFilteredDays ? '✓' : ''} Filtered Days
                  </button>
                  <button
                    onClick={() => loadMasterMatrix(true, activeTab)}
                    disabled={masterLoading}
                    className={`px-4 py-2 rounded font-medium text-sm ${
                      masterLoading
                        ? 'bg-gray-700 cursor-not-allowed'
                        : 'bg-green-600 hover:bg-green-700'
                    }`}
                  >
                    {masterLoading ? 'Rebuilding...' : 'Rebuild Stream'}
                  </button>
                </div>
              </div>
              
              {renderColumnSelector()}
              
              {/* Stats Toggle */}
              <div className="mb-4">
                <button
                  onClick={() => toggleStats(activeTab)}
                  className="flex items-center justify-between w-full px-4 py-2 bg-gray-800 hover:bg-gray-800 rounded text-left"
                >
                  <span className="font-medium">Statistics</span>
                  <span>{showStats[activeTab] ? '▼' : '▶'}</span>
                </button>
                {showStats[activeTab] && renderStats(activeTab, memoizedActiveTabStats)}
              </div>
              
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
                    Showing {filteredDataLength || 0} of {filteredDataLength || 0} trades
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
