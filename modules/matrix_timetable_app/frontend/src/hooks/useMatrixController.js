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
  
  // File change detection
  const [matrixFileId, setMatrixFileId] = useState(null)
  const [matrixFreshness, setMatrixFreshness] = useState(null)
  const lastMatrixFileIdRef = useRef(null)
  
  // Backend stats state
  const [backendStatsFull, setBackendStatsFull] = useState(null)
  const [backendStatsMultiplier, setBackendStatsMultiplier] = useState(null)
  const [masterStatsLoading, setMasterStatsLoading] = useState(false)
  
  // Track if initial load has been attempted
  const hasLoadedRef = useRef(false)
  
  // Matrix generation counter - increments on every successful matrix mutation
  // Used as an invalidation boundary to force UI refresh and worker reinitialization
  const [matrixGeneration, setMatrixGeneration] = useState(0)
  
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
        // Detect file change
        const currentFileId = data.matrix_file_id || data.file || null
        const fileChanged = lastMatrixFileIdRef.current !== null && 
                            lastMatrixFileIdRef.current !== currentFileId
        
        if (fileChanged) {
          console.log(`[Matrix] File changed during load: ${lastMatrixFileIdRef.current} -> ${currentFileId}`)
        }
        
        setMasterData(trades)
        const mergeTime = data.file_mtime ? new Date(data.file_mtime * 1000) : new Date()
        setLastMergeTime(mergeTime)
        
        // Update file ID tracking
        if (currentFileId) {
          setMatrixFileId(currentFileId)
          lastMatrixFileIdRef.current = currentFileId
        }
        
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
  
  // Reinitialize worker data when matrix generation changes (invalidation boundary)
  // This ensures worker gets fresh data after any matrix mutation (resequence, rebuild, reload)
  useEffect(() => {
    if (workerReady && masterData.length > 0 && workerInitData) {
      // Reinitialize worker with fresh data when generation changes
      workerInitData(masterData)
    }
  }, [workerReady, workerInitData, matrixGeneration, masterData])
  
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
  
  // Reload latest matrix from disk (without rebuilding)
  // IMPORTANT: Always reloads data, even if file ID hasn't changed, because the file content may have been updated
  const reloadLatestMatrix = useCallback(async () => {
    setMasterLoading(true)
    setMasterError(null)
    
    try {
      // Check if backend is reachable
      const healthCheck = await matrixApi.checkBackendHealth(3000)
      if (!healthCheck.success) {
        setMasterError(healthCheck.error)
        setMasterLoading(false)
        return
      }
      
      // Reload latest file metadata to check for file changes
      const reloadInfo = await matrixApi.reloadLatestMatrix()
      
      // Check if file changed
      const fileChanged = lastMatrixFileIdRef.current !== null && 
                          lastMatrixFileIdRef.current !== reloadInfo.matrix_file_id
      
      if (fileChanged) {
        console.log(`[Matrix] File changed: ${lastMatrixFileIdRef.current} -> ${reloadInfo.matrix_file_id}`)
      } else {
        console.log(`[Matrix] Reloading data from same file: ${reloadInfo.matrix_file_id}`)
      }
      
      // ALWAYS reload data when user clicks refresh, regardless of file change
      // The file content may have been updated even if filename is the same
      const masterFilters = streamFilters['master'] || {}
      const masterIncludeStreams = masterFilters.include_streams || []
      const streamIncludeParam = masterIncludeStreams.length > 0 ? masterIncludeStreams : null
      
      // Load data - when file changes, load more rows to see affected date window
      // Calculate date range from existing data to determine affected window
      let limit = 10000
      if (fileChanged && masterData.length > 0) {
        // Find the most recent date in current data
        const recentDates = masterData
          .map(row => {
            const date = row.Date || row.trade_date
            if (!date) return null
            return date instanceof Date ? date : new Date(date)
          })
          .filter(d => d && !isNaN(d.getTime()))
          .sort((a, b) => b - a)
        
        if (recentDates.length > 0) {
          // Load enough rows to cover last 60 days (approximately 3000-4000 rows)
          // This ensures we see changes in the affected window
          limit = 5000
          console.log(`[Matrix] File changed, loading ${limit} rows to show affected date window`)
        }
      }
      
      // Force reload by adding a cache-busting timestamp to ensure fresh data
      const data = await matrixApi.getMatrixData({
        limit: limit,
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
      
      // Update state - this will trigger UI refresh
      setMasterData(trades)
      const mergeTime = data.file_mtime ? new Date(data.file_mtime * 1000) : new Date()
      setLastMergeTime(mergeTime)
      
      // Update file ID
      if (data.matrix_file_id || data.file) {
        setMatrixFileId(data.matrix_file_id || data.file)
        lastMatrixFileIdRef.current = data.matrix_file_id || data.file
      }
      
      setMasterError(null)
      console.log(`[Matrix] Successfully reloaded ${trades.length} rows from file: ${data.matrix_file_id || data.file}`)
      
      // Increment matrix generation to invalidate UI and force worker reinitialization
      setMatrixGeneration(prev => prev + 1)
      
      setMasterLoading(false)
    } catch (error) {
      setMasterError(`Failed to reload latest matrix: ${error.message}`)
      console.error('[Matrix] Reload error:', error)
      setMasterLoading(false)
    }
  }, [streamFilters, masterContractMultiplier, includeFilteredExecuted, masterData, workerInitData, workerReady])
  
  // Resequence master matrix (rolling resequence)
  const resequenceMasterMatrix = useCallback(async (resequenceDays = 40) => {
    setMasterLoading(true)
    setMasterError(null)
    
    try {
      // Check if backend is reachable
      const healthCheck = await matrixApi.checkBackendHealth(3000)
      if (!healthCheck.success) {
        setMasterError(healthCheck.error)
        setMasterLoading(false)
        return
      }
      
      const resequenceInfo = await matrixApi.resequenceMatrix({
        streamFilters,
        resequenceDays
      })
      
      console.log(`[Matrix] Rolling resequence complete:`, resequenceInfo.summary)
      
      // CRITICAL: Force reload from disk to get fresh data
      // reloadLatestMatrix() handles data loading, stats, and worker reinit
      await reloadLatestMatrix()
      
      setMasterLoading(false)
    } catch (error) {
      console.error('[Matrix] Resequence error:', error)
      setMasterError(error.message || 'Failed to resequence matrix')
      setMasterLoading(false)
    }
  }, [streamFilters, reloadLatestMatrix])
  
  // Build master matrix (full rebuild)
  const buildMasterMatrix = useCallback(async () => {
    setMasterLoading(true)
    setMasterError(null)
    
    try {
      // Check if backend is reachable
      const healthCheck = await matrixApi.checkBackendHealth(3000)
      if (!healthCheck.success) {
        setMasterError(healthCheck.error)
        setMasterLoading(false)
        return
      }
      
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
      
      // Build matrix
      await matrixApi.buildMatrix({
        streamFilters,
        visibleYears,
        warmupMonths: 1
      })
      
      console.log(`[Matrix] Full rebuild complete`)
      
      // CRITICAL: Force reload from disk to get fresh data
      await reloadLatestMatrix()
      
      setMasterLoading(false)
    } catch (error) {
      console.error('[Matrix] Build error:', error)
      setMasterError(error.message || 'Failed to build master matrix')
      setMasterLoading(false)
    }
  }, [streamFilters, reloadLatestMatrix])
  
  // Check matrix freshness periodically
  useEffect(() => {
    const checkFreshness = async () => {
      try {
        const freshness = await matrixApi.getMatrixFreshness()
        setMatrixFreshness(freshness)
      } catch (error) {
        console.warn('[Matrix] Failed to check freshness:', error)
      }
    }
    
    // Check immediately
    checkFreshness()
    
    // Check every 2 minutes
    const interval = setInterval(checkFreshness, 2 * 60 * 1000)
    return () => clearInterval(interval)
  }, [])
  
  // Auto-update interval - automatically resequence then refresh page every 20 minutes
  useEffect(() => {
    if (!autoUpdateEnabled) {
      return
    }
    
    const interval = setInterval(async () => {
      try {
        console.log('[Matrix] Auto-update: Triggering resequence...')
        await resequenceMasterMatrix()
        console.log('[Matrix] Auto-update: Resequence complete, refreshing page...')
        window.location.reload()
      } catch (error) {
        console.error('[Matrix] Auto-update error:', error)
        // Still refresh even if resequence fails
        window.location.reload()
      }
    }, 20 * 60 * 1000) // 20 minutes
    
    return () => clearInterval(interval)
  }, [autoUpdateEnabled, resequenceMasterMatrix])
  
  return {
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
  }
}
