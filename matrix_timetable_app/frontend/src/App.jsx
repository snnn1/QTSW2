import { useState, useEffect, useRef, useCallback } from 'react'
import './App.css'

const API_BASE = 'http://localhost:8000/api'

const STREAMS = ['ES1', 'ES2', 'GC1', 'GC2', 'CL1', 'CL2', 'NQ1', 'NQ2', 'NG1', 'NG2', 'YM1', 'YM2']
const DAYS_OF_WEEK = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']
const AVAILABLE_TIMES = ['07:30', '08:00', '09:00', '09:30', '10:00', '10:30', '11:00']
const DEFAULT_COLUMNS = ['Date', 'Day of Week', 'Stream', 'Time', 'Target', 'Range', 'SL', 'Profit', 'Peak', 'Direction', 'Result', 'Time Change', 'Profit ($)']

function App() {
  const [activeTab, setActiveTab] = useState('master') // 'master' or stream ID
  
  // Master matrix data
  const [masterData, setMasterData] = useState([])
  const [masterLoading, setMasterLoading] = useState(false)
  const [masterError, setMasterError] = useState(null)
  const [availableYearsFromAPI, setAvailableYearsFromAPI] = useState([])
  
  // Available columns (detected from data)
  const [availableColumns, setAvailableColumns] = useState([])
  
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
  
  // Save stats visibility to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_show_stats', JSON.stringify(showStats))
  }, [showStats])
  
  // Save contract multiplier to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('matrix_master_contract_multiplier', masterContractMultiplier.toString())
  }, [masterContractMultiplier])
  
  // Calculate stats for a stream from filtered data
  const calculateStats = (streamId) => {
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
    
    // Helper function to get contract value for a trade
    const getContractValue = (trade) => {
      const symbol = (trade.Symbol || trade.Instrument || 'ES').toUpperCase()
      // Extract base symbol (ES1 -> ES, NQ2 -> NQ, etc.) or use as-is if no number
      const baseSymbol = symbol.replace(/\d+$/, '') || symbol
      return contractValues[baseSymbol] || 50 // Default to ES if unknown
    }
    
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
      worstTrade
    }
  }
  
  const toggleStats = (streamId) => {
    setShowStats(prev => ({
      ...prev,
      [streamId]: !prev[streamId]
    }))
  }
  
  const loadMasterMatrix = async (rebuild = false, rebuildStream = null) => {
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
          // Include filters even if arrays are empty (to ensure all streams are represented)
          // But only include if the stream has filter configuration
          if (filters) {
            streamFiltersApi[streamId] = {
              exclude_days_of_week: filters.exclude_days_of_week || [],
              exclude_days_of_month: filters.exclude_days_of_month || [],
              exclude_times: filters.exclude_times || []
            }
            // Debug logging
            if (filters.exclude_times && filters.exclude_times.length > 0) {
              console.log(`[DEBUG] Stream ${streamId} exclude_times:`, filters.exclude_times)
            }
          }
        })
        
        console.log('[DEBUG] Sending stream_filters to API:', streamFiltersApi)
        
        // If rebuilding a specific stream, only rebuild that stream
        // Otherwise rebuild all streams (master tab)
        const buildBody = {
          stream_filters: Object.keys(streamFiltersApi).length > 0 ? streamFiltersApi : null
        }
        if (rebuildStream) {
          buildBody.streams = [rebuildStream]
        }
        
        console.log('[DEBUG] Build request body:', JSON.stringify(buildBody, null, 2))
        console.log('[DEBUG] ES1 filters in request:', buildBody.stream_filters?.ES1)
        
        console.log('[DEBUG] About to send build request to:', `${API_BASE}/matrix/build`)
        try {
          const buildResponse = await fetch(`${API_BASE}/matrix/build`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(buildBody)
          })
          
          console.log('[DEBUG] Build response status:', buildResponse.status)
          
          if (!buildResponse.ok) {
            const errorData = await buildResponse.json()
            console.error('[DEBUG] Build failed:', errorData)
            setMasterError(errorData.detail || 'Failed to build master matrix')
            setMasterLoading(false)
            return
          }
          
          const buildResult = await buildResponse.json()
          console.log('[DEBUG] Build result:', buildResult)
        } catch (error) {
          console.error('[DEBUG] Build request error:', error)
          setMasterError(`Failed to build master matrix: ${error.message}`)
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
      const trades = data.data || []
      
      // Store available years from API response
      if (data.years && Array.isArray(data.years) && data.years.length > 0) {
        setAvailableYearsFromAPI(data.years)
        console.log('[DEBUG] Available years from API:', data.years)
      } else {
        console.warn('[DEBUG] No years field in API response or empty array')
      }
      
      // Debug: Check if Time Change column exists in the data
      if (trades.length > 0 && 'Time Change' in trades[0]) {
        const timeChangeCount = trades.filter(t => t['Time Change'] && t['Time Change'].trim() !== '').length
        console.log(`[DEBUG] Time Change column found in ${timeChangeCount} of ${trades.length} trades`)
      } else if (trades.length > 0) {
        console.log('[DEBUG] Time Change column NOT found in data. Available columns:', Object.keys(trades[0]).slice(0, 20))
      }
      
      if (trades.length > 0) {
        setMasterData(trades)
        
        // Detect available columns from first row
        if (availableColumns.length === 0) {
          const cols = Object.keys(trades[0])
          // Filter out internal/technical columns that shouldn't be displayed
          const hiddenColumns = [
            'global_trade_id',
            'filter_reasons',
            'onr_high',
            'onr_low',
            'onr',
            'scf_s1',
            'scf_s2',
            'prewindow_high_s1',
            'prewindow_low_s1',
            'prewindow_range_s1',
            'session_high_s1',
            'session_low_s1',
            'session_range_s1',
            'prewindow_high_s2',
            'prewindow_low_s2',
            'prewindow_range_s2',
            'session_high_s2',
            'session_low_s2',
            'session_range_s2',
            'onr_q1',
            'onr_q2',
            'onr_q3',
            'onr_bucket',
            'entry_time',
            'exit_time',
            'entry_price',
            'exit_price',
            'R',
            'pnl',
            'rs_value',
            'selected_time',
            'time_bucket',
            'trade_date',
            'day_of_month',
            'dow',
            'dow_full',
            'month',
            'session_index',
            'is_two_stream',
            'dom_blocked',
            'final_allowed'
          ]
          // Keep time slot rolling and points columns (e.g., "07:30 Rolling", "08:00 Points")
          // These should be included in the column selector
          // Filter out internal columns, but explicitly include time slot columns
          const displayableCols = cols.filter(col => {
            // Always include time slot Points and Rolling columns
            if (col.includes(' Points') || col.includes(' Rolling')) {
              return true
            }
            // Filter out internal columns
            return !col.startsWith('_') && !hiddenColumns.includes(col)
          })
          
          // Add computed dollar columns if Profit column exists
          if (displayableCols.includes('Profit')) {
            if (!displayableCols.includes('Profit ($)')) {
              displayableCols.push('Profit ($)')
            }
          }
          
          // Ensure default columns are always available (even if not in data yet)
          // This is important for columns like SL that are calculated but might not be in old files
          DEFAULT_COLUMNS.forEach(col => {
            if (!displayableCols.includes(col)) {
              displayableCols.push(col)
            }
          })
          
          // Explicitly exclude "Revised Score" and "Revised Profit ($)" from default selection
          // These columns may exist in data but should not be shown by default
          const excludedFromDefault = ['Revised Score', 'Revised Profit ($)']
          
          // Required columns that must always be included by default
          const requiredDefaultColumns = ['Day of Week', 'Profit', 'Profit ($)', 'Time Change']
          
          setAvailableColumns(displayableCols)
          
          // Initialize column selections for all streams if not set
          setSelectedColumns(prev => {
            const updated = { ...prev }
            let changed = false
            
            // Helper function to ensure required columns are included
            const ensureRequiredColumns = (cols) => {
              const filtered = cols.filter(col => !excludedFromDefault.includes(col))
              // Add any missing required columns
              requiredDefaultColumns.forEach(reqCol => {
                if (!filtered.includes(reqCol) && displayableCols.includes(reqCol)) {
                  filtered.push(reqCol)
                }
              })
              return sortColumnsByDefaultOrder(filtered)
            }
            
            // Initialize Master tab - filter out excluded columns, ensure required columns, and sort
            if (!updated['master'] || updated['master'].length === 0) {
              updated['master'] = ensureRequiredColumns(DEFAULT_COLUMNS)
              changed = true
            } else {
              // Remove excluded columns, ensure required columns, and sort
              const masterCols = ensureRequiredColumns(updated['master'])
              if (JSON.stringify(masterCols) !== JSON.stringify(updated['master'])) {
                updated['master'] = masterCols
                changed = true
              }
            }
            
            // Initialize each stream tab - filter out excluded columns, ensure required columns, and sort
            STREAMS.forEach(stream => {
              if (!updated[stream] || updated[stream].length === 0) {
                updated[stream] = ensureRequiredColumns(DEFAULT_COLUMNS)
                changed = true
              } else {
                // Remove excluded columns, ensure required columns, and sort
                const streamCols = ensureRequiredColumns(updated[stream])
                if (JSON.stringify(streamCols) !== JSON.stringify(updated[stream])) {
                  updated[stream] = streamCols
                  changed = true
                }
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
    console.log(`[DEBUG] updateStreamFilter called: streamId=${streamId}, filterType=${filterType}, value=${value}`)
    console.log(`[DEBUG] updateStreamFilter: current streamFilters state =`, streamFilters)
    setStreamFilters(prev => {
      console.log(`[DEBUG] updateStreamFilter: prev state =`, prev)
      const updated = { ...prev }
      if (!updated[streamId]) {
        updated[streamId] = {
          exclude_days_of_week: [],
          exclude_days_of_month: [],
          exclude_times: [],
          include_years: []
        }
      }
      
      // Create a new filter object for this stream
      const currentFilters = { ...updated[streamId] }
      console.log(`[DEBUG] updateStreamFilter: currentFilters before update =`, currentFilters)
      
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
          console.log(`[DEBUG] Removed exclude_times filter for ${streamId}: ${value}. New filters:`, currentFilters.exclude_times)
        } else {
          // Add filter
          currentFilters.exclude_times = [...current, value]
          console.log(`[DEBUG] Added exclude_times filter for ${streamId}: ${value}. New filters:`, currentFilters.exclude_times)
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
      console.log(`[DEBUG] updateStreamFilter: new state =`, newState)
      console.log(`[DEBUG] updateStreamFilter: new filters for ${streamId} =`, currentFilters)
      return newState
    })
  }
  
  const getFilteredData = (data, streamId = null) => {
    let filtered = [...data]
    
    // Filter by stream if specified (but not for 'master' which shows all streams)
    if (streamId && streamId !== 'master') {
      filtered = filtered.filter(row => row.Stream === streamId)
    }
    
    // Apply day filters on frontend (for immediate visual feedback)
    // NOTE: Time filters are NOT applied client-side - they only affect sequencer logic during rebuild
    // When a time is excluded, the sequencer won't consider it when choosing which time slot to use,
    // but the resulting trades (at the chosen times) will still be visible
    // Year filter is also applied on frontend (for display purposes)
    
    if (streamId && streamId !== 'master') {
      // Individual stream tab - apply its filters
      if (streamFilters[streamId]) {
        const filters = streamFilters[streamId]
        
        // NOTE: Time filters are NOT applied here - they only affect sequencer logic during rebuild
        // Excluding a time means the sequencer won't consider it when choosing which time slot to use
        
        // Day of week filter (frontend filtering for immediate visual feedback)
        if (filters.exclude_days_of_week && filters.exclude_days_of_week.length > 0) {
          filtered = filtered.filter(row => {
            if (!row.Date) return true
            try {
              const date = new Date(row.Date)
              if (isNaN(date.getTime())) return true
              const dayName = date.toLocaleDateString('en-US', { weekday: 'long' })
              return !filters.exclude_days_of_week.includes(dayName)
            } catch {
              return true
            }
          })
        }
        
        // Day of month filter (frontend filtering for immediate visual feedback)
        if (filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0) {
          filtered = filtered.filter(row => {
            if (!row.Date) return true
            try {
              const date = new Date(row.Date)
              if (isNaN(date.getTime())) return true
              return !filters.exclude_days_of_month.includes(date.getDate())
            } catch {
              return true
            }
          })
        }
        
        // Year filter (for display purposes, doesn't require rebuild)
        if (filters.include_years && filters.include_years.length > 0) {
          filtered = filtered.filter(row => {
            // Check both Date and trade_date fields
            const dateValue = row.Date || row.trade_date
            if (!dateValue) return false
            try {
              const date = new Date(dateValue)
              if (!isNaN(date.getTime())) {
                const year = date.getFullYear()
                return filters.include_years.includes(year)
              }
              // Try to extract year from string formats
              if (typeof dateValue === 'string') {
                // Try YYYY-MM-DD format
                const isoMatch = dateValue.match(/^(\d{4})-\d{2}-\d{2}/)
                if (isoMatch) {
                  return filters.include_years.includes(parseInt(isoMatch[1]))
                }
                // Try MM/DD/YYYY or DD/MM/YYYY format
                const slashMatch = dateValue.match(/(\d{4})/)
                if (slashMatch) {
                  return filters.include_years.includes(parseInt(slashMatch[1]))
                }
              }
              return false
            } catch {
              return false
            }
          })
        }
      }
    } else if (streamId === 'master') {
      // Master tab - apply each stream's filters to its own trades
      // This ensures master stats reflect all the filters from individual streams
      filtered = filtered.filter(row => {
        const rowStream = row.Stream
        if (!rowStream || !streamFilters[rowStream]) {
          return true // No filters for this stream, show all
        }
        
        const filters = streamFilters[rowStream]
        const dateValue = row.Date || row.trade_date
        
        // Day of week filter
        if (filters.exclude_days_of_week && filters.exclude_days_of_week.length > 0) {
          if (dateValue) {
            try {
              const date = new Date(dateValue)
              if (!isNaN(date.getTime())) {
                const dayName = date.toLocaleDateString('en-US', { weekday: 'long' })
                if (filters.exclude_days_of_week.includes(dayName)) {
                  return false
                }
              }
            } catch {
              // Continue if date parsing fails
            }
          }
        }
        
        // Day of month filter
        if (filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0) {
          if (dateValue) {
            try {
              const date = new Date(dateValue)
              if (!isNaN(date.getTime())) {
                if (filters.exclude_days_of_month.includes(date.getDate())) {
                  return false
                }
              }
            } catch {
              // Continue if date parsing fails
            }
          }
        }
        
        // Year filter
        if (filters.include_years && filters.include_years.length > 0) {
          if (!dateValue) return false
          try {
            const date = new Date(dateValue)
            if (!isNaN(date.getTime())) {
              const year = date.getFullYear()
              if (!filters.include_years.includes(year)) {
                return false
              }
            } else {
              // Try to extract year from string formats
              if (typeof dateValue === 'string') {
                const isoMatch = dateValue.match(/^(\d{4})-\d{2}-\d{2}/)
                if (isoMatch) {
                  if (!filters.include_years.includes(parseInt(isoMatch[1]))) {
                    return false
                  }
                } else {
                  const slashMatch = dateValue.match(/(\d{4})/)
                  if (slashMatch) {
                    if (!filters.include_years.includes(parseInt(slashMatch[1]))) {
                      return false
                    }
                  } else {
                    return false
                  }
                }
              } else {
                return false
              }
            }
          } catch {
            return false
          }
        }
        
        return true // Passed all filters for this stream
      })
      
      // Also apply master-specific filters if they exist (on top of stream filters)
      const masterFilters = streamFilters['master'] || {}
      
      // Master day of week filter (additional filtering)
      if (masterFilters.exclude_days_of_week && masterFilters.exclude_days_of_week.length > 0) {
        filtered = filtered.filter(row => {
          const dateValue = row.Date || row.trade_date
          if (!dateValue) return true
          try {
            const date = new Date(dateValue)
            if (isNaN(date.getTime())) return true
            const dayName = date.toLocaleDateString('en-US', { weekday: 'long' })
            return !masterFilters.exclude_days_of_week.includes(dayName)
          } catch {
            return true
          }
        })
      }
      
      // Master day of month filter (additional filtering)
      if (masterFilters.exclude_days_of_month && masterFilters.exclude_days_of_month.length > 0) {
        filtered = filtered.filter(row => {
          const dateValue = row.Date || row.trade_date
          if (!dateValue) return true
          try {
            const date = new Date(dateValue)
            if (isNaN(date.getTime())) return true
            return !masterFilters.exclude_days_of_month.includes(date.getDate())
          } catch {
            return true
          }
        })
      }
      
      // Master year filter (additional filtering)
      if (masterFilters.include_years && masterFilters.include_years.length > 0) {
        filtered = filtered.filter(row => {
          const dateValue = row.Date || row.trade_date
          if (!dateValue) return false
          try {
            const date = new Date(dateValue)
            if (!isNaN(date.getTime())) {
              const year = date.getFullYear()
              return masterFilters.include_years.includes(year)
            }
            if (typeof dateValue === 'string') {
              const isoMatch = dateValue.match(/^(\d{4})-\d{2}-\d{2}/)
              if (isoMatch) {
                return masterFilters.include_years.includes(parseInt(isoMatch[1]))
              }
              const slashMatch = dateValue.match(/(\d{4})/)
              if (slashMatch) {
                return masterFilters.include_years.includes(parseInt(slashMatch[1]))
              }
            }
            return false
          } catch {
            return false
          }
        })
      }
    } else {
      // Master tab (no streamId) - apply each stream's filters to its own rows
      filtered = filtered.filter(row => {
        const rowStream = row.Stream
        if (!rowStream || !streamFilters[rowStream]) {
          return true // No filters for this stream, show all
        }
        
        const filters = streamFilters[rowStream]
        
        // NOTE: Time filters are NOT applied here - they only affect sequencer logic during rebuild
        // Excluding a time means the sequencer won't consider it when choosing which time slot to use
        
        // Day of week filter (frontend filtering for immediate visual feedback)
        if (filters.exclude_days_of_week && filters.exclude_days_of_week.length > 0) {
          if (row.Date) {
            try {
              const date = new Date(row.Date)
              if (!isNaN(date.getTime())) {
                const dayName = date.toLocaleDateString('en-US', { weekday: 'long' })
                if (filters.exclude_days_of_week.includes(dayName)) {
                  return false
                }
              }
            } catch {
              // Continue if date parsing fails
            }
          }
        }
        
        // Day of month filter (frontend filtering for immediate visual feedback)
        if (filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0) {
          if (row.Date) {
            try {
              const date = new Date(row.Date)
              if (!isNaN(date.getTime())) {
                if (filters.exclude_days_of_month.includes(date.getDate())) {
                  return false
                }
              }
            } catch {
              // Continue if date parsing fails
            }
          }
        }
        
        // Year filter (for display purposes)
        if (filters.include_years && filters.include_years.length > 0) {
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
        }
        
        return true
      })
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
  
  const getStreamFilters = (streamId) => {
    const defaultFilters = {
      exclude_days_of_week: [],
      exclude_days_of_month: [],
      exclude_times: [],
      include_years: [] // Empty array means all years
    }
    // For 'master', return default filters (no stream-specific filters applied)
    if (streamId === 'master' || !streamId) {
      return defaultFilters
    }
    return streamFilters[streamId] ? { ...defaultFilters, ...streamFilters[streamId] } : defaultFilters
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
  
  const renderStats = (streamId) => {
    const stats = calculateStats(streamId)
    console.log(`[DEBUG] renderStats for ${streamId}:`, { stats, hasStats: !!stats })
    if (!stats) {
      // Debug why stats are null
      const filtered = getFilteredData(masterData, streamId)
      console.log(`[DEBUG] No stats - filtered data length:`, filtered.length)
      if (filtered.length > 0) {
        console.log(`[DEBUG] Sample row:`, filtered[0])
        console.log(`[DEBUG] Has Result column:`, 'Result' in (filtered[0] || {}))
        console.log(`[DEBUG] Has Profit column:`, 'Profit' in (filtered[0] || {}))
      }
      return (
        <div className="bg-gray-800 rounded-lg p-4 mb-4">
          <p className="text-gray-400 text-sm">No data available for statistics</p>
          {filtered.length === 0 && (
            <p className="text-gray-500 text-xs mt-2">No filtered data found for {streamId}</p>
          )}
        </div>
      )
    }
    
    return (
      <div className="bg-gray-800 rounded-lg p-4 mb-4 border border-gray-700">
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
          <div>
            <div className="text-xs text-gray-400 mb-1">Total Trades</div>
            <div className="text-lg font-semibold">{stats.totalTrades}</div>
          </div>
          <div>
            <div className="text-xs text-gray-400 mb-1">Total Days</div>
            <div className="text-lg font-semibold">{stats.totalDays}</div>
          </div>
          {streamId === 'master' && (
            <div>
              <div className="text-xs text-gray-400 mb-1">Avg Trades/Day</div>
              <div className="text-lg font-semibold">{stats.avgTradesPerDay}</div>
            </div>
          )}
          <div>
            <div className="text-xs text-gray-400 mb-1">Win Rate</div>
            <div className="text-lg font-semibold text-green-400">{stats.winRate}%</div>
          </div>
          {streamId !== 'master' && (
            <div>
              <div className="text-xs text-gray-400 mb-1">Total Profit</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.totalProfit) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.totalProfit}
              </div>
            </div>
          )}
          <div>
            <div className="text-xs text-gray-400 mb-1">Total Profit ($)</div>
            <div className={`text-lg font-semibold ${parseFloat(stats.totalProfit) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
              {stats.totalProfitDollars}
            </div>
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
          {streamId !== 'master' && (
            <div>
              <div className="text-xs text-gray-400 mb-1">Final Time</div>
              <div className="text-lg font-semibold">{stats.finalTime}</div>
            </div>
          )}
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
          {streamId === 'master' ? (
            <div>
              <div className="text-xs text-gray-400 mb-1">Max Drawdown ($)</div>
              <div className="text-lg font-semibold text-red-400">{stats.maxDrawdownDollars}</div>
            </div>
          ) : (
            <>
              <div>
                <div className="text-xs text-gray-400 mb-1">Max Drawdown</div>
                <div className="text-lg font-semibold text-red-400">{stats.maxDrawdown}</div>
              </div>
              <div>
                <div className="text-xs text-gray-400 mb-1">Max Drawdown ($)</div>
                <div className="text-lg font-semibold text-red-400">{stats.maxDrawdownDollars}</div>
              </div>
            </>
          )}
        </div>
      </div>
    )
  }
  
  const renderFilters = (streamId) => {
    // Get filters directly from state to ensure we have the latest values
    const filters = streamFilters[streamId] || {
      exclude_days_of_week: [],
      exclude_days_of_month: [],
      exclude_times: [],
      include_years: []
    }
    console.log(`[DEBUG] renderFilters for ${streamId}: streamFilters state =`, streamFilters)
    console.log(`[DEBUG] renderFilters for ${streamId}: filters =`, filters)
    const availableYears = getAvailableYears()
    const hasFilters = (filters.exclude_days_of_week && filters.exclude_days_of_week.length > 0) || 
                      (filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0) || 
                      (filters.exclude_times && filters.exclude_times.length > 0) ||
                      (filters.include_years && filters.include_years.length > 0)
    
    return (
      <div className="bg-gray-700 rounded-lg p-4 mb-4">
        <div className="flex items-center justify-between mb-3">
          <h4 className="font-medium text-sm">Filters for {streamId}</h4>
          {hasFilters && (
            <span className="text-xs bg-blue-600 px-2 py-1 rounded">Active</span>
          )}
        </div>
        
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          {/* Years Filter */}
          <div>
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
                          : 'bg-gray-600 hover:bg-gray-500'
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
          <div>
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
                      console.log('Day of week clicked:', dow, 'Stream:', streamId)
                      updateStreamFilter(streamId, 'exclude_days_of_week', dow)
                    }}
                    className={`px-2 py-1 text-xs rounded cursor-pointer transition-colors ${
                      isExcluded
                        ? 'bg-red-600 text-white'
                        : 'bg-gray-600 hover:bg-gray-500'
                    }`}
                  >
                    {dow.substring(0, 3)}
                  </button>
                )
              })}
            </div>
          </div>
          
          {/* Days of Month */}
          <div className="relative dom-dropdown-container">
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
              className="w-full px-2 py-1 text-xs bg-gray-600 border border-gray-500 rounded text-gray-100 text-left flex items-center justify-between hover:bg-gray-500"
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
                className="absolute z-10 w-full mt-1 bg-gray-700 border border-gray-500 rounded shadow-lg max-h-48 overflow-y-auto"
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
                          console.log('DOM clicked:', day, 'Stream:', streamId)
                          updateStreamFilter(streamId, 'exclude_days_of_month', day)
                        }}
                        className={`px-2 py-1 text-xs rounded cursor-pointer text-center transition-colors ${
                          isSelected
                            ? 'bg-red-600 text-white'
                            : 'bg-gray-600 text-gray-200 hover:bg-gray-500'
                        }`}
                      >
                        <input
                          type="checkbox"
                          checked={isSelected || false}
                          onChange={() => {}}
                          onClick={(e) => {
                            e.preventDefault()
                            e.stopPropagation()
                            console.log('DOM checkbox clicked:', day, 'Stream:', streamId)
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
          <div>
            <label className="block text-xs font-medium mb-2 text-gray-400">Exclude Times</label>
            <div className="flex flex-wrap gap-1">
              {/* Only show relevant times for this stream */}
              {(() => {
                const relevantTimes = getRelevantTimeSlots(streamId) || AVAILABLE_TIMES
                const excludeTimes = filters.exclude_times || []
                console.log(`[DEBUG] Filter button render for ${streamId}: exclude_times =`, excludeTimes)
                return relevantTimes.map(time => {
                  const isExcluded = excludeTimes.includes(time)
                  return (
                    <button
                      key={time}
                      type="button"
                      onClick={(e) => {
                        e.preventDefault()
                        e.stopPropagation()
                        console.log(`[DEBUG] Filter button clicked for ${streamId}: ${time}, current exclude_times:`, excludeTimes)
                        updateStreamFilter(streamId, 'exclude_times', time)
                      }}
                      className={`px-2 py-1 text-xs rounded font-mono cursor-pointer transition-colors ${
                        isExcluded
                          ? 'bg-red-600 text-white'
                          : 'bg-gray-600 hover:bg-gray-500'
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
  
  // Sort columns to maintain DEFAULT_COLUMNS order, with extra columns at the end
  const sortColumnsByDefaultOrder = (columns) => {
    const sorted = []
    const extras = []
    
    // First, add columns in DEFAULT_COLUMNS order
    DEFAULT_COLUMNS.forEach(defaultCol => {
      if (columns.includes(defaultCol)) {
        sorted.push(defaultCol)
      }
    })
    
    // Then add any columns not in DEFAULT_COLUMNS at the end
    columns.forEach(col => {
      if (!DEFAULT_COLUMNS.includes(col) && !sorted.includes(col)) {
        extras.push(col)
      }
    })
    
    return [...sorted, ...extras]
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
      <div className="mb-4 bg-gray-800 rounded-lg p-4 border border-gray-700">
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-lg font-semibold">Select Columns</h3>
          <button
            onClick={() => setShowColumnSelector(false)}
            className="text-gray-400 hover:text-gray-300"
          >
            ✕
          </button>
        </div>
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-2 max-h-64 overflow-y-auto">
          {sortedColumns.map(col => {
            const currentCols = getSelectedColumnsForTab(activeTab)
            return (
              <label key={col} className="flex items-center space-x-2 cursor-pointer hover:bg-gray-700 p-2 rounded">
                <input
                  type="checkbox"
                  checked={currentCols.includes(col)}
                  onChange={() => toggleColumn(col)}
                  className="w-4 h-4 text-blue-600 bg-gray-700 border-gray-600 rounded focus:ring-blue-500"
                />
                <span className="text-sm text-gray-300">{col}</span>
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
            className="px-3 py-1 text-sm bg-gray-700 hover:bg-gray-600 rounded"
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
            className="px-3 py-1 text-sm bg-gray-700 hover:bg-gray-600 rounded"
          >
            Select All
          </button>
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
    
    // Use selected columns for the current tab, fallback to default if none selected
    const tabId = streamId || 'master'
    let columnsToShow = getSelectedColumnsForTab(tabId)
    
    // Filter columns based on stream (only show relevant time slot columns)
    columnsToShow = getFilteredColumns(columnsToShow, streamId)
    
    return (
      <div className="overflow-x-auto">
        <table className="w-full text-sm border-collapse">
          <thead>
            <tr className="bg-gray-800">
              {columnsToShow.map(col => (
                <th key={col} className="text-left p-2 font-medium border border-gray-600">{col}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {visible.map((row, idx) => (
              <tr
                key={idx}
                ref={idx === visible.length - 1 ? lastRowRef : null}
                className={`hover:bg-gray-800 ${
                  row.final_allowed === false ? 'bg-red-900/20' : ''
                }`}
              >
                {columnsToShow.map(col => {
                  let value = row[col]
                  // Handle column name variations
                  if (col === 'Symbol' && !value) {
                    value = row['Instrument'] || ''
                  }
                  // Debug SL column access
                  if (col === 'SL' && idx < 3) {
                    console.log(`Row ${idx} SL access:`, { col, value, rowHasSL: 'SL' in row, rowKeys: Object.keys(row).filter(k => k.includes('SL')) })
                  }
                  if (col === 'Date' && value) {
                    value = new Date(value).toLocaleDateString()
                  }
                  // Day of Week - extract from Date (show 3-letter abbreviation)
                  if (col === 'Day of Week' && row.Date) {
                    try {
                      const date = new Date(row.Date)
                      if (!isNaN(date.getTime())) {
                        value = date.toLocaleDateString('en-US', { weekday: 'short' })
                      } else {
                        value = ''
                      }
                    } catch {
                      value = ''
                    }
                  }
                  // Format numeric columns
                  if (['Profit', 'Peak', 'Target', 'Range', 'SL'].includes(col)) {
                    // Handle SL specially - 0 is valid, null/undefined shows as "-"
                    if (col === 'SL') {
                      // Debug: log first few SL values
                      if (idx < 3) {
                        console.log(`SL value for row ${idx}, col ${col}:`, value, 'type:', typeof value)
                      }
                      // Check if value exists and is not null/undefined
                      if (value === null || value === undefined) {
                        value = '-' // Missing value
                      } else {
                        // Convert to number - handle both string and number types
                        const numValue = typeof value === 'number' ? value : parseFloat(value)
                        // Check for 0 explicitly (0 is falsy but valid)
                        if (numValue === 0 || numValue === '0' || numValue === 0.0) {
                          value = '0.00'
                        } else if (!isNaN(numValue) && isFinite(numValue)) {
                          // Valid number - format to 2 decimal places
                          value = numValue.toFixed(2)
                        } else {
                          // Invalid number or empty string
                          value = '-'
                        }
                      }
                    } else if (value !== null && value !== undefined) {
                      const numValue = parseFloat(value)
                      if (!isNaN(numValue)) {
                        value = numValue.toFixed(2)
                      }
                    }
                  }
                  // Time Change - show the new time if changed, blank otherwise
                  if (col === 'Time Change') {
                    // If value is a time string (e.g., "08:00", "09:30"), show it
                    // Otherwise show blank
                    if (value && typeof value === 'string' && value.trim() !== '') {
                      // Value is already the time (e.g., "08:00"), keep it as is
                      value = value.trim()
                    } else {
                      // Show blank for null/undefined/empty/false/0
                      value = ''
                    }
                  }
                  // Format dollar columns (computed from Profit using contract values)
                  if (col === 'Profit ($)') {
                    const profitValue = parseFloat(row.Profit) || 0
                    // Get contract value based on Symbol/Instrument (same as sequential processor)
                    const symbol = row.Symbol || row.Instrument || 'ES'
                    const contractValues = {
                      'ES': 50,
                      'NQ': 10,
                      'YM': 5,
                      'CL': 1000,
                      'NG': 10000,
                      'GC': 100
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
                  // Format time slot rolling columns (e.g., "07:30 Rolling", "08:00 Rolling")
                  if (col.includes(' Rolling') && value !== null && value !== undefined) {
                    const numValue = parseFloat(value)
                    if (!isNaN(numValue)) {
                      value = numValue.toFixed(2)
                    }
                  }
                  // Format time slot points columns (e.g., "07:30 Points", "08:00 Points")
                  if (col.includes(' Points') && value !== null && value !== undefined) {
                    const numValue = parseFloat(value)
                    if (!isNaN(numValue)) {
                      value = numValue.toFixed(0)
                    }
                  }
                  return (
                    <td key={col} className="p-2 border border-gray-600">
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
                <div className="flex gap-2">
                  <button
                    onClick={() => setShowColumnSelector(!showColumnSelector)}
                    className="px-4 py-2 rounded font-medium text-sm bg-gray-700 hover:bg-gray-600"
                  >
                    {showColumnSelector ? 'Hide' : 'Show'} Columns
                  </button>
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
              </div>
              
              {renderColumnSelector()}
              
              {/* Stats Toggle for Master */}
              <div className="mb-4">
                <button
                  onClick={() => toggleStats('master')}
                  className="flex items-center justify-between w-full px-4 py-2 bg-gray-700 hover:bg-gray-600 rounded text-left"
                >
                  <span className="font-medium">Statistics (All Streams)</span>
                  <span>{showStats['master'] ? '▼' : '▶'}</span>
                </button>
                {showStats['master'] && renderStats('master')}
              </div>
              
              {/* Contract Multiplier for Master */}
              <div className="mb-4 bg-gray-700 rounded-lg p-4">
                <label className="block text-sm font-medium mb-2">
                  Contract Size Multiplier
                </label>
                <div className="flex items-center gap-3">
                  <input
                    type="number"
                    min="0.01"
                    max="100"
                    step="0.01"
                    value={masterContractMultiplier}
                    onChange={(e) => {
                      const value = parseFloat(e.target.value)
                      if (!isNaN(value) && value > 0) {
                        setMasterContractMultiplier(Math.max(0.01, Math.min(100, value)))
                      }
                    }}
                    className="w-24 px-3 py-2 bg-gray-800 border border-gray-600 rounded text-white focus:outline-none focus:border-blue-500"
                  />
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
              <div className="flex items-center justify-between mb-4">
                <h2 className="text-xl font-semibold">Stream: {activeTab}</h2>
                <div className="flex gap-2">
                  <button
                    onClick={() => setShowColumnSelector(!showColumnSelector)}
                    className="px-4 py-2 rounded font-medium text-sm bg-gray-700 hover:bg-gray-600"
                  >
                    {showColumnSelector ? 'Hide' : 'Show'} Columns
                  </button>
                  <button
                    onClick={() => loadMasterMatrix(true, activeTab)}
                    disabled={masterLoading}
                    className={`px-4 py-2 rounded font-medium text-sm ${
                      masterLoading
                        ? 'bg-gray-600 cursor-not-allowed'
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
                  className="flex items-center justify-between w-full px-4 py-2 bg-gray-700 hover:bg-gray-600 rounded text-left"
                >
                  <span className="font-medium">Statistics</span>
                  <span>{showStats[activeTab] ? '▼' : '▶'}</span>
                </button>
                {showStats[activeTab] && renderStats(activeTab)}
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
