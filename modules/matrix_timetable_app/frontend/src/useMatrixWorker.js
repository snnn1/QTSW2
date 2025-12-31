// Hook for managing Matrix Web Worker
import { useState, useEffect, useRef, useCallback } from 'react'
import { WORKER_MESSAGE_TYPES, WORKER_RESPONSE_TYPES, CONTRACT_VALUES, createWorkerMessage } from './worker/contract'
import { useWorkerRequestManager } from './worker/requestManager'

// #region agent log
// Disabled for performance - debug logging adds network overhead
// Enable by setting VITE_ENABLE_DEBUG_LOGGING=true in environment
const ENABLE_DEBUG_LOGGING = import.meta.env.VITE_ENABLE_DEBUG_LOGGING === 'true'
const logDebug = ENABLE_DEBUG_LOGGING 
  ? (location, message, data) => {
      fetch('http://127.0.0.1:7242/ingest/eade699f-d61f-42de-a82b-fcbc1c4af825',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location,message,data,timestamp:Date.now(),sessionId:'debug-session',runId:'run1'})}).catch(()=>{});
    }
  : () => {} // No-op when disabled
// #endregion

// Operation types for request manager
const OPERATIONS = {
  FILTER: 'FILTER',
  STATS: 'STATS',
  BREAKDOWN: 'BREAKDOWN',
  TIMETABLE: 'TIMETABLE',
  GET_ROWS: 'GET_ROWS'
}

