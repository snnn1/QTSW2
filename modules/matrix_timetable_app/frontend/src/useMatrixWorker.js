// Hook for managing Matrix Web Worker
import { useState, useEffect, useRef, useCallback } from 'react'

// #region agent log
const logDebug = (location, message, data) => {
  fetch('http://127.0.0.1:7242/ingest/eade699f-d61f-42de-a82b-fcbc1c4af825',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location,message,data,timestamp:Date.now(),sessionId:'debug-session',runId:'run1'})}).catch(()=>{});
};
// #endregion

const CONTRACT_VALUES = {
  'ES': 50,
  'NQ': 10,
  'YM': 5,
  'CL': 1000,
  'NG': 10000,
  'GC': 100
}

export function useMatrixWorker() {
  const workerRef = useRef(null)
  const [workerReady, setWorkerReady] = useState(false)
  const [filteredLength, setFilteredLength] = useState(0)
  const [filterMask, setFilterMask] = useState(null)
  const [filteredIndices, setFilteredIndices] = useState([])
  const [filteredRows, setFilteredRows] = useState([])
  const [stats, setStats] = useState(null)
  const [statsLoading, setStatsLoading] = useState(false)
  const [profitBreakdown, setProfitBreakdown] = useState(null)
  const [breakdownType, setBreakdownType] = useState(null)
  const [breakdownLoading, setBreakdownLoading] = useState(false)
  const [timetable, setTimetable] = useState([])
  const [timetableLoading, setTimetableLoading] = useState(false)
  const [executionTimetable, setExecutionTimetable] = useState(null)
  const [error, setError] = useState(null)
  
  // Track active request IDs for operation cancellation
  const activeRequestIdRef = useRef(0)
  const activeFilterRequestIdRef = useRef(null)
  const activeStatsRequestIdRef = useRef(null)
  const activeBreakdownRequestIdRef = useRef(null)
  const activeTimetableRequestIdRef = useRef(null)
  
  // Track breakdown types to accept responses even if request ID is slightly stale
  // This prevents breakdown data from disappearing when switching tabs quickly
  // We track a Set of active breakdown types since we send both _before and _after requests
  const activeBreakdownTypesRef = useRef(new Set())
  
  // Initialize worker
  useEffect(() => {
    let worker = null
    try {
      // Reset worker ready state when creating new worker (important for hot reloads)
      setWorkerReady(false)
      worker = new Worker(new URL('./matrixWorker.js', import.meta.url), { type: 'module' })
      workerRef.current = worker
      
      worker.onmessage = (e) => {
        const { type, payload } = e.data
        
        switch (type) {
          case 'DATA_INITIALIZED':
            setWorkerReady(true)
            setError(null)
            // #region agent log
            logDebug('useMatrixWorker.js:44', 'DATA_INITIALIZED handled', {duration: Date.now() - messageReceivedStart, hypothesisId: 'A'});
            // #endregion
            break
            
          case 'FILTERED':
            // #region agent log
            const filteredReceivedStart = Date.now();
            logDebug('useMatrixWorker.js:49', 'FILTERED message received', {length: payload.length, hasRows: !!payload.rows, requestId: payload.requestId, activeRequestId: activeFilterRequestIdRef.current, hypothesisId: 'A'});
            // #endregion
            // Check if this response is for the current active request
            if (payload.requestId !== undefined && payload.requestId !== activeFilterRequestIdRef.current) {
              // #region agent log
              logDebug('useMatrixWorker.js:66', 'FILTERED message ignored - stale request', {requestId: payload.requestId, activeRequestId: activeFilterRequestIdRef.current, hypothesisId: 'A'});
              // #endregion
              break // Ignore stale response
            }
            setFilteredLength(payload.length)
            setFilterMask(payload.mask)
            setFilteredIndices(payload.indices || [])
            if (payload.rows) {
              setFilteredRows(payload.rows)
            }
            // #region agent log
            logDebug('useMatrixWorker.js:55', 'FILTERED state updated', {duration: Date.now() - filteredReceivedStart, totalDuration: Date.now() - messageReceivedStart, hypothesisId: 'A'});
            // #endregion
            break
            
          case 'STATS':
            // #region agent log
            const statsReceivedStart = Date.now();
            logDebug('useMatrixWorker.js:58', 'STATS message received', {requestId: payload.requestId, activeRequestId: activeStatsRequestIdRef.current, hypothesisId: 'A'});
            // #endregion
            // Check if this response is for the current active request
            if (payload.requestId !== undefined && payload.requestId !== activeStatsRequestIdRef.current) {
              // #region agent log
              logDebug('useMatrixWorker.js:82', 'STATS message ignored - stale request', {requestId: payload.requestId, activeRequestId: activeStatsRequestIdRef.current, hypothesisId: 'A'});
              // #endregion
              break // Ignore stale response
            }
            setStats(payload.stats)
            setStatsLoading(false)
            // #region agent log
            logDebug('useMatrixWorker.js:60', 'STATS state updated', {duration: Date.now() - statsReceivedStart, totalDuration: Date.now() - messageReceivedStart, hypothesisId: 'A'});
            // #endregion
            break
            
          case 'PROFIT_BREAKDOWN':
            // #region agent log
            const breakdownReceivedStart = Date.now();
            logDebug('useMatrixWorker.js:63', 'PROFIT_BREAKDOWN message received', {breakdownType: payload.breakdownType, requestId: payload.requestId, activeRequestId: activeBreakdownRequestIdRef.current, activeBreakdownTypes: Array.from(activeBreakdownTypesRef.current), hypothesisId: 'B'});
            // #endregion
            // For breakdowns, accept response if:
            // 1. Request ID matches (exact match), OR
            // 2. Breakdown type is in the set of active breakdown types (allows slightly stale but valid data)
            // This prevents breakdown data from disappearing when switching tabs quickly
            const requestIdMatches = payload.requestId !== undefined && payload.requestId === activeBreakdownRequestIdRef.current
            const breakdownTypeMatches = payload.breakdownType && activeBreakdownTypesRef.current.has(payload.breakdownType)
            
            if (!requestIdMatches && !breakdownTypeMatches) {
              // #region agent log
              logDebug('useMatrixWorker.js:94', 'PROFIT_BREAKDOWN message ignored - stale request', {requestId: payload.requestId, activeRequestId: activeBreakdownRequestIdRef.current, breakdownType: payload.breakdownType, activeBreakdownTypes: Array.from(activeBreakdownTypesRef.current), hypothesisId: 'B'});
              // #endregion
              break // Ignore stale response
            }
            setProfitBreakdown(payload.breakdown)
            setBreakdownType(payload.breakdownType)
            setBreakdownLoading(false)
            // #region agent log
            logDebug('useMatrixWorker.js:66', 'PROFIT_BREAKDOWN state updated', {duration: Date.now() - breakdownReceivedStart, totalDuration: Date.now() - messageReceivedStart, hypothesisId: 'B'});
            // #endregion
            break
            
          case 'TIMETABLE':
            // #region agent log
            const timetableReceivedStart = Date.now();
            logDebug('useMatrixWorker.js:69', 'TIMETABLE message received', {timetableLength: payload.timetable?.length || 0, requestId: payload.requestId, activeRequestId: activeTimetableRequestIdRef.current, hypothesisId: 'C'});
            // #endregion
            // Check if this response is for the current active request
            if (payload.requestId !== undefined && payload.requestId !== activeTimetableRequestIdRef.current) {
              // #region agent log
              logDebug('useMatrixWorker.js:107', 'TIMETABLE message ignored - stale request', {requestId: payload.requestId, activeRequestId: activeTimetableRequestIdRef.current, hypothesisId: 'C'});
              // #endregion
              break // Ignore stale response
            }
            setTimetable(payload.timetable || [])
            setTimetableLoading(false)
            // Store execution timetable for saving
            if (payload.executionTimetable) {
              setExecutionTimetable(payload.executionTimetable)
            }
            // #region agent log
            logDebug('useMatrixWorker.js:75', 'TIMETABLE state updated', {duration: Date.now() - timetableReceivedStart, totalDuration: Date.now() - messageReceivedStart, hypothesisId: 'C'});
            // #endregion
            break
            
          case 'ROWS':
            // Handle row requests (for virtualization)
            break
            
          case 'ERROR':
            setError(payload.message)
            setTimetableLoading(false) // Stop loading on error
            console.error('Worker error:', payload)
            // #region agent log
            logDebug('useMatrixWorker.js:84', 'ERROR message received', {error: payload.message, hypothesisId: 'A'});
            // #endregion
            break
        }
      }
      
      worker.onerror = (error) => {
        setError(error.message)
        console.error('Worker error:', error)
      }
      
      return () => {
        if (worker) {
          worker.terminate()
        }
      }
    } catch (err) {
      setError(`Failed to initialize worker: ${err.message}`)
      console.error('Worker initialization error:', err)
      // Don't crash the app - just disable worker features
      setWorkerReady(false)
    }
  }, [])
  
  // Initialize data in worker
  const initData = useCallback((data) => {
    if (!workerRef.current) return
    
    workerRef.current.postMessage({
      type: 'INIT_DATA',
      payload: { data }
    })
  }, [])
  
  // Filter data
  const filter = useCallback((streamFilters, streamId, returnRows = false, sortIndices = true) => {
    // #region agent log
    const filterStart = Date.now();
    logDebug('useMatrixWorker.js:118', 'Filter function called', {streamId, returnRows, sortIndices, workerReady, hasWorker: !!workerRef.current, hypothesisId: 'A'});
    // #endregion
    if (!workerRef.current || !workerReady) {
      // #region agent log
      logDebug('useMatrixWorker.js:119', 'Filter skipped - worker not ready', {hypothesisId: 'A'});
      // #endregion
      return
    }
    
    // Increment request ID and track active request
    activeRequestIdRef.current += 1
    const requestId = activeRequestIdRef.current
    activeFilterRequestIdRef.current = requestId
    
    // #region agent log
    const postMessageStart = Date.now();
    logDebug('useMatrixWorker.js:122', 'Posting FILTER message to worker', {streamId, requestId, hypothesisId: 'A'});
    // #endregion
    workerRef.current.postMessage({
      type: 'FILTER',
      payload: { streamFilters, streamId, returnRows, sortIndices, requestId }
    })
    // #region agent log
    logDebug('useMatrixWorker.js:125', 'FILTER message posted', {duration: Date.now() - postMessageStart, totalDuration: Date.now() - filterStart, requestId, hypothesisId: 'A'});
    // #endregion
  }, [workerReady])
  
  // Calculate stats
  const calculateStats = useCallback((streamFilters, streamId, contractMultiplier, includeFilteredExecuted = true) => {
    // #region agent log
    const statsStart = Date.now();
    logDebug('useMatrixWorker.js:128', 'CalculateStats function called', {streamId, workerReady, hasWorker: !!workerRef.current, hypothesisId: 'A'});
    // #endregion
    if (!workerRef.current || !workerReady) {
      // #region agent log
      logDebug('useMatrixWorker.js:129', 'CalculateStats skipped - worker not ready', {hypothesisId: 'A'});
      // #endregion
      return
    }
    
    // Increment request ID and track active request
    activeRequestIdRef.current += 1
    const requestId = activeRequestIdRef.current
    activeStatsRequestIdRef.current = requestId
    
    setStatsLoading(true)
    // #region agent log
    const postMessageStart = Date.now();
    logDebug('useMatrixWorker.js:133', 'Posting CALCULATE_STATS message to worker', {streamId, requestId, hypothesisId: 'A'});
    // #endregion
    workerRef.current.postMessage({
      type: 'CALCULATE_STATS',
      payload: {
        streamFilters,
        streamId,
        contractMultiplier,
        contractValues: CONTRACT_VALUES,
        includeFilteredExecuted,
        requestId
      }
    })
    // #region agent log
    logDebug('useMatrixWorker.js:142', 'CALCULATE_STATS message posted', {duration: Date.now() - postMessageStart, totalDuration: Date.now() - statsStart, requestId, hypothesisId: 'A'});
    // #endregion
  }, [workerReady])
  
  // Get rows by indices (for virtualization)
  const getRows = useCallback((indices, callback) => {
    if (!workerRef.current || !workerReady) return
    
    const handler = (e) => {
      if (e.data.type === 'ROWS') {
        callback(e.data.payload.rows)
        workerRef.current.removeEventListener('message', handler)
      }
    }
    
    workerRef.current.addEventListener('message', handler)
    workerRef.current.postMessage({
      type: 'GET_ROWS',
      payload: { indices }
    })
  }, [workerReady])
  
  // Calculate profit breakdown
  const calculateProfitBreakdown = useCallback((streamFilters, streamId, contractMultiplier, breakdownType, useFiltered) => {
    // #region agent log
    const breakdownStart = Date.now();
    logDebug('useMatrixWorker.js:163', 'CalculateProfitBreakdown function called', {streamId, breakdownType, useFiltered, workerReady, hasWorker: !!workerRef.current, hypothesisId: 'B'});
    // #endregion
    if (!workerRef.current || !workerReady) {
      // #region agent log
      logDebug('useMatrixWorker.js:164', 'CalculateProfitBreakdown skipped - worker not ready', {hypothesisId: 'B'});
      // #endregion
      return
    }
    
    // Extract the base breakdown type (e.g., "time" from "time_before")
    const baseBreakdownType = breakdownType.split('_')[0]
    
    // Clear old breakdown types for different base types when switching tabs
    // Keep only breakdown types that match the current base type
    const currentBaseTypes = Array.from(activeBreakdownTypesRef.current).map(bt => bt.split('_')[0])
    if (currentBaseTypes.length > 0 && !currentBaseTypes.includes(baseBreakdownType)) {
      // Switching to a different breakdown tab - clear old types
      activeBreakdownTypesRef.current.clear()
    }
    
    // Increment request ID and track active request
    activeRequestIdRef.current += 1
    const requestId = activeRequestIdRef.current
    activeBreakdownRequestIdRef.current = requestId
    activeBreakdownTypesRef.current.add(breakdownType) // Track breakdown type for lenient matching
    
    setBreakdownLoading(true)
    // #region agent log
    const postMessageStart = Date.now();
    logDebug('useMatrixWorker.js:168', 'Posting CALCULATE_PROFIT_BREAKDOWN message to worker', {breakdownType, requestId, hypothesisId: 'B'});
    // #endregion
    workerRef.current.postMessage({
      type: 'CALCULATE_PROFIT_BREAKDOWN',
      payload: {
        streamFilters,
        streamId,
        contractMultiplier,
        contractValues: CONTRACT_VALUES,
        breakdownType,
        useFiltered,
        requestId
      }
    })
    // #region agent log
    logDebug('useMatrixWorker.js:178', 'CALCULATE_PROFIT_BREAKDOWN message posted', {duration: Date.now() - postMessageStart, totalDuration: Date.now() - breakdownStart, requestId, hypothesisId: 'B'});
    // #endregion
  }, [workerReady])
  
  // Calculate timetable
  const calculateTimetable = useCallback((streamFilters, currentTradingDay) => {
    // #region agent log
    const timetableStart = Date.now();
    logDebug('useMatrixWorker.js:181', 'CalculateTimetable function called', {workerReady, hasWorker: !!workerRef.current, hypothesisId: 'C'});
    // #endregion
    if (!workerRef.current || !workerReady) {
      // #region agent log
      logDebug('useMatrixWorker.js:182', 'CalculateTimetable skipped - worker not ready', {hypothesisId: 'C'});
      // #endregion
      return
    }
    
    // Increment request ID and track active request
    activeRequestIdRef.current += 1
    const requestId = activeRequestIdRef.current
    activeTimetableRequestIdRef.current = requestId
    
    setTimetableLoading(true)
    // #region agent log
    const postMessageStart = Date.now();
    logDebug('useMatrixWorker.js:186', 'Posting CALCULATE_TIMETABLE message to worker', {requestId, hypothesisId: 'C'});
    // #endregion
    workerRef.current.postMessage({
      type: 'CALCULATE_TIMETABLE',
      payload: {
        streamFilters,
        currentTradingDay: currentTradingDay ? currentTradingDay.toISOString().split('T')[0] : null,
        requestId
      }
    })
    // #region agent log
    logDebug('useMatrixWorker.js:192', 'CALCULATE_TIMETABLE message posted', {duration: Date.now() - postMessageStart, totalDuration: Date.now() - timetableStart, requestId, hypothesisId: 'C'});
    // #endregion
  }, [workerReady])
  
  return {
    workerReady,
    filteredLength,
    filterMask,
    filteredIndices,
    filteredRows,
    stats,
    statsLoading,
    profitBreakdown,
    breakdownType,
    breakdownLoading,
    timetable,
    timetableLoading,
    executionTimetable,
    error,
    initData,
    filter,
    calculateStats,
    getRows,
    calculateProfitBreakdown,
    calculateTimetable
  }
}

