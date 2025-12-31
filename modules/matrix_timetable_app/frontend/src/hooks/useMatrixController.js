/**
 * Matrix Controller Hook
 * 
 * Centralizes matrix orchestration logic:
 * - Backend data lifecycle (load, rebuild, update)
 * - Worker lifecycle (initData, compute triggers)
 * - Derived state management
 * 
 * This hook separates matrix domain logic from UI rendering in App.jsx
 */

import { useState, useEffect, useRef, useCallback } from 'react'
import { useMatrixWorker } from '../useMatrixWorker'
import * as matrixApi from '../api/matrixApi'
import { DEFAULT_COLUMNS } from '../utils/constants'

const API_PORT = import.meta.env.VITE_API_PORT || '8000'

export function useMatrixController({
  streamFilters,
  masterContractMultiplier,
  includeFilteredExecuted,
  activeTab,
  deferredActiveTab,
  autoUpdateEnabled,
  showFilteredDays = true
}) {
  // Backend data state
  const [masterData, setMasterData] = useState([])
  const [masterLoading, setMasterLoading] = useState(false)
  const [masterError, setMasterError] = useState(null)
  const [availableYearsFromAPI, setAvailableYearsFromAPI] = useState([])
  const [lastMergeTime, setLastMergeTime] = useState(null)
  const [availableColumns, setAvailableColumns] = useState([])
  
  // Backend stats state
  const [backendStatsFull, setBackendStatsFull] = useState(null)
  const [backendStatsMultiplier, setBackendStatsMultiplier] = useState(null)
  const [masterStatsLoading, setMasterStatsLoading] = useState(false)
  
  // Track if initial load has been attempted
  const hasLoadedRef = useRef(false)
  
  // Worker hook
  const {
    workerReady,
    filteredLength,
    filteredIndices: workerFilteredIndices,
    filteredRows: workerFilteredRows,
    stats: workerStats,
    statsLoading,
    profitBreakdown: workerProfitBreakdown,
    breakdownType: workerBreakdownType,
    breakdownLoading: workerBreakdownLoading,
    timetable: workerTimetable,
    timetableLoading: workerTimetableLoading,
    executionTimetable: workerExecutionTimetable,
    error: workerError,
    initData: workerInitData,
    filter: workerFilter,
    calculateStats: workerCalculateStats,
    getRows: workerGetRows,
    calculateProfitBreakdown,
    calculateTimetable: workerCalculateTimetable
  } = useMatrixWorker()
  
  // Load master matrix function
  const loadMasterMatrix = useCallback(async (rebuild = false, rebuildStream = null) => {
    setMasterLoading(true)
    setMasterError(null)
    
    const hadExistingData = masterData.length > 0
    
    try {
      // Check if backend is reachable
      const healthCheck = await matrixApi.checkBackendHealth(3000)
      if (!healthCheck.success) {
        setMasterError(healthCheck.error)
        if (!hadExistingData) {
          setMasterData([])
        }
        setMasterLoading(false)
        return
      }
      
      // If rebuild requested, build matrix first
      if (rebuild) {
        // Extract visible years from stream filters
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
        
        try {
          await matrixApi.buildMatrix({
            streamFilters,
            visibleYears,
            rebuildStream,
            warmupMonths: 1
          })
        } catch (error) {
          setMasterError(error.message)
          setMasterLoading(false)
          return
        }
      }
      
      // Get master stream inclusion filter
      const masterFilters = streamFilters['master'] || {}
      const masterIncludeStreams = masterFilters.include_streams || []
      const streamIncludeParam = masterIncludeStreams.length > 0 ? masterIncludeStreams : null
      
      // Load the matrix data
      const data = await matrixApi.getMatrixData({
        limit: 10000,
        order: 'newest',
        essentialColumnsOnly: true,
        skipCleaning: true,
        contractMultiplier: masterContractMultiplier,
        includeFilteredExecuted: includeFilteredExecuted,
        streamInclude: streamIncludeParam
      })
      
      const trades = data.data || []
      
      // Store full-dataset stats if available
      if (data.stats_full) {
        setBackendStatsFull(data.stats_full)
        setBackendStatsMultiplier(masterContractMultiplier)
      }
      
      if (data.years && Array.isArray(data.years) && data.years.length > 0) {
        setAvailableYearsFromAPI(data.years)
      }
      
      if (trades.length > 0) {
        setMasterData(trades)
        const mergeTime = data.file_mtime ? new Date(data.file_mtime) : new Date()
        setLastMergeTime(mergeTime)
        
        // Initialize worker with new data
        if (trades.length > 0) {
          workerInitData(trades)
        }
        
        // Detect available columns
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
        }
        
        setMasterError(null)
      } else {
        if (!hadExistingData) {
          setMasterData([])
          setMasterError('No data found. Click "Rebuild Matrix" to build it.')
        } else {
          setMasterError('Warning: Load returned no data. Previous data preserved.')
        }
      }
    } catch (error) {
      if (error.name === 'TypeError' && error.message.includes('fetch')) {
        setMasterError(`Cannot connect to backend. Make sure the dashboard backend is running on http://localhost:${API_PORT}`)
      } else {
        setMasterError('Failed to load master matrix: ' + error.message)
      }
      if (!hadExistingData) {
        setMasterData([])
      }
    } finally {
      setMasterLoading(false)
    }
  }, [
    streamFilters,
    masterContractMultiplier,
    includeFilteredExecuted,
    masterData.length,
    availableColumns.length,
    workerInitData
  ])
  
  // Refetch stats only (for when includeFilteredExecuted toggle changes)
  // Accept includeFilteredExecuted as parameter to avoid closure issues
  const refetchMasterStats = useCallback(async (includeFilteredValue = null) => {
    setMasterStatsLoading(true)
    try {
      // Use provided value or fall back to current state
      const valueToUse = includeFilteredValue !== null ? includeFilteredValue : includeFilteredExecuted
      
      // Check if backend is reachable
      const healthCheck = await matrixApi.checkBackendHealth(3000)
      if (!healthCheck.success) {
        console.warn('Backend not available for stats refetch')
        setMasterStatsLoading(false)
        return
      }
      
      console.log(`[Master Stats] Refetching with includeFilteredExecuted=${valueToUse}`)
      
      // Get master stream inclusion filter
      const masterFilters = streamFilters['master'] || {}
      const masterIncludeStreams = masterFilters.include_streams || []
      const streamIncludeParam = masterIncludeStreams.length > 0 ? masterIncludeStreams : null
      
      // Fetch only stats (with same 10k limit to avoid loading full table)
      const data = await matrixApi.getMatrixData({
        limit: 10000,
        order: 'newest',
        essentialColumnsOnly: true,
        skipCleaning: true,
        contractMultiplier: masterContractMultiplier,
        includeFilteredExecuted: valueToUse,
        streamInclude: streamIncludeParam
      })
      
      // Update stats only (don't touch masterData)
      if (data.stats_full) {
        setBackendStatsFull(data.stats_full)
        setBackendStatsMultiplier(masterContractMultiplier)
        console.log(`[Master Stats] Successfully refetched stats with includeFilteredExecuted=${valueToUse}`)
        console.log(`[Master Stats] Sample counts:`, {
          total: data.stats_full?.sample_counts?.executed_trades_total,
          allowed: data.stats_full?.sample_counts?.executed_trades_allowed,
          filtered: data.stats_full?.sample_counts?.executed_trades_filtered
        })
      } else {
        console.warn('[Master Stats] No stats_full in response')
      }
    } catch (error) {
      console.error('Failed to refetch master stats:', error)
    } finally {
      setMasterStatsLoading(false)
    }
  }, [masterContractMultiplier, includeFilteredExecuted, streamFilters])
  
  // Update master matrix function (rolling window update)
  const updateMasterMatrix = useCallback(async () => {
    setMasterLoading(true)
    setMasterError(null)
    
    const hadExistingData = masterData.length > 0
    
    try {
      // Check if backend is reachable
      const healthCheck = await matrixApi.checkBackendHealth(3000)
      if (!healthCheck.success) {
        setMasterError(healthCheck.error)
        if (!hadExistingData) {
          setMasterData([])
        }
        setMasterLoading(false)
        return
      }
      
      // Update matrix
      try {
        await matrixApi.updateMatrix({ streamFilters })
      } catch (error) {
        setMasterError(error.message)
        setMasterLoading(false)
        return
      }
      
      // Get master stream inclusion filter
      const masterFilters = streamFilters['master'] || {}
      const masterIncludeStreams = masterFilters.include_streams || []
      const streamIncludeParam = masterIncludeStreams.length > 0 ? masterIncludeStreams : null
      
      // Load the updated matrix data
      const data = await matrixApi.getMatrixData({
        limit: 10000,
        order: 'newest',
        essentialColumnsOnly: true,
        skipCleaning: true,
        contractMultiplier: masterContractMultiplier,
        includeFilteredExecuted: includeFilteredExecuted,
        streamInclude: streamIncludeParam
      })
      
      const trades = data.data || []
      
      if (data.years && Array.isArray(data.years) && data.years.length > 0) {
        setAvailableYearsFromAPI(data.years)
      }
      
      // Update master data and reinitialize worker
      setMasterData(trades)
      const mergeTime = data.file_mtime ? new Date(data.file_mtime) : new Date()
      setLastMergeTime(mergeTime)
      
      // Reinitialize worker with new data
      if (trades.length > 0 && workerInitData && workerReady) {
        setTimeout(() => {
          workerInitData(trades)
        }, 100)
      }
      
      setMasterError(null)
      setMasterLoading(false)
    } catch (error) {
      setMasterError(`Failed to update master matrix: ${error.message}`)
      if (!hadExistingData) {
        setMasterData([])
      }
      setMasterLoading(false)
    }
  }, [masterData, streamFilters, masterContractMultiplier, workerInitData, workerReady])
  
  // Reinitialize worker data when worker becomes ready and masterData exists
  useEffect(() => {
    if (workerReady && masterData.length > 0 && workerInitData) {
      const timeoutId = setTimeout(() => {
        workerInitData(masterData)
      }, 50)
      return () => clearTimeout(timeoutId)
    }
  }, [workerReady, workerInitData, masterData.length])
  
  // Apply filters in worker when filters or active tab changes
  useEffect(() => {
    try {
      // Breakdown tabs don't use data table filtering
      const breakdownTabs = ['time', 'day', 'dom', 'date', 'month', 'year', 'timetable']
      if (breakdownTabs.includes(deferredActiveTab)) {
        return
      }
      
      if (workerReady && masterData.length > 0 && workerFilter) {
        const streamId = deferredActiveTab === 'timetable' ? 'master' : deferredActiveTab
        
        // Request filtering - worker will use its own cache for performance
        const returnRows = deferredActiveTab !== 'timetable'
        workerFilter(streamFilters, streamId, returnRows, true, showFilteredDays)
        
        // Only calculate worker stats for master stream or if backend stats aren't available
        // For individual streams, backend stats should be used (they cover full dataset)
        // Worker stats are only a fallback and may be incomplete
        if (deferredActiveTab !== 'timetable' && workerCalculateStats) {
          // For master stream, always calculate worker stats (used as fallback)
          // For individual streams, worker stats are only calculated but backend stats take precedence
          if (streamId === 'master') {
            workerCalculateStats(streamFilters, streamId, masterContractMultiplier, includeFilteredExecuted)
          } else {
            // Still calculate worker stats for individual streams as fallback, but backend stats should be used
            workerCalculateStats(streamFilters, streamId, 1.0, includeFilteredExecuted) // Individual streams use 1.0 multiplier
          }
        }
      }
    } catch (error) {
      console.error('Error in filter useEffect:', error)
    }
  }, [
    streamFilters,
    deferredActiveTab,
    masterContractMultiplier,
    workerReady,
    masterData.length,
    workerFilter,
    workerCalculateStats,
    includeFilteredExecuted,
    showFilteredDays
  ])
  
  // Auto-update interval
  const masterLoadingRef = useRef(masterLoading)
  useEffect(() => {
    masterLoadingRef.current = masterLoading
  }, [masterLoading])
  
  useEffect(() => {
    if (!autoUpdateEnabled) {
      return
    }
    
    const interval = setInterval(() => {
      if (!masterLoadingRef.current) {
        updateMasterMatrix()
      }
    }, 20 * 60 * 1000) // 20 minutes
    
    return () => clearInterval(interval)
  }, [autoUpdateEnabled, updateMasterMatrix])
  
  return {
    // Backend data state
    masterData,
    masterLoading,
    masterError,
    availableYearsFromAPI,
    lastMergeTime,
    availableColumns,
    setAvailableColumns,
    
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
    updateMasterMatrix,
    refetchMasterStats,
    hasLoadedRef
  }
}
