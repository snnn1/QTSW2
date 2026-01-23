import { useState, useEffect, useRef, useCallback, useMemo, useTransition, useDeferredValue } from 'react'
import { List } from 'react-window'
import './App.css'
import { useMatrixWorker } from './useMatrixWorker'
import { STREAMS, DAYS_OF_WEEK, AVAILABLE_TIMES, ANALYZER_COLUMN_ORDER, DEFAULT_COLUMNS } from './utils/constants'
import { getDefaultFilters, loadAllFilters, saveAllFilters, getStreamFiltersFromStorage } from './utils/filterUtils'
import { getChicagoDateNow, getCMETradingDate, dateToYYYYMMDD, parseYYYYMMDD, formatChicagoTime } from './utils/dateUtils'
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
import { useMatrixController } from './hooks/useMatrixController'
import DataTable from './components/DataTable'
import * as matrixApi from './api/matrixApi'

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
  
  // React 18 optimizations for tab switching
  const [isPending, startTransition] = useTransition()
  const deferredActiveTab = useDeferredValue(activeTab)
  
  // Use deferredActiveTab for data table rendering to align with worker filtering
  const tableTab = deferredActiveTab
  const isSwitchingTab = activeTab !== deferredActiveTab
  
  // Debounce timer ref for tab switching
  const tabDebounceTimerRef = useRef(null)
  
  // Wrapper for setActiveTab that uses startTransition and debouncing
  const handleTabChange = useCallback((newTab) => {
    // Cancel any pending tab change
    if (tabDebounceTimerRef.current) {
      clearTimeout(tabDebounceTimerRef.current)
      tabDebounceTimerRef.current = null
    }
    
    // Debounce tab changes by 100ms to avoid rapid switching
    tabDebounceTimerRef.current = setTimeout(() => {
      startTransition(() => {
        setActiveTab(newTab)
      })
      tabDebounceTimerRef.current = null
    }, 100)
  }, [startTransition])
  
  // Cleanup debounce timer on unmount
  useEffect(() => {
    return () => {
      if (tabDebounceTimerRef.current) {
        clearTimeout(tabDebounceTimerRef.current)
      }
    }
  }, [])
  
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
  
  // Auto-update toggle (persisted in localStorage, default: enabled)
  const [autoUpdateEnabled, setAutoUpdateEnabled] = useState(() => {
    const saved = localStorage.getItem('matrix_auto_update_enabled')
    // Default to true (enabled) if not set yet
    if (saved === null) {
      return true
    }
    return saved === 'true'
  })
  
  // Include filtered executed trades in stats (default: true)
  const [includeFilteredExecuted, setIncludeFilteredExecuted] = useState(() => {
    const saved = localStorage.getItem('matrix_include_filtered_executed')
    return saved !== null ? JSON.parse(saved) : true
  })
  
  // Track previous includeFilteredExecuted to detect changes and force refetch
  const prevIncludeFilteredExecutedRef = useRef(includeFilteredExecuted)
  
  // Contract multiplier for master stream (default 1 contract)
  const [masterContractMultiplier, setMasterContractMultiplier] = useState(() => {
    const saved = localStorage.getItem('matrix_master_contract_multiplier')
    return saved ? parseFloat(saved) || 1 : 1
  })
  
  // Show/Hide filtered days toggle (default: ON/show filtered days)
  const [showFilteredDays, setShowFilteredDays] = useState(() => {
    const saved = localStorage.getItem('matrix_show_filtered_days')
    if (saved !== null) {
      return saved === 'true'
    }
    return true // Default: show filtered days
  })
  
  // Matrix controller hook - handles all matrix orchestration
  const {
    // Backend data state
    masterData,
    masterLoading,
    masterError,
    availableYearsFromAPI,
    lastMergeTime,
    availableColumns,
    setAvailableColumns,
    // File change detection
    matrixFileId,
    matrixFreshness,
    // Matrix generation (invalidation boundary)
    matrixGeneration,
    // Backend stats
    backendStatsFull,
    backendStatsMultiplier,
    masterStatsLoading,
    setBackendStatsFull,
    setBackendStatsMultiplier,
    // Worker state
    workerReady,
    filteredLength,
    workerFilteredIndices,
    workerFilteredRows,
    workerStats,
    statsLoading,
    workerProfitBreakdown,
    workerBreakdownType,
    breakdownLoading: workerBreakdownLoading,
    workerTimetable,
    timetableLoading: workerTimetableLoading,
    workerExecutionTimetable,
    workerError,
    // Worker functions
    workerGetRows,
    calculateProfitBreakdown,
    workerCalculateTimetable,
    // Controller functions
    loadMasterMatrix,
    resequenceMasterMatrix,
    buildMasterMatrix,
    reloadLatestMatrix,
    refetchMasterStats,
    hasLoadedRef
  } = useMatrixController({
    streamFilters,
    masterContractMultiplier,
    includeFilteredExecuted,
    activeTab,
    deferredActiveTab,
    autoUpdateEnabled,
    showFilteredDays
  })
  
  // Save auto-update preference to localStorage
  useEffect(() => {
    localStorage.setItem('matrix_auto_update_enabled', String(autoUpdateEnabled))
  }, [autoUpdateEnabled])
  
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
  
  // Save showFilteredDays to localStorage when it changes
  useEffect(() => {
    localStorage.setItem('matrix_show_filtered_days', String(showFilteredDays))
  }, [showFilteredDays])
  
  // Stats visibility per stream
  // Full-dataset stats from backend (calculated from all rows, not just loaded subset)
  // backendStatsFull is now managed by useMatrixController
  const [backendStreamStats, setBackendStreamStats] = useState({}) // Map of streamId -> stats
  const [backendStreamStatsLoading, setBackendStreamStatsLoading] = useState({}) // Map of streamId -> loading state
  
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
  
  // Show/hide filters per stream (persisted in localStorage)
  const [showFilters, setShowFilters] = useState(() => {
    const saved = localStorage.getItem('matrix_show_filters')
    if (saved) {
      try {
        return JSON.parse(saved)
      } catch {
        return {}
      }
    }
    return {}
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
  
  // Setup columns when data loads (UI-specific logic)
  useEffect(() => {
    if (masterData.length > 0 && availableColumns.length === 0) {
      const cols = Object.keys(masterData[0])
      const hiddenColumns = [
        'global_trade_id', 'filter_reasons', 'onr_high', 'onr_low', 'onr',
        'scf_s1', 'scf_s2', 'prewindow_high_s1', 'prewindow_low_s1', 'prewindow_range_s1',
        'session_high_s1', 'session_low_s1', 'session_range_s1', 'prewindow_high_s2',
        'prewindow_low_s2', 'prewindow_range_s2', 'session_high_s2', 'session_low_s2',
        'session_range_s2', 'onr_q1', 'onr_q2', 'onr_q3', 'onr_bucket', 'entry_time',
        'exit_time', 'entry_price', 'exit_price', 'R', 'pnl', 'rs_value', 'selected_time',
        'time_bucket', 'trade_date', 'day_of_month', 'dow', 'dow_full', 'month',
        'session_index', 'is_two_stream', 'dom_blocked', 'final_allowed', 'SL'
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
      
      setAvailableColumns(displayableCols)
      
      const excludedFromDefault = ['Revised Score', 'Revised Profit ($)']
      setSelectedColumns(prev => {
        const updated = { ...prev }
        let changed = false
        
        const getDefaultColumns = () => {
          return DEFAULT_COLUMNS.filter(col => !excludedFromDefault.includes(col))
        }
        
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
  }, [masterData, availableColumns.length, setAvailableColumns, setSelectedColumns])
  
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
        
        // Try /health endpoint first (more reliable), fallback to /api/matrix/test
        const baseUrl = API_BASE.replace('/api', '')
        const response = await fetch(`${baseUrl}/health`, {
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
    
    // Set timeout for max retry window (30 seconds - increased for slower startup)
    timeoutId = setTimeout(() => {
      if (!isCancelled && !isReady) {
        setBackendConnecting(false)
        const baseUrl = API_BASE.replace('/api', '')
        setBackendConnectionError(`Backend did not respond within 30 seconds. Please check if the backend is running on ${baseUrl}. Make sure you started the backend using RUN_MASTER_MATRIX.bat`)
        if (pollInterval) {
          clearInterval(pollInterval)
          pollInterval = null
        }
      }
    }, 30000)
    
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
  
  // Refetch master stats when includeFilteredExecuted changes
  // This ensures backendStatsFull always reflects the current toggle state with full-history data
  useEffect(() => {
    const prevValue = prevIncludeFilteredExecutedRef.current
    
    // Only refetch if the value actually changed (not on initial mount) and we have data loaded
    if (prevValue !== undefined && prevValue !== includeFilteredExecuted && masterData.length > 0 && refetchMasterStats) {
      console.log(`[Master Stats] Toggle changed: ${prevValue} -> ${includeFilteredExecuted}, refetching stats...`)
      // Clear individual stream stats (they need to be refetched with new setting)
      setBackendStreamStats({})
      setBackendStreamStatsLoading({})
      
      // Refetch master stats with new includeFilteredExecuted setting
      // Pass the new value directly to avoid closure issues
      refetchMasterStats(includeFilteredExecuted)
    }
    
    // Update ref after processing
    prevIncludeFilteredExecutedRef.current = includeFilteredExecuted
  }, [includeFilteredExecuted, masterData.length, refetchMasterStats])
  
  // Refetch master stats when stream filters change
  useEffect(() => {
    const masterFilters = streamFilters['master'] || {}
    const masterIncludeStreams = masterFilters.include_streams || []
    
    // Refetch stats when stream filter changes (if we have data loaded)
    if (masterData.length > 0 && refetchMasterStats) {
      console.log(`[Master Stats] Stream filter changed, refetching stats... (include_streams: ${masterIncludeStreams.length > 0 ? masterIncludeStreams.join(',') : 'all'})`)
      refetchMasterStats(includeFilteredExecuted)
    }
  }, [streamFilters['master']?.include_streams, masterData.length, refetchMasterStats, includeFilteredExecuted])
  
  // Fetch backend stats for individual streams (full dataset)
  // CRITICAL: Always fetch backend stats for individual streams to ensure stats cover ALL data
  useEffect(() => {
    // Only fetch for individual stream tabs (not 'master' or 'timetable')
    if (deferredActiveTab && deferredActiveTab !== 'master' && deferredActiveTab !== 'timetable') {
      const streamId = deferredActiveTab
      
      // Check if includeFilteredExecuted just changed - if so, force refetch
      const includeFilteredJustChanged = prevIncludeFilteredExecutedRef.current !== includeFilteredExecuted
      
      // Check if we already have stats for this stream with the current settings
      // But don't skip if includeFilteredExecuted just changed (cache was cleared)
      if (backendStreamStats[streamId] && !backendStreamStatsLoading[streamId] && !includeFilteredJustChanged) {
        console.log(`[Stream Stats] Already have stats for ${streamId}, skipping fetch`)
        return // Already fetched
      }
      
      // Don't refetch if already loading
      if (backendStreamStatsLoading[streamId]) {
        console.log(`[Stream Stats] Already fetching stats for ${streamId}, waiting...`)
        return
      }
      
      console.log(`[Stream Stats] Fetching backend stats for stream ${streamId} (includeFilteredExecuted=${includeFilteredExecuted})`)
      
      // Set loading state
      setBackendStreamStatsLoading(prev => ({ ...prev, [streamId]: true }))
      
      // Fetch stats from backend
      const fetchStreamStats = async () => {
        try {
          const response = await fetch(`${API_BASE}/matrix/stream-stats`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              stream_id: streamId,
              include_filtered_executed: includeFilteredExecuted,
              contract_multiplier: 1.0 // Individual streams use 1.0 multiplier
            })
          })
          
          if (response.ok) {
            const data = await response.json()
            console.log(`[Stream Stats] Received stats for ${streamId}:`, data.stats ? 'present' : 'missing')
            if (data.stats) {
              setBackendStreamStats(prev => ({
                ...prev,
                [streamId]: data.stats
              }))
              console.log(`[Stream Stats] Updated backendStreamStats for ${streamId}`, {
                totalTrades: data.stats?.sample_counts?.executed_trades_total,
                totalProfit: data.stats?.performance_trade_metrics?.total_profit
              })
            } else {
              console.warn(`[Stream Stats] No stats in response for ${streamId}`)
            }
          } else {
            const errorText = await response.text()
            console.error(`[Stream Stats] Failed to fetch stats for stream ${streamId}:`, errorText)
            // Don't set loading to false on error - let user see the error state
          }
        } catch (error) {
          console.error(`[Stream Stats] Error fetching stats for stream ${streamId}:`, error)
        } finally {
          setBackendStreamStatsLoading(prev => ({ ...prev, [streamId]: false }))
        }
      }
      
      fetchStreamStats()
    }
    
    // NOTE: Master stream stats come from backendStatsFull (from initial data load) or worker stats
    // The /matrix/stream-stats endpoint doesn't support stream_id='master' - it only works for individual streams
    // When includeFilteredExecuted changes, worker stats are recalculated (handled in useMatrixController)
    // backendStatsFull is cleared when includeFilteredExecuted changes, forcing use of worker stats
  }, [deferredActiveTab, includeFilteredExecuted, backendStreamStats, backendStreamStatsLoading]) // Include dependencies to track state
  
  
  // Save filters to localStorage whenever they change
  useEffect(() => {
    saveAllFilters(streamFilters)
  }, [streamFilters])
  
  // Save stats visibility to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_show_stats', JSON.stringify(showStats))
  }, [showStats])
  
  // Save filters visibility to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_show_filters', JSON.stringify(showFilters))
  }, [showFilters])
  
  // Save includeFilteredExecuted to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_include_filtered_executed', JSON.stringify(includeFilteredExecuted))
  }, [includeFilteredExecuted])
  
  // Track the multiplier used for backend stats to detect changes
  // backendStatsMultiplier is now managed by useMatrixController
  const prevMultiplierRef = useRef(masterContractMultiplier)
  
  // Save contract multiplier to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_master_contract_multiplier', masterContractMultiplier.toString())
    // Sync input value when multiplier changes (e.g., from localStorage on mount)
    setMultiplierInput(masterContractMultiplier)
    
    // Check if multiplier actually changed (not just initial mount)
    const multiplierChanged = prevMultiplierRef.current !== masterContractMultiplier
    prevMultiplierRef.current = masterContractMultiplier
    
    // If multiplier changed and we have backend stats, reload stats to get updated values
    if (multiplierChanged && backendStatsFull !== null && backendStatsMultiplier !== null) {
      // Reload stats with new multiplier
          const reloadStats = async () => {
            try {
              setMasterLoading(true)
              const data = await matrixApi.getMatrixData({
                limit: 10000,
                order: 'newest',
                essentialColumnsOnly: true,
                skipCleaning: true,
                contractMultiplier: masterContractMultiplier
              })
              if (data.stats_full) {
                setBackendStatsFull(data.stats_full)
                setBackendStatsMultiplier(masterContractMultiplier)
              }
            } catch (error) {
              console.error('Failed to reload stats with new multiplier:', error.message)
            } finally {
              setMasterLoading(false)
            }
          }
      reloadStats()
    }
  }, [masterContractMultiplier, backendStatsMultiplier, backendStatsFull])
  
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
  
  // calculateStats function removed - using calculateStatsUtil from statsCalculations.js instead
  
  const toggleStats = (streamId) => {
    setShowStats(prev => ({
      ...prev,
      [streamId]: !prev[streamId]
    }))
  }
  
  const toggleFilters = (streamId) => {
    setShowFilters(prev => ({
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
    const masterFilters = streamFilters['master'] || {}
    const masterIncludeStreams = masterFilters.include_streams || []
    const hasMasterStreamFilter = masterIncludeStreams.length > 0
    
    // Single pass through data with all filters applied
    for (const row of data) {
      // Filter by stream first (fastest check)
      if (streamId && streamId !== 'master' && row.Stream !== streamId) {
        continue
      }
      
      // Apply master stream inclusion filter (if master tab and filter is set)
      if (streamId === 'master' && hasMasterStreamFilter) {
        if (!masterIncludeStreams.includes(row.Stream)) {
          continue
        }
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
        avgDrawdownDollars: streamId === 'master' ? formatCurrency(pdm.avg_drawdown_daily || 0) : null,
        avgDrawdownDurationDays: streamId === 'master' ? (pdm.avg_drawdown_duration_days ? pdm.avg_drawdown_duration_days.toFixed(1) : '0.0') : null,
        drawdownEpisodesPerYear: streamId === 'master' ? (pdm.drawdown_episodes_per_year ? pdm.drawdown_episodes_per_year.toFixed(2) : '0.00') : null,
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
      avgDrawdownDollars: streamId === 'master' ? formatCurrency(rawStats.avgDrawdownDollars || 0) : null,
      avgDrawdownDurationDays: streamId === 'master' ? (rawStats.avgDrawdownDurationDays ? parseFloat(rawStats.avgDrawdownDurationDays).toFixed(1) : '0.0') : null,
      drawdownEpisodesPerYear: streamId === 'master' ? (rawStats.drawdownEpisodesPerYear ? parseFloat(rawStats.drawdownEpisodesPerYear).toFixed(2) : '0.00') : null,
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
    // Prefer backend stats (full dataset) for master stream, then worker stats, then fallback
    // CRITICAL: Backend stats always take precedence over precomputed stats (which come from worker)
    let stats = null
    try {
      // Show loading state for master stats when refetching
      if (streamId === 'master' && masterStatsLoading) {
        return (
          <div className="bg-gray-900 rounded-lg p-4 mb-4">
            <p className="text-gray-400 text-sm">Refreshing full-history statistics...</p>
          </div>
        )
      }
      
      // For master stream: ALWAYS prefer backendStatsFull (full-history stats) when available
      // backendStatsFull is now computed with the correct includeFilteredExecuted setting
      if (streamId === 'master' && backendStatsFull && formatWorkerStats) {
        // Use backendStatsFull - it contains full-history stats with correct includeFilteredExecuted setting
        console.log(`[Master Stats] Using backendStatsFull (full-history) with includeFilteredExecuted=${includeFilteredExecuted}`)
        stats = formatWorkerStats(backendStatsFull, streamId)
        
        // Dev-only sanity check: Compare backend vs worker stats when both available
        if (workerReady && workerStats && formatWorkerStats && import.meta.env.DEV) {
          try {
            const workerFormatted = formatWorkerStats(workerStats, streamId)
            const backendProfit = parseFloat(stats?.totalProfitDollars?.replace(/[^0-9.-]/g, '') || 0)
            const workerProfit = parseFloat(workerFormatted?.totalProfitDollars?.replace(/[^0-9.-]/g, '') || 0)
            const backendTrades = stats?.totalTrades || 0
            const workerTrades = workerFormatted?.totalTrades || 0
            
            // Check for significant differences (>5% or >100 trades)
            const profitDiff = Math.abs(backendProfit - workerProfit)
            const profitDiffPercent = backendProfit !== 0 ? (profitDiff / Math.abs(backendProfit)) * 100 : 0
            const tradesDiff = Math.abs(backendTrades - workerTrades)
            
            if (profitDiffPercent > 5 || tradesDiff > 100) {
              console.warn(`[Stats Sanity Check] ${streamId}: Backend (full-history) vs Worker (10k rows) stats differ`, {
                backend: { profit: backendProfit, trades: backendTrades },
                worker: { profit: workerProfit, trades: workerTrades },
                diff: { profit: profitDiff, profitPercent: profitDiffPercent.toFixed(2) + '%', trades: tradesDiff },
                note: 'Using backendStatsFull (full-history) - worker stats are from partial loaded data (10k rows)'
              })
            } else {
              console.log(`[Stats Sanity Check] ${streamId}: Stats aligned`, {
                profitDiff: profitDiff.toFixed(2),
                tradesDiff
              })
            }
          } catch (checkError) {
            // Silently ignore sanity check errors in dev
            console.debug('[Stats Sanity Check] Error:', checkError)
          }
        }
      } else if (streamId !== 'master' && backendStreamStats[streamId] && formatWorkerStats) {
        // For individual streams, ALWAYS prefer backend stats (full dataset) over precomputed worker stats
        const backendStats = backendStreamStats[streamId]
        console.log(`[Stream Stats] Using backend stats for ${streamId} (full dataset) - overriding precomputed worker stats`)
        stats = formatWorkerStats(backendStats, streamId)
        
        // Dev-only sanity check: Compare backend vs worker stats when both available
        if (precomputedStats && import.meta.env.DEV) {
          try {
            const backendProfit = parseFloat(stats?.totalProfitDollars?.replace(/[^0-9.-]/g, '') || 0)
            const workerProfit = parseFloat(precomputedStats?.totalProfitDollars?.replace(/[^0-9.-]/g, '') || 0)
            const backendTrades = stats?.totalTrades || 0
            const workerTrades = precomputedStats?.totalTrades || 0
            
            const profitDiff = Math.abs(backendProfit - workerProfit)
            const profitDiffPercent = backendProfit !== 0 ? (profitDiff / Math.abs(backendProfit)) * 100 : 0
            const tradesDiff = Math.abs(backendTrades - workerTrades)
            
            if (profitDiffPercent > 5 || tradesDiff > 100) {
              console.warn(`[Stats Sanity Check] ${streamId}: Backend vs Worker stats differ`, {
                backend: { profit: backendProfit, trades: backendTrades },
                worker: { profit: workerProfit, trades: workerTrades },
                diff: { profit: profitDiff, profitPercent: profitDiffPercent.toFixed(2) + '%', trades: tradesDiff },
                note: 'Using backend stats (full dataset) - worker stats are from partial loaded data'
              })
            }
          } catch (checkError) {
            console.debug('[Stats Sanity Check] Error:', checkError)
          }
        }
      } else if (precomputedStats && streamId === 'master') {
        // Fallback for master: Use worker stats only if backendStatsFull not available yet
        // This should rarely happen since backendStatsFull is fetched on initial load
        console.warn(`[Master Stats] Falling back to worker stats - backendStatsFull not available yet`)
        stats = precomputedStats
      } else if (precomputedStats && streamId !== 'master') {
        // Use precomputed stats only for non-master streams if no backend stats available
        stats = precomputedStats
        
        // Dev-only sanity check: Compare backend vs worker stats when both available
        if (workerReady && workerStats && formatWorkerStats && import.meta.env.DEV) {
          try {
            const workerFormatted = formatWorkerStats(workerStats, streamId)
            const backendProfit = parseFloat(stats?.totalProfitDollars?.replace(/[^0-9.-]/g, '') || 0)
            const workerProfit = parseFloat(workerFormatted?.totalProfitDollars?.replace(/[^0-9.-]/g, '') || 0)
            const backendTrades = stats?.totalTrades || 0
            const workerTrades = workerFormatted?.totalTrades || 0
            
            // Check for significant differences (>5% or >100 trades)
            const profitDiff = Math.abs(backendProfit - workerProfit)
            const profitDiffPercent = backendProfit !== 0 ? (profitDiff / Math.abs(backendProfit)) * 100 : 0
            const tradesDiff = Math.abs(backendTrades - workerTrades)
            
            if (profitDiffPercent > 5 || tradesDiff > 100) {
              console.warn(`[Stats Sanity Check] ${streamId}: Potential drift detected`, {
                backend: { profit: backendProfit, trades: backendTrades },
                worker: { profit: workerProfit, trades: workerTrades },
                diff: { profit: profitDiff, profitPercent: profitDiffPercent.toFixed(2) + '%', trades: tradesDiff },
                note: 'Worker stats may be from partial dataset (filtered view)'
              })
            } else {
              console.log(`[Stats Sanity Check] ${streamId}: Stats aligned`, {
                profitDiff: profitDiff.toFixed(2),
                tradesDiff
              })
            }
          } catch (checkError) {
            // Silently ignore sanity check errors in dev
            console.debug('[Stats Sanity Check] Error:', checkError)
          }
        }
      } else if (backendStreamStats[streamId] && formatWorkerStats) {
        // Use backend stats (full dataset) for individual streams
        // CRITICAL: Backend stats cover ALL data in the parquet file, not just loaded rows
        const backendStats = backendStreamStats[streamId]
        console.log(`[Stream Stats] Using backend stats for ${streamId} (full dataset)`)
        console.log(`[Stream Stats] Backend stats structure:`, {
          has_sample_counts: !!backendStats.sample_counts,
          has_performance_trade_metrics: !!backendStats.performance_trade_metrics,
          total_profit: backendStats.performance_trade_metrics?.total_profit,
          executed_trades: backendStats.sample_counts?.executed_trades_total
        })
        stats = formatWorkerStats(backendStats, streamId)
        console.log(`[Stream Stats] Formatted stats:`, {
          totalTrades: stats?.totalTrades,
          totalProfitDollars: stats?.totalProfitDollars,
          winRate: stats?.winRate
        })
        
        // Dev-only sanity check: Compare backend vs worker stats when both available
        if (workerReady && workerStats && formatWorkerStats && import.meta.env.DEV) {
          try {
            const workerFormatted = formatWorkerStats(workerStats, streamId)
            const backendProfit = parseFloat(stats?.totalProfitDollars?.replace(/[^0-9.-]/g, '') || 0)
            const workerProfit = parseFloat(workerFormatted?.totalProfitDollars?.replace(/[^0-9.-]/g, '') || 0)
            const backendTrades = stats?.totalTrades || 0
            const workerTrades = workerFormatted?.totalTrades || 0
            
            // Check for significant differences (>5% or >100 trades)
            const profitDiff = Math.abs(backendProfit - workerProfit)
            const profitDiffPercent = backendProfit !== 0 ? (profitDiff / Math.abs(backendProfit)) * 100 : 0
            const tradesDiff = Math.abs(backendTrades - workerTrades)
            
            if (profitDiffPercent > 5 || tradesDiff > 100) {
              console.warn(`[Stats Sanity Check] ${streamId}: Potential drift detected`, {
                backend: { profit: backendProfit, trades: backendTrades },
                worker: { profit: workerProfit, trades: workerTrades },
                diff: { profit: profitDiff, profitPercent: profitDiffPercent.toFixed(2) + '%', trades: tradesDiff },
                note: 'Worker stats may be from partial dataset (filtered view)'
              })
            } else {
              console.log(`[Stats Sanity Check] ${streamId}: Stats aligned`, {
                profitDiff: profitDiff.toFixed(2),
                tradesDiff
              })
            }
          } catch (checkError) {
            // Silently ignore sanity check errors in dev
            console.debug('[Stats Sanity Check] Error:', checkError)
          }
        }
      } else if (backendStreamStatsLoading[streamId]) {
        // Backend stats are being fetched - show loading instead of wrong worker stats
        return (
          <div className="bg-gray-900 rounded-lg p-4 mb-4">
            <p className="text-gray-400 text-sm">Loading full-dataset statistics...</p>
          </div>
        )
      } else if (workerReady && workerStats && formatWorkerStats) {
        // Use worker stats (computed from loaded rows - filtered view) ONLY if backend stats aren't available
        // For individual streams, backend stats should always be used - worker stats are incomplete
        if (streamId !== 'master') {
          console.warn(`[Stream Stats] WARNING: Using worker stats for ${streamId} - backend stats not available. Stats may be incomplete (only showing ${workerStats?.sample_counts?.executed_trades_total || 'unknown'} trades from loaded data).`)
          console.warn(`[Stream Stats] Backend stats state:`, {
            hasBackendStats: !!backendStreamStats[streamId],
            isLoading: !!backendStreamStatsLoading[streamId],
            streamId
          })
        }
        stats = formatWorkerStats(workerStats, streamId)
      } else if (!workerReady) {
        // Only fallback to main thread if worker not ready yet
        let filtered = getFilteredData(masterData, streamId)
        // Apply year filter if specified
        const filters = getStreamFilters(streamId)
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
        const contractMultiplier = streamId === 'master' ? masterContractMultiplier : 1
        stats = calculateStatsUtil(filtered, streamId, contractMultiplier)
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
        let filtered = getFilteredData(masterData, streamId)
        // Apply year filter if specified
        const filters = getStreamFilters(streamId)
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
        const contractMultiplier = streamId === 'master' ? masterContractMultiplier : 1
        stats = calculateStatsUtil(filtered, streamId, contractMultiplier)
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
                onClick={() => {
                  const newValue = !includeFilteredExecuted
                  console.log(`[Toggle] Button clicked: ${includeFilteredExecuted} -> ${newValue}`)
                  setIncludeFilteredExecuted(newValue)
                }}
                role="button"
                tabIndex={0}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault()
                    const newValue = !includeFilteredExecuted
                    console.log(`[Toggle] Keyboard activated: ${includeFilteredExecuted} -> ${newValue}`)
                    setIncludeFilteredExecuted(newValue)
                  }
                }}
              >
                <input
                  type="checkbox"
                  className="sr-only"
                  checked={includeFilteredExecuted}
                  onChange={(e) => {
                    console.log(`[Toggle] Checkbox changed: ${includeFilteredExecuted} -> ${e.target.checked}`)
                    setIncludeFilteredExecuted(e.target.checked)
                  }}
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
            <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-4">
              <div>
                <div className="text-xs text-gray-400 mb-1">Max Drawdown ($)</div>
                <div className="text-lg font-semibold text-red-400">{stats.maxDrawdownDollars}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Time-to-Recovery (Days)</div>
                <div className="text-lg font-semibold">{stats.timeToRecoveryDays ?? 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Average Drawdown ($)</div>
                <div className="text-lg font-semibold text-red-400">{stats.avgDrawdownDollars || 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Avg Drawdown Duration (Days)</div>
                <div className="text-lg font-semibold">{stats.avgDrawdownDurationDays ?? 'N/A'}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Drawdown Frequency (per Year)</div>
                <div className="text-lg font-semibold">{stats.drawdownEpisodesPerYear ?? 'N/A'}</div>
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
                      (filters.include_years && filters.include_years.length > 0) ||
                      (streamId === 'master' && filters.include_streams && filters.include_streams.length > 0)
    
    return (
      <div className="bg-gray-800 rounded-lg p-4 mb-4">
        <div className="flex items-center justify-between mb-3">
          <h4 className="font-medium text-sm">Filters for {streamId}</h4>
          {hasFilters && (
            <span className="text-xs bg-blue-600 px-2 py-1 rounded">Active</span>
          )}
        </div>
        
        <div className={`grid grid-cols-1 md:grid-cols-${streamId === 'master' ? '5' : '4'} gap-6`}>
          {/* Stream Filters - Only for Master */}
          {streamId === 'master' && (
            <div className="space-y-2">
              <div className="flex items-center justify-between mb-2">
                <label className="block text-xs font-medium text-gray-400">Include Streams</label>
                {filters.include_streams && filters.include_streams.length > 0 && (
                  <button
                    type="button"
                    onClick={(e) => {
                      e.preventDefault()
                      e.stopPropagation()
                      setStreamFilters(prev => {
                        const updated = { ...prev }
                        if (updated[streamId]) {
                          updated[streamId] = {
                            ...updated[streamId],
                            include_streams: []
                          }
                        } else {
                          updated[streamId] = {
                            exclude_days_of_week: [],
                            exclude_days_of_month: [],
                            exclude_times: [],
                            include_years: [],
                            include_streams: []
                          }
                        }
                        return updated
                      })
                    }}
                    className="text-xs text-blue-400 hover:text-blue-300 underline"
                    title="Show all streams"
                  >
                    Show All
                  </button>
                )}
              </div>
              <div className="flex flex-wrap gap-1 max-h-32 overflow-y-auto">
                {STREAMS.map(stream => {
                  const isSelected = filters.include_streams && filters.include_streams.includes(stream)
                  return (
                    <button
                      key={stream}
                      type="button"
                      onClick={(e) => {
                        e.preventDefault()
                        e.stopPropagation()
                        const current = filters.include_streams || []
                        const newStreams = isSelected
                          ? current.filter(s => s !== stream)
                          : [...current, stream]
                        setStreamFilters(prev => {
                          const updated = { ...prev }
                          if (!updated[streamId]) {
                            updated[streamId] = getDefaultFilters()
                          }
                          updated[streamId] = {
                            ...updated[streamId],
                            include_streams: newStreams
                          }
                          return updated
                        })
                      }}
                      className={`px-2 py-1 text-xs rounded cursor-pointer ${
                        isSelected
                          ? 'bg-green-600 text-white'
                          : 'bg-gray-700 hover:bg-gray-600'
                      }`}
                    >
                      {stream}
                    </button>
                  )
                })}
              </div>
              {filters.include_streams && filters.include_streams.length > 0 && (
                <div className="mt-2 text-xs text-gray-400">
                  Selected: {filters.include_streams.sort().join(', ')}
                  {filters.include_streams.length === 0 && ' (All streams)'}
                </div>
              )}
              {(!filters.include_streams || filters.include_streams.length === 0) && (
                <div className="mt-2 text-xs text-gray-500">
                  All streams included
                </div>
              )}
            </div>
          )}
          
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
            <div className="flex items-center justify-between mb-2">
              <label className="block text-xs font-medium text-gray-400">Exclude DOM</label>
              {filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0 && (
                <button
                  type="button"
                  onClick={(e) => {
                    e.preventDefault()
                    e.stopPropagation()
                    // Clear all day of month filters
                    setStreamFilters(prev => {
                      const updated = { ...prev }
                      if (updated[streamId]) {
                        updated[streamId] = {
                          ...updated[streamId],
                          exclude_days_of_month: []
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
                  title="Clear all excluded days"
                >
                  Clear All
                </button>
              )}
            </div>
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
                className="absolute top-full left-0 right-0 w-full mt-1 bg-gray-800 border border-gray-600 rounded shadow-xl max-h-48 overflow-y-auto z-20"
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
  
  // Row component removed - using TableRow from DataTable component instead
  
  // State to track loaded rows for incremental loading
  const [loadedRows, setLoadedRows] = useState([])
  const [loadingMoreRows, setLoadingMoreRows] = useState(false)
  
  // State to limit Day tab to 200 days by default for performance
  const [showAllDays, setShowAllDays] = useState(false)
  
  // Update loaded rows when worker filtered rows change
  // Reset loadedRows when stream/activeTab changes to prevent stale data
  const prevActiveTabRef = useRef(deferredActiveTab)
  // Track which tab the current workerFilteredRows belongs to
  const workerFilteredRowsTabRef = useRef(null)
  
  // Clear loadedRows immediately when activeTab changes (not deferred)
  // This prevents stale data from being displayed while deferredActiveTab catches up
  const prevActiveTabImmediateRef = useRef(activeTab)
  useEffect(() => {
    if (prevActiveTabImmediateRef.current !== activeTab) {
      setLoadedRows([])
      workerFilteredRowsTabRef.current = null
      prevActiveTabImmediateRef.current = activeTab
    }
  }, [activeTab])
  
  // Clear loadedRows when showFilteredDays changes to force reload with new filter
  const prevShowFilteredDaysRef = useRef(showFilteredDays)
  useEffect(() => {
    if (prevShowFilteredDaysRef.current !== showFilteredDays) {
      setLoadedRows([])
      workerFilteredRowsTabRef.current = null
      prevShowFilteredDaysRef.current = showFilteredDays
    }
  }, [showFilteredDays])
  
  useEffect(() => {
    // Reset loadedRows when deferredActiveTab changes (stream switch)
    if (prevActiveTabRef.current !== deferredActiveTab) {
      setLoadedRows([])
      prevActiveTabRef.current = deferredActiveTab
      // Clear the tracked tab when switching tabs to prevent using stale data
      workerFilteredRowsTabRef.current = null
    }
    
    // Only use workerFilteredRows if it belongs to the current tab
    if (workerReady && workerFilteredRows && workerFilteredRows.length > 0) {
      // If workerFilteredRowsTabRef is null or matches current tab, accept the rows
      // This handles both initial load and re-filtering (e.g., when showFilteredDays changes)
      if (workerFilteredRowsTabRef.current === null || workerFilteredRowsTabRef.current === deferredActiveTab) {
        // Set loadedRows when we get new filtered rows (worker re-filtered)
        setLoadedRows(workerFilteredRows)
        // Track which tab these rows belong to
        workerFilteredRowsTabRef.current = deferredActiveTab
      }
      // If workerFilteredRows doesn't match current tab, ignore it (it's stale)
    }
  }, [workerReady, workerFilteredRows, deferredActiveTab])
  
  // Auto-load ALL rows when filtered indices change (no limit)
  // Track previous showFilteredDays to detect changes and reset loading
  const prevShowFilteredDaysForLoadingRef = useRef(showFilteredDays)
  useEffect(() => {
    // If showFilteredDays changed, reset loadedRows to force reload with new filter
    if (prevShowFilteredDaysForLoadingRef.current !== showFilteredDays) {
      setLoadedRows([])
      prevShowFilteredDaysForLoadingRef.current = showFilteredDays
      return // Wait for new filtered indices before loading
    }
    
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
              if (!newRows || newRows.length === 0) {
                // No rows returned, move to next chunk
                loadedChunks++
                setTimeout(() => loadChunk(chunkIndex + 1), 10)
                return
              }
              
              setLoadedRows(prev => {
                const existingLength = prev.length
                // Check if we already have enough rows (avoid duplicates)
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
  }, [workerReady, workerFilteredIndices && workerFilteredIndices.length, workerGetRows, loadedRows.length, loadingMoreRows, showFilteredDays])
  
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
  
  // renderDataTable function removed - using DataTable component instead
  
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
  const renderProfitTable = (data, periodType, limitDays = null) => {
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
    
    // Limit to most recent N days for date tab if limitDays is specified
    let displayPeriods = sortedPeriods
    if (periodType === 'date' && limitDays !== null && limitDays > 0 && sortedPeriods.length > limitDays) {
      displayPeriods = sortedPeriods.slice(0, limitDays)
    }
    
    if (displayPeriods.length === 0) {
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
              {displayPeriods.map(period => {
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
                  const streamTotal = displayPeriods.reduce((sum, period) => {
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
                  displayPeriods.reduce((sum, period) => {
                    const periodData = data[period]
                    if (!periodData || typeof periodData !== 'object') return sum
                    return sum + Object.values(periodData).reduce((s, v) => s + (v || 0), 0)
                  }, 0) > 0 ? 'text-green-400' : 'text-red-400'
                }`}>
                  {formatCurrency(
                    displayPeriods.reduce((sum, period) => {
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
              {displayPeriods.map(period => {
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
                  const streamTotal = displayPeriods.reduce((sum, period) => {
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
                  displayPeriods.reduce((sum, period) => {
                    const periodData = data[period]
                    if (!periodData || typeof periodData !== 'object') return sum
                    return sum + Object.values(periodData).reduce((s, v) => s + (v || 0), 0)
                  }, 0) > 0 ? 'text-green-400' : 'text-red-400'
                }`}>
                  {formatCurrency(
                    displayPeriods.reduce((sum, period) => {
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
  // Only use filteredLength if it matches the current tableTab to prevent stale counts
  const filteredDataLength = (workerFilteredRowsTabRef.current === tableTab ? filteredLength : null) || 0
  
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
  
  // Track previous breakdown tab to clear old breakdown types when switching
  const prevBreakdownTabRef = useRef(null)
  
  // Calculate profit breakdowns - use backend for full-dataset breakdowns (DOW/DOM/TIME), worker for filtered view (date/month/year)
  // Use activeTab (not deferredActiveTab) for breakdowns since they're less performance-critical
  // and we want them to trigger immediately when user clicks a breakdown tab
  useEffect(() => {
    const activeBreakdownTabs = ['time', 'day', 'dom', 'date', 'month', 'year']
    
    // Only calculate if we're on a breakdown tab
    if (!activeBreakdownTabs.includes(activeTab)) {
      prevBreakdownTabRef.current = null
      return
    }
    
    prevBreakdownTabRef.current = activeTab
    const streamId = 'master' // Breakdowns always use master stream
    
    // For DOW, DOM, and TIME tabs, fetch from backend (full dataset)
    // For other tabs (date, month, year), use worker (filtered view is fine)
    if (activeTab === 'day' || activeTab === 'dom' || activeTab === 'time') {
      // Fetch full-dataset breakdowns from backend
      const fetchBreakdownFromBackend = async (useFiltered) => {
        try {
          let breakdownType
          if (activeTab === 'day') {
            breakdownType = 'dow'
          } else if (activeTab === 'dom') {
            breakdownType = 'dom'
          } else if (activeTab === 'time') {
            breakdownType = 'time'
          }
          
          // Get master stream inclusion filter
          const masterFilters = streamFilters['master'] || {}
          const masterIncludeStreams = masterFilters.include_streams || []
          const streamIncludeParam = masterIncludeStreams.length > 0 ? masterIncludeStreams : null
          
          console.log(`[Breakdown] Fetching ${breakdownType} breakdown from backend (useFiltered=${useFiltered}, streamInclude=${streamIncludeParam})`)
          const data = await matrixApi.getProfitBreakdown({
            breakdownType,
            streamFilters,
            useFiltered,
            contractMultiplier: masterContractMultiplier,
            streamInclude: streamIncludeParam
          })
          
          if (data && data.breakdown) {
            const suffix = useFiltered ? 'after' : 'before'
            // Verify breakdown format - should be {time: {stream: profit}} for time tab
            if (activeTab === 'time') {
              const sampleKey = Object.keys(data.breakdown)[0]
              if (sampleKey && typeof data.breakdown[sampleKey] === 'object') {
                const sampleValue = data.breakdown[sampleKey]
                const hasStreamKeys = Object.keys(sampleValue).some(key => !['profit', 'trades'].includes(key))
                if (!hasStreamKeys) {
                  console.error(`[Breakdown] ERROR: Time breakdown has wrong format. Expected {time: {stream: profit}}, got:`, sampleValue)
                } else {
                  console.log(`[Breakdown] Time breakdown format verified: ${sampleKey} has streams:`, Object.keys(sampleValue))
                }
              }
            }
            setProfitBreakdowns(prev => ({
              ...prev,
              [`${activeTab}_${suffix}`]: data.breakdown
            }))
            console.log(`[Breakdown] Updated ${activeTab}_${suffix} breakdown from backend (${Object.keys(data.breakdown).length} entries)`)
          } else {
            console.warn(`[Breakdown] No breakdown data in response for ${activeTab}`)
          }
        } catch (error) {
          console.error(`Failed to fetch ${activeTab} breakdown from backend:`, error)
        }
      }
      
      // Fetch both before and after filters
      fetchBreakdownFromBackend(false) // before filters
      fetchBreakdownFromBackend(true)  // after filters
    } else if (workerReady && masterData.length > 0 && calculateProfitBreakdown) {
      // For other tabs (time, date, month, year), use worker
      // For date tab, calculate "after filters" first for better perceived performance
      // Then defer "before filters" slightly to prioritize showing filtered results
      if (activeTab === 'date') {
        // Calculate "after filters" first (most commonly viewed)
        calculateProfitBreakdown(streamFilters, streamId, masterContractMultiplier, `${activeTab}_after`, true)
        // Defer "before filters" by a small delay to improve perceived responsiveness
        setTimeout(() => {
          calculateProfitBreakdown(streamFilters, streamId, masterContractMultiplier, `${activeTab}_before`, false)
        }, 100)
      } else {
        // For other tabs, calculate both immediately
        calculateProfitBreakdown(streamFilters, streamId, masterContractMultiplier, `${activeTab}_before`, false)
        calculateProfitBreakdown(streamFilters, streamId, masterContractMultiplier, `${activeTab}_after`, true)
      }
    }
  }, [workerReady, masterData.length, masterContractMultiplier, streamFilters, activeTab, calculateProfitBreakdown, streamFilters['master']?.include_streams])
  
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
  // CRITICAL FIX: Use CME trading date with 17:00 Chicago rollover rule
  // This matches the timetable generation logic (timetable_current.json)
  const [currentTradingDay, setCurrentTradingDay] = useState(() => {
    // Get CME trading date (applies 17:00 rollover rule)
    const cmeTradingDateStr = getCMETradingDate()
    let tradingDay = parseYYYYMMDD(cmeTradingDateStr)
    const dayOfWeek = tradingDay.getDay()
    if (dayOfWeek === 0) { // Sunday
      tradingDay.setDate(tradingDay.getDate() + 1) // Monday
    } else if (dayOfWeek === 6) { // Saturday
      tradingDay.setDate(tradingDay.getDate() + 2) // Monday
    }
    return tradingDay
  })
  
  // Update trading day only when the date changes (not every second)
  // CRITICAL FIX: Use CME trading date with 17:00 Chicago rollover rule
  useEffect(() => {
    const updateTradingDay = () => {
      // Get CME trading date (applies 17:00 rollover rule)
      const cmeTradingDateStr = getCMETradingDate()
      let tradingDay = parseYYYYMMDD(cmeTradingDateStr)
      const dayOfWeek = tradingDay.getDay()
      if (dayOfWeek === 0) { // Sunday
        tradingDay.setDate(tradingDay.getDate() + 1) // Monday
      } else if (dayOfWeek === 6) { // Saturday
        tradingDay.setDate(tradingDay.getDate() + 2) // Monday
      }
      
      // Only update if the date string changed (not the time)
      // CRITICAL FIX: Use dateToYYYYMMDD instead of toISOString()
      const newDateStr = dateToYYYYMMDD(tradingDay)
      const currentDateStr = dateToYYYYMMDD(currentTradingDay)
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
  // If displayed date doesn't exist in master matrix, read from backend-generated timetable_current.json
  // IMPORTANT: Check matrix freshness before calculating timetable
  useEffect(() => {
    if (deferredActiveTab !== 'timetable' || !currentTradingDay) return
    
    // Check matrix freshness - refuse to calculate timetable if matrix is stale
    const checkFreshnessAndCalculate = async () => {
      if (matrixFreshness && matrixFreshness.is_stale) {
        console.warn('[Timetable] Matrix is stale, reloading before calculating timetable')
        // Reload latest matrix first
        try {
          await reloadLatestMatrix()
          // After reload, the useEffect will trigger again with fresh data
          return
        } catch (error) {
          console.error('[Timetable] Failed to reload matrix:', error)
          // Continue anyway - better than blocking
        }
      }
      
      // Check if displayed date exists in master matrix
      // CRITICAL FIX: Use dateToYYYYMMDD instead of toISOString()
      const displayedDateStr = dateToYYYYMMDD(currentTradingDay)
      const dateExistsInMatrix = masterData.some(row => {
        const rowDate = row.Date || row.trade_date
        if (!rowDate) return false
        const rowDateStr = rowDate instanceof Date 
          ? dateToYYYYMMDD(rowDate)
          : rowDate.split('T')[0]
        return rowDateStr === displayedDateStr
      })
      
      // If date doesn't exist in matrix, try to read from backend-generated timetable_current.json
      // This file is generated with RS calculation and contains all streams
      if (!dateExistsInMatrix && workerReady) {
        console.log('[Timetable] Displayed date not in matrix, reading from timetable_current.json:', displayedDateStr)
        try {
          const timetable = await matrixApi.getCurrentTimetable()
          if (timetable && timetable.trading_date === displayedDateStr && timetable.streams) {
            // Convert backend format to worker format
            const timetableRows = timetable.streams
              .map(s => ({
                Stream: s.stream,
                Time: s.slot_time || s.decision_time || '',
                Enabled: s.enabled,
                BlockReason: s.block_reason || null
              }))
            
            // Update worker timetable state directly (bypass worker calculation)
            // We need to access the setTimetable function from useMatrixWorker
            // For now, trigger backend generation which will update the file
            const generateResponse = await fetch(`http://localhost:8000/api/timetable/generate`, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ date: displayedDateStr })
            })
            if (generateResponse.ok) {
              // After generation, trigger worker calculation which will now use the updated file
              // Or better: read the file again and update state
              const updatedTimetable = await matrixApi.getCurrentTimetable()
              if (updatedTimetable && updatedTimetable.streams) {
                const updatedRows = updatedTimetable.streams
                  .map(s => ({
                    Stream: s.stream,
                    Time: s.slot_time || s.decision_time || '',
                    Enabled: s.enabled,
                    BlockReason: s.block_reason || null
                  }))
                // Note: We can't directly set worker state here, so we'll let the worker
                // recalculate after the file is updated. The worker should read from the file.
                console.log('[Timetable] Generated via backend, triggering worker recalculation')
                if (workerCalculateTimetable) {
                  // Small delay to ensure file is written
                  setTimeout(() => {
                    workerCalculateTimetable(streamFilters, currentTradingDay)
                  }, 100)
                }
              }
            }
          } else if (timetable && timetable.trading_date !== displayedDateStr) {
            // File exists but for different date - generate for displayed date
            console.log('[Timetable] File exists for different date, generating for:', displayedDateStr)
            await fetch(`http://localhost:8000/api/timetable/generate`, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ date: displayedDateStr })
            })
            if (workerCalculateTimetable) {
              setTimeout(() => {
                workerCalculateTimetable(streamFilters, currentTradingDay)
              }, 100)
            }
          }
        } catch (error) {
          console.warn('[Timetable] Backend read failed, using worker:', error)
          // Fall through to worker calculation
          if (workerReady && masterData.length > 0 && workerCalculateTimetable) {
            workerCalculateTimetable(streamFilters, currentTradingDay)
          }
        }
        return
      }
      
      // Normal path: Use worker calculation from master matrix
      if (workerReady && masterData.length > 0 && workerCalculateTimetable) {
        workerCalculateTimetable(streamFilters, currentTradingDay)
      }
    }
    
    checkFreshnessAndCalculate()
  }, [workerReady, masterData.length, streamFilters, deferredActiveTab, workerCalculateTimetable, currentTradingDay, masterData, matrixFreshness, reloadLatestMatrix])
  
  // Save execution timetable whenever UI timetable updates
  // This ensures timetable_current.json matches exactly what the UI shows
  useEffect(() => {
    if (workerExecutionTimetable && workerExecutionTimetable.streams && workerExecutionTimetable.streams.length > 0) {
      const saveExecutionTimetable = async () => {
        try {
          await matrixApi.saveExecutionTimetable({
            tradingDate: workerExecutionTimetable.trading_date,
            streams: workerExecutionTimetable.streams
          })
          console.log('Execution timetable saved successfully')
        } catch (error) {
          console.error('Error saving execution timetable:', error.message)
        }
      }
      saveExecutionTimetable()
    }
  }, [workerExecutionTimetable])
  
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
        {/* Freshness Warning Banner */}
        {matrixFreshness && matrixFreshness.is_stale && (
          <div className="mb-4 p-4 bg-yellow-900 border border-yellow-700 rounded-lg">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                <span className="text-yellow-400 font-semibold">⚠️ Stale Data Warning</span>
                <span className="text-yellow-300 text-sm">
                  {matrixFreshness.staleness_message || 'Analyzer has newer data than Matrix — UI may be stale.'}
                </span>
              </div>
              <button
                onClick={() => reloadLatestMatrix()}
                disabled={masterLoading}
                className="px-4 py-2 bg-yellow-600 hover:bg-yellow-700 rounded text-white text-sm font-medium disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {masterLoading ? 'Refreshing...' : 'Refresh Data'}
              </button>
            </div>
          </div>
        )}
        
        {/* Sticky Header with Title and Tabs */}
        <div className="sticky top-0 z-20 bg-black pt-4 pb-2 -mx-4 px-4">
          {/* Tabs */}
          <div className="flex gap-2 mb-6 border-b border-gray-700 overflow-x-auto">
          <button
            onClick={() => {
              handleTabChange('timetable');
            }}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'timetable'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Timetable
          </button>
          <button
            onClick={() => {
              handleTabChange('master');
            }}
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
              onClick={() => {
                handleTabChange(stream);
              }}
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
            onClick={() => {
              handleTabChange('time');
            }}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'time'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Time
          </button>
          <button
            onClick={() => {
              handleTabChange('day');
            }}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'day'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            DOW
          </button>
          <button
            onClick={() => {
              handleTabChange('dom');
            }}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'dom'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            DOM
          </button>
          <button
            onClick={() => {
              handleTabChange('date');
            }}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'date'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Day
          </button>
          <button
            onClick={() => {
              handleTabChange('month');
            }}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'month'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Month
          </button>
          <button
            onClick={() => {
              handleTabChange('year');
            }}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'year'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Year
          </button>
          <button
            onClick={() => {
              handleTabChange('stats');
            }}
            className={`px-4 py-2 font-medium whitespace-nowrap ${
              activeTab === 'stats'
                ? 'border-b-2 border-blue-500 text-blue-400'
                : 'text-gray-400 hover:text-gray-300'
            }`}
          >
            Stats
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
                  <div className="flex gap-6 items-start">
                    {/* UTC Time */}
                    <div>
                      <div className="text-xs font-semibold text-gray-500 uppercase mb-1">UTC</div>
                      <div className="text-sm font-mono font-semibold text-gray-400">
                        {currentTime.toLocaleDateString('en-US', { 
                          weekday: 'long', 
                          year: 'numeric', 
                          month: 'long', 
                          day: 'numeric',
                          timeZone: 'UTC'
                        })}
                      </div>
                      <div className="text-xl font-mono font-bold text-blue-400">
                        {currentTime.toLocaleTimeString('en-US', { 
                          hour12: false, 
                          hour: '2-digit', 
                          minute: '2-digit', 
                          second: '2-digit',
                          timeZone: 'UTC'
                        })}
                      </div>
                    </div>
                    {/* Chicago Time */}
                    <div>
                      <div className="text-xs font-semibold text-gray-500 uppercase mb-1">Chicago</div>
                      <div className="text-sm font-mono font-semibold text-gray-400">
                        {formatChicagoTime(currentTime, {
                          weekday: 'long',
                          year: 'numeric',
                          month: 'long',
                          day: 'numeric'
                        })}
                      </div>
                      <div className="text-xl font-mono font-bold text-green-400">
                        {formatChicagoTime(currentTime, {
                          hour12: false,
                          hour: '2-digit',
                          minute: '2-digit',
                          second: '2-digit'
                        })}
                      </div>
                    </div>
                  </div>
                  {lastMergeTime && (
                    <div className="mt-2 text-sm font-mono text-gray-400">
                      Matrix Built: {lastMergeTime.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })} ({lastMergeTime.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })})
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
                          <th className="px-4 py-3 text-left font-semibold">Status</th>
                        </tr>
                      </thead>
                      <tbody>
                        {workerTimetable
                          .map((row, idx) => {
                            const isDisabled = row.Enabled === false
                            return (
                              <tr 
                                key={`${row.Stream}-${idx}`} 
                                className={`border-b border-gray-700 hover:bg-gray-750 ${isDisabled ? 'opacity-50 bg-gray-800' : ''}`}
                                title={isDisabled && row.BlockReason ? `Blocked: ${row.BlockReason}` : ''}
                              >
                                <td className="px-4 py-3">{row.Stream}</td>
                                <td className="px-4 py-3 font-mono">{row.Time}</td>
                                <td className="px-4 py-3">
                                  {isDisabled ? (
                                    <span className="text-red-400 text-sm" title={row.BlockReason || 'Blocked'}>
                                      Blocked {row.BlockReason && `(${row.BlockReason})`}
                                    </span>
                                  ) : (
                                    <span className="text-green-400 text-sm">Enabled</span>
                                  )}
                                </td>
                              </tr>
                            )
                          })}
                      </tbody>
                    </table>
                  </div>
              )}
            </div>
          </div>
        ) : activeTab === 'stats' ? (
          <div className="space-y-6">
            <div className="bg-gray-900 rounded-lg p-6">
              <h2 className="text-xl font-semibold mb-6">Statistics & Analysis</h2>
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
                <WorstDaysTable contractMultiplier={masterContractMultiplier} />
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
                    <div className="flex items-center justify-between mb-4">
                      <h3 className="text-lg font-semibold text-green-400">After Filters</h3>
                      {activeTab === 'date' && (
                        <button
                          onClick={() => setShowAllDays(!showAllDays)}
                          className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-700 text-gray-300"
                        >
                          {showAllDays ? 'Show Recent 200 Days' : 'Show All Days'}
                        </button>
                      )}
                    </div>
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
                      activeTab,
                      activeTab === 'date' && !showAllDays ? 200 : null
                    )}
                  </div>
                  
                  {/* Before Filters */}
                  <div>
                    <div className="flex items-center justify-between mb-4">
                      <h3 className="text-lg font-semibold text-blue-400">Before Filters</h3>
                      {activeTab === 'date' && (
                        <button
                          onClick={() => setShowAllDays(!showAllDays)}
                          className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-700 text-gray-300"
                        >
                          {showAllDays ? 'Show Recent 200 Days' : 'Show All Days'}
                        </button>
                      )}
                    </div>
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
                      activeTab,
                      activeTab === 'date' && !showAllDays ? 200 : null
                    )}
                  </div>
                </>
              )}
            </div>
          </div>
        ) : tableTab === 'master' ? (
          <div className="space-y-4">
            <div className="bg-gray-900 rounded-lg p-6">
              <div className="flex items-center justify-between mb-4">
                <div>
                  <h2 className="text-xl font-semibold">All Streams Combined</h2>
                  <p className="text-sm text-gray-400 mt-1">
                    Sorted by: Date (newest first), Time (earliest first)
                  </p>
                  {lastMergeTime && (
                    <p className="text-xs text-gray-500 mt-1 font-mono">
                      Matrix Built: {lastMergeTime.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })} ({lastMergeTime.toLocaleDateString('en-US', { month: 'short', day: 'numeric' })})
                    </p>
                  )}
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
                    onClick={() => resequenceMasterMatrix(40)}
                    disabled={masterLoading}
                    className={`px-4 py-2 rounded font-medium text-sm ${
                      masterLoading
                        ? 'bg-gray-700 cursor-not-allowed'
                        : 'bg-green-600 hover:bg-green-700'
                    } text-white`}
                    title="Rolling resequence (default): Removes last 40 trading days, restores sequencer state from checkpoint, and resequences from analyzer output. No stale rows preserved in window."
                  >
                    {masterLoading ? 'Resequencing...' : 'Resequence Last 40 Days'}
                  </button>
                  <button
                    onClick={() => buildMasterMatrix()}
                    disabled={masterLoading}
                    className={`px-4 py-2 rounded font-medium text-sm ${
                      masterLoading
                        ? 'bg-gray-700 cursor-not-allowed'
                        : 'bg-red-600 hover:bg-red-700'
                    } text-white`}
                    title="Full rebuild (destructive): Rebuilds entire matrix from scratch. Use only for recovery or when resequence fails."
                  >
                    {masterLoading ? 'Rebuilding...' : 'Full Rebuild'}
                  </button>
                  <button
                    onClick={() => reloadLatestMatrix()}
                    disabled={masterLoading}
                    className={`px-4 py-2 rounded font-medium text-sm ${
                      masterLoading
                        ? 'bg-gray-700 cursor-not-allowed'
                        : 'bg-blue-600 hover:bg-blue-700'
                    } text-white`}
                    title="Reload latest matrix file from disk immediately (no rebuild). Use this to see new analyzer data right away."
                  >
                    {masterLoading ? 'Refreshing...' : 'Refresh Data'}
                  </button>
                  <label className="flex items-center gap-2 px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-700 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={autoUpdateEnabled}
                      onChange={(e) => setAutoUpdateEnabled(e.target.checked)}
                      className="w-4 h-4 rounded"
                    />
                    <span className="text-gray-300">
                      Auto-update every 20 min
                    </span>
                  </label>
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
                    onClick={async () => {
                      const value = parseFloat(multiplierInput)
                      if (!isNaN(value) && value > 0) {
                        const clamped = Math.max(0.1, Math.min(100, value))
                        // Update multiplier state
                        setMasterContractMultiplier(clamped)
                        setMultiplierInput(clamped)
                        
                        // Immediately reload stats with new multiplier
                        // Clear existing backend stats first to force refresh
                        setBackendStatsFull(null)
                        try {
                          setMasterLoading(true)
                          console.log(`Reloading stats with contract_multiplier=${clamped}`)
                          const data = await matrixApi.getMatrixData({
                            limit: 10000,
                            order: 'newest',
                            essentialColumnsOnly: true,
                            skipCleaning: true,
                            contractMultiplier: clamped
                          })
                          if (data) {
                            console.log('Received stats_full:', data.stats_full ? 'present' : 'missing')
                            if (data.stats_full) {
                              // Check if total profit changed to verify multiplier was applied
                              const oldTotalProfit = backendStatsFull?.performance_trade_metrics?.total_profit || 0
                              const newTotalProfit = data.stats_full?.performance_trade_metrics?.total_profit || 0
                              console.log(`Total profit: ${oldTotalProfit} -> ${newTotalProfit} (expected ratio: ${clamped / (masterContractMultiplier || 1)})`)
                              
                              setBackendStatsFull(data.stats_full)
                              setBackendStatsMultiplier(clamped)
                              console.log('Updated backendStatsFull with new multiplier')
                            } else {
                              console.warn('No stats_full in response')
                            }
                          } else {
                            const errorData = await dataResponse.json().catch(() => ({ detail: 'Unknown error' }))
                            console.error('Failed to reload stats:', errorData)
                          }
                        } catch (error) {
                          console.error('Failed to reload stats with new multiplier:', error)
                        } finally {
                          setMasterLoading(false)
                        }
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
              
              {/* Filters Toggle for Master */}
              <div className="mb-4">
                <button
                  onClick={() => toggleFilters('master')}
                  className="flex items-center justify-between w-full px-4 py-2 bg-gray-800 hover:bg-gray-800 rounded text-left"
                >
                  <span className="font-medium">Filters for Master</span>
                  <span>{showFilters['master'] ? '▼' : '▶'}</span>
                </button>
                {showFilters['master'] && renderFilters('master')}
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
                    Showing {filteredDataLength || 0} of {filteredDataLength || 0} trades
                  </div>
                  {isSwitchingTab && (
                    <div className="text-center py-4 text-gray-400 text-sm">
                      Switching to Masterstream...
                    </div>
                  )}
                  <DataTable
                    key={`master-${showFilteredDays}-${matrixGeneration}`}
                    data={masterData}
                    streamId="master"
                    workerReady={workerReady && tableTab === 'master'}
                    workerFilteredRows={workerFilteredRowsTabRef.current === 'master' ? workerFilteredRows : null}
                    workerFilteredIndices={workerFilteredRowsTabRef.current === 'master' ? workerFilteredIndices : null}
                    filteredLength={workerFilteredRowsTabRef.current === 'master' ? filteredLength : null}
                    loadedRows={loadedRows}
                    loadingMoreRows={loadingMoreRows}
                    selectedColumns={selectedColumns}
                    activeTab="master"
                    onLoadMoreRows={loadMoreRows}
                    showFilteredDays={showFilteredDays}
                    getFilteredData={getFilteredData}
                  />
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
              
              {/* Filters Toggle */}
              <div className="mb-4">
                <button
                  onClick={() => toggleFilters(activeTab)}
                  className="flex items-center justify-between w-full px-4 py-2 bg-gray-800 hover:bg-gray-800 rounded text-left"
                >
                  <span className="font-medium">Filters for {activeTab}</span>
                  <span>{showFilters[activeTab] ? '▼' : '▶'}</span>
                </button>
                {showFilters[activeTab] && renderFilters(activeTab)}
              </div>
              
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
                  {isSwitchingTab && (
                    <div className="text-center py-4 text-gray-400 text-sm">
                      Switching to {activeTab}...
                    </div>
                  )}
                  <DataTable
                    key={`${tableTab}-${showFilteredDays}-${matrixGeneration}`}
                    data={masterData}
                    streamId={tableTab}
                    workerReady={workerReady && workerFilteredRowsTabRef.current === tableTab}
                    workerFilteredRows={workerFilteredRowsTabRef.current === tableTab ? workerFilteredRows : null}
                    workerFilteredIndices={workerFilteredRowsTabRef.current === tableTab ? workerFilteredIndices : null}
                    filteredLength={workerFilteredRowsTabRef.current === tableTab ? filteredLength : null}
                    loadedRows={loadedRows}
                    loadingMoreRows={loadingMoreRows}
                    selectedColumns={selectedColumns}
                    activeTab={tableTab}
                    onLoadMoreRows={loadMoreRows}
                    showFilteredDays={showFilteredDays}
                    getFilteredData={getFilteredData}
                  />
                </>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

// Component to display the 50 worst days by profit
function WorstDaysTable({ contractMultiplier = 1 }) {
  const [worstDays, setWorstDays] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  
  useEffect(() => {
    const fetchWorstDays = async () => {
      setLoading(true)
      setError(null)
      try {
        // Fetch ALL data from backend (limit: 0 means no limit)
        const data = await matrixApi.getMatrixData({
          limit: 0, // Get all data
          order: 'oldest', // Get in chronological order
          essentialColumnsOnly: false, // Get all columns
          skipCleaning: false,
          contractMultiplier: contractMultiplier,
          includeFilteredExecuted: false,
          streamInclude: null // Get all streams
        })
        
        const trades = data.data || []
        
        if (trades.length === 0) {
          setWorstDays([])
          setLoading(false)
          return
        }
        
        // Calculate daily profit using the existing utility
        const dailyProfit = calculateDateProfit(trades, contractMultiplier)
        
        // Sum profits across all streams for each date
        const dailyTotals = Object.keys(dailyProfit).map(date => {
          const streams = dailyProfit[date]
          const totalProfit = Object.values(streams).reduce((sum, profit) => sum + profit, 0)
          return {
            date,
            totalProfit,
            streams: Object.keys(streams).length,
            streamDetails: streams
          }
        })
        
        // Sort by profit (ascending - worst first) and take top 50
        const sorted = dailyTotals
          .sort((a, b) => a.totalProfit - b.totalProfit)
          .slice(0, 50)
        
        setWorstDays(sorted)
      } catch (err) {
        console.error('Error fetching worst days:', err)
        setError(err.message)
      } finally {
        setLoading(false)
      }
    }
    
    fetchWorstDays()
  }, [contractMultiplier])
  
  const formatCurrency = (value) => {
    if (value === null || value === undefined || isNaN(value)) return '$0.00'
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(value)
  }
  
  const formatDate = (dateStr) => {
    try {
      const date = new Date(dateStr + 'T00:00:00')
      return date.toLocaleDateString('en-US', { 
        weekday: 'short',
        year: 'numeric',
        month: 'short',
        day: 'numeric'
      })
    } catch {
      return dateStr
    }
  }
  
  if (loading) {
    return <div className="text-center py-8 text-gray-400">Loading worst days from full dataset...</div>
  }
  
  if (error) {
    return <div className="text-center py-8 text-red-400">Error loading data: {error}</div>
  }
  
  if (worstDays.length === 0) {
    return <div className="text-center py-8 text-gray-400">No data available</div>
  }
  
  return (
    <div>
      <h3 className="text-lg font-semibold mb-4">50 Worst Trading Days by Profit</h3>
      <div className="overflow-x-auto">
        <table className="w-full border-collapse">
          <thead>
            <tr className="bg-gray-800">
              <th className="px-4 py-3 text-left font-semibold">Rank</th>
              <th className="px-4 py-3 text-left font-semibold">Date</th>
              <th className="px-4 py-3 text-right font-semibold">Total Profit</th>
              <th className="px-4 py-3 text-center font-semibold">Streams</th>
            </tr>
          </thead>
          <tbody>
            {worstDays.map((day, index) => (
              <tr 
                key={day.date} 
                className={`border-b border-gray-700 hover:bg-gray-800 ${
                  day.totalProfit < 0 ? 'text-red-400' : 'text-gray-300'
                }`}
              >
                <td className="px-4 py-3 font-medium">{index + 1}</td>
                <td className="px-4 py-3">{formatDate(day.date)}</td>
                <td className={`px-4 py-3 text-right font-mono ${
                  day.totalProfit < 0 ? 'text-red-400' : day.totalProfit > 0 ? 'text-green-400' : 'text-gray-400'
                }`}>
                  {formatCurrency(day.totalProfit)}
                </td>
                <td className="px-4 py-3 text-center text-gray-400">{day.streams}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

export default App