export function useMatrixWorker() {
  const workerRef = useRef(null)
  const [workerReady, setWorkerReady] = useState(false)
  const [filteredLength, setFilteredLength] = useState(0)
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
  const [dataInitialized, setDataInitialized] = useState(false)
  
  // Centralized request manager
  const requestManager = useWorkerRequestManager()
  
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
          case WORKER_RESPONSE_TYPES.DATA_INITIALIZED:
            setWorkerReady(true)
            setDataInitialized(true)
            setError(null)
            requestManager.reset() // Clear all active requests on reinit
            // #region agent log
            logDebug('useMatrixWorker.js:44', 'DATA_INITIALIZED handled', {hypothesisId: 'A'});
            // #endregion
            break
            
          case WORKER_RESPONSE_TYPES.FILTERED:
            // #region agent log
            const filteredReceivedStart = Date.now();
            logDebug('useMatrixWorker.js:49', 'FILTERED message received', {length: payload.length, hasRows: !!payload.rows, requestId: payload.requestId, hypothesisId: 'A'});
            // #endregion
            // Check if this response should be accepted
            if (!requestManager.shouldAcceptResponse(OPERATIONS.FILTER, payload.requestId)) {
              // #region agent log
              logDebug('useMatrixWorker.js:66', 'FILTERED message ignored - stale request', {requestId: payload.requestId, hypothesisId: 'A'});
              // #endregion
              break // Ignore stale response
            }
            setFilteredLength(payload.length)
            setFilteredIndices(payload.indices || [])
            if (payload.rows) {
              setFilteredRows(payload.rows)
              // Clear any stale loaded rows - new filtered rows are coming
            }
            // #region agent log
            logDebug('useMatrixWorker.js:55', 'FILTERED state updated', {duration: Date.now() - filteredReceivedStart, hypothesisId: 'A'});
            // #endregion
            break
            
          case WORKER_RESPONSE_TYPES.STATS:
            // #region agent log
            const statsReceivedStart = Date.now();
            logDebug('useMatrixWorker.js:58', 'STATS message received', {requestId: payload.requestId, hypothesisId: 'A'});
            // #endregion
            // Check if this response should be accepted
            if (!requestManager.shouldAcceptResponse(OPERATIONS.STATS, payload.requestId)) {
              // #region agent log
              logDebug('useMatrixWorker.js:82', 'STATS message ignored - stale request', {requestId: payload.requestId, hypothesisId: 'A'});
              // #endregion
              break // Ignore stale response
            }
            setStats(payload.stats)
            setStatsLoading(false)
            // #region agent log
            logDebug('useMatrixWorker.js:60', 'STATS state updated', {duration: Date.now() - statsReceivedStart, hypothesisId: 'A'});
            // #endregion
            break
            
          case WORKER_RESPONSE_TYPES.PROFIT_BREAKDOWN:
            // #region agent log
            const breakdownReceivedStart = Date.now();
            logDebug('useMatrixWorker.js:63', 'PROFIT_BREAKDOWN message received', {breakdownType: payload.breakdownType, requestId: payload.requestId, hypothesisId: 'B'});
            // #endregion
            // For breakdowns, use lenient matching by breakdown type
            if (!requestManager.shouldAcceptResponse(OPERATIONS.BREAKDOWN, payload.requestId, payload.breakdownType)) {
              // #region agent log
              logDebug('useMatrixWorker.js:94', 'PROFIT_BREAKDOWN message ignored - stale request', {requestId: payload.requestId, breakdownType: payload.breakdownType, hypothesisId: 'B'});
              // #endregion
              break // Ignore stale response
            }
            setProfitBreakdown(payload.breakdown)
            setBreakdownType(payload.breakdownType)
            setBreakdownLoading(false)
            // #region agent log
            logDebug('useMatrixWorker.js:66', 'PROFIT_BREAKDOWN state updated', {duration: Date.now() - breakdownReceivedStart, hypothesisId: 'B'});
            // #endregion
            break
            
          case WORKER_RESPONSE_TYPES.TIMETABLE:
            // #region agent log
            const timetableReceivedStart = Date.now();
            logDebug('useMatrixWorker.js:69', 'TIMETABLE message received', {timetableLength: payload.timetable?.length || 0, requestId: payload.requestId, hypothesisId: 'C'});
            // #endregion
            // Check if this response should be accepted
            if (!requestManager.shouldAcceptResponse(OPERATIONS.TIMETABLE, payload.requestId)) {
              // #region agent log
              logDebug('useMatrixWorker.js:107', 'TIMETABLE message ignored - stale request', {requestId: payload.requestId, hypothesisId: 'C'});
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
            logDebug('useMatrixWorker.js:75', 'TIMETABLE state updated', {duration: Date.now() - timetableReceivedStart, hypothesisId: 'C'});
            // #endregion
            break
            
          case WORKER_RESPONSE_TYPES.ROWS:
            // Handle row requests (for virtualization)
            break
            
          case WORKER_RESPONSE_TYPES.ERROR:
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
    
    // Reset data initialized flag when reinitializing
    setDataInitialized(false)
    requestManager.reset() // Clear all active requests on reinit
    
    const message = createWorkerMessage(WORKER_MESSAGE_TYPES.INIT_DATA, { data })
    workerRef.current.postMessage(message)
  }, [requestManager])
  
  // Filter data
  const filter = useCallback((streamFilters, streamId, returnRows = false, sortIndices = true, showFilteredDays = true) => {
    // #region agent log
    const filterStart = Date.now();
    logDebug('useMatrixWorker.js:118', 'Filter function called', {streamId, returnRows, sortIndices, showFilteredDays, workerReady, hasWorker: !!workerRef.current, hypothesisId: 'A'});
    // #endregion
    if (!workerRef.current || !workerReady) {
      // #region agent log
      logDebug('useMatrixWorker.js:119', 'Filter skipped - worker not ready', {hypothesisId: 'A'});
      // #endregion
      return
    }
    
    // Clear previous filter outputs to prevent stale data from being displayed
    // This ensures the UI doesn't show data from a previous tab while filtering
    setFilteredLength(0)
    setFilteredIndices([])
    setFilteredRows([])
    
    // Generate request ID and track active request
    const requestId = requestManager.nextRequestId()
    requestManager.setActiveRequest(OPERATIONS.FILTER, requestId)
    
    // #region agent log
    const postMessageStart = Date.now();
    logDebug('useMatrixWorker.js:122', 'Posting FILTER message to worker', {streamId, requestId, showFilteredDays, hypothesisId: 'A'});
    // #endregion
    const message = createWorkerMessage(WORKER_MESSAGE_TYPES.FILTER, {
      streamFilters,
      streamId,
      returnRows,
      sortIndices,
      showFilteredDays,
      requestId
    })
    workerRef.current.postMessage(message)
    // #region agent log
    logDebug('useMatrixWorker.js:125', 'FILTER message posted', {duration: Date.now() - postMessageStart, totalDuration: Date.now() - filterStart, requestId, hypothesisId: 'A'});
    // #endregion
  }, [workerReady, requestManager])
  
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
    
    // Generate request ID and track active request
    const requestId = requestManager.nextRequestId()
    requestManager.setActiveRequest(OPERATIONS.STATS, requestId)
    
    setStatsLoading(true)
    // #region agent log
    const postMessageStart = Date.now();
    logDebug('useMatrixWorker.js:133', 'Posting CALCULATE_STATS message to worker', {streamId, requestId, hypothesisId: 'A'});
    // #endregion
    const message = createWorkerMessage(WORKER_MESSAGE_TYPES.CALCULATE_STATS, {
      streamFilters,
      streamId,
      contractMultiplier,
      contractValues: CONTRACT_VALUES,
      includeFilteredExecuted,
      requestId
    })
    workerRef.current.postMessage(message)
    // #region agent log
    logDebug('useMatrixWorker.js:142', 'CALCULATE_STATS message posted', {duration: Date.now() - postMessageStart, totalDuration: Date.now() - statsStart, requestId, hypothesisId: 'A'});
    // #endregion
  }, [workerReady, requestManager])
  
  // Get rows by indices (for virtualization)
  const getRows = useCallback((indices, callback) => {
    if (!workerRef.current || !workerReady) return
    
    const handler = (e) => {
      if (e.data.type === WORKER_RESPONSE_TYPES.ROWS) {
        callback(e.data.payload.rows)
        workerRef.current.removeEventListener('message', handler)
      }
    }
    
    workerRef.current.addEventListener('message', handler)
    const message = createWorkerMessage(WORKER_MESSAGE_TYPES.GET_ROWS, { indices })
    workerRef.current.postMessage(message)
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
    requestManager.clearLenientKeys(OPERATIONS.BREAKDOWN, baseBreakdownType)
    
    // Generate request ID and track active request with lenient matching
    const requestId = requestManager.nextRequestId()
    requestManager.setActiveRequest(OPERATIONS.BREAKDOWN, requestId, breakdownType)
    
    setBreakdownLoading(true)
    // #region agent log
    const postMessageStart = Date.now();
    logDebug('useMatrixWorker.js:168', 'Posting CALCULATE_PROFIT_BREAKDOWN message to worker', {breakdownType, requestId, hypothesisId: 'B'});
    // #endregion
    const message = createWorkerMessage(WORKER_MESSAGE_TYPES.CALCULATE_PROFIT_BREAKDOWN, {
      streamFilters,
      streamId,
      contractMultiplier,
      contractValues: CONTRACT_VALUES,
      breakdownType,
      useFiltered,
      requestId
    })
    workerRef.current.postMessage(message)
    // #region agent log
    logDebug('useMatrixWorker.js:178', 'CALCULATE_PROFIT_BREAKDOWN message posted', {duration: Date.now() - postMessageStart, totalDuration: Date.now() - breakdownStart, requestId, hypothesisId: 'B'});
    // #endregion
  }, [workerReady, requestManager])
  
  // Calculate timetable
  const calculateTimetable = useCallback((streamFilters, currentTradingDay) => {
    // #region agent log
    const timetableStart = Date.now();
    logDebug('useMatrixWorker.js:181', 'CalculateTimetable function called', {workerReady, hasWorker: !!workerRef.current, dataInitialized, hypothesisId: 'C'});
    // #endregion
    if (!workerRef.current || !workerReady) {
      // #region agent log
      logDebug('useMatrixWorker.js:182', 'CalculateTimetable skipped - worker not ready', {hypothesisId: 'C'});
      // #endregion
      return
    }
    
    // Ensure data is initialized before calculating timetable
    if (!dataInitialized) {
      // #region agent log
      logDebug('useMatrixWorker.js:182', 'CalculateTimetable skipped - data not initialized', {hypothesisId: 'C'});
      // #endregion
      console.warn('Cannot calculate timetable: worker data not initialized yet')
      return
    }
    
    // Generate request ID and track active request
    const requestId = requestManager.nextRequestId()
    requestManager.setActiveRequest(OPERATIONS.TIMETABLE, requestId)
    
    setTimetableLoading(true)
    // #region agent log
    const postMessageStart = Date.now();
    logDebug('useMatrixWorker.js:186', 'Posting CALCULATE_TIMETABLE message to worker', {requestId, hypothesisId: 'C'});
    // #endregion
    const message = createWorkerMessage(WORKER_MESSAGE_TYPES.CALCULATE_TIMETABLE, {
      streamFilters,
      currentTradingDay: currentTradingDay ? currentTradingDay.toISOString().split('T')[0] : null,
      requestId
    })
    workerRef.current.postMessage(message)
    // #region agent log
    logDebug('useMatrixWorker.js:192', 'CALCULATE_TIMETABLE message posted', {duration: Date.now() - postMessageStart, totalDuration: Date.now() - timetableStart, requestId, hypothesisId: 'C'});
    // #endregion
  }, [workerReady, dataInitialized, requestManager])
  
  return {
    workerReady,
    filteredLength,
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

