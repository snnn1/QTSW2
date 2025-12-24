// Hook for managing Matrix Web Worker
import { useState, useEffect, useRef, useCallback } from 'react'

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
  const [error, setError] = useState(null)
  
  // Initialize worker
  useEffect(() => {
    let worker = null
    try {
      worker = new Worker(new URL('./matrixWorker.js', import.meta.url), { type: 'module' })
      workerRef.current = worker
      
      worker.onmessage = (e) => {
        const { type, payload } = e.data
        
        switch (type) {
          case 'DATA_INITIALIZED':
            setWorkerReady(true)
            setError(null)
            break
            
          case 'FILTERED':
            setFilteredLength(payload.length)
            setFilterMask(payload.mask)
            setFilteredIndices(payload.indices || [])
            if (payload.rows) {
              setFilteredRows(payload.rows)
            }
            break
            
          case 'STATS':
            setStats(payload.stats)
            setStatsLoading(false)
            break
            
          case 'PROFIT_BREAKDOWN':
            setProfitBreakdown(payload.breakdown)
            setBreakdownType(payload.breakdownType)
            setBreakdownLoading(false)
            break
            
          case 'TIMETABLE':
            setTimetable(payload.timetable || [])
            setTimetableLoading(false)
            break
            
          case 'ROWS':
            // Handle row requests (for virtualization)
            break
            
          case 'ERROR':
            setError(payload.message)
            setTimetableLoading(false) // Stop loading on error
            console.error('Worker error:', payload)
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
    if (!workerRef.current || !workerReady) return
    
    workerRef.current.postMessage({
      type: 'FILTER',
      payload: { streamFilters, streamId, returnRows, sortIndices }
    })
  }, [workerReady])
  
  // Calculate stats
  const calculateStats = useCallback((streamFilters, streamId, contractMultiplier) => {
    if (!workerRef.current || !workerReady) return
    
    setStatsLoading(true)
    workerRef.current.postMessage({
      type: 'CALCULATE_STATS',
      payload: {
        streamFilters,
        streamId,
        contractMultiplier,
        contractValues: CONTRACT_VALUES
      }
    })
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
    if (!workerRef.current || !workerReady) return
    
    setBreakdownLoading(true)
    workerRef.current.postMessage({
      type: 'CALCULATE_PROFIT_BREAKDOWN',
      payload: {
        streamFilters,
        streamId,
        contractMultiplier,
        contractValues: CONTRACT_VALUES,
        breakdownType,
        useFiltered
      }
    })
  }, [workerReady])
  
  // Calculate timetable
  const calculateTimetable = useCallback((streamFilters, currentTradingDay) => {
    if (!workerRef.current || !workerReady) return
    
    setTimetableLoading(true)
    workerRef.current.postMessage({
      type: 'CALCULATE_TIMETABLE',
      payload: {
        streamFilters,
        currentTradingDay: currentTradingDay ? currentTradingDay.toISOString().split('T')[0] : null
      }
    })
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
    error,
    initData,
    filter,
    calculateStats,
    getRows,
    calculateProfitBreakdown,
    calculateTimetable
  }
}

