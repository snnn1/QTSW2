import { useCallback, useMemo, memo } from 'react'
import { List } from 'react-window'
import { getFilteredColumns } from '../utils/columnUtils'
import { DEFAULT_COLUMNS } from '../utils/constants'
import { sortColumnsByDefaultOrder } from '../utils/columnUtils'

// Memoized formatters to avoid per-cell allocations
const currencyFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 0,
  maximumFractionDigits: 0
})

const dateFormatter = new Intl.DateTimeFormat('en-US', {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  weekday: 'short'
})

const dowFormatter = new Intl.DateTimeFormat('en-US', { weekday: 'short' })

// Row component for virtual scrolling
function TableRow({ index, style, rows, columnsToShow, streamId, getColumnWidth, totalFiltered, workerFilteredIndices, showFilteredDays = true }) {
  // If row not loaded yet (sparse array), show loading placeholder
  const row = rows[index]
  if (!row || row === undefined) {
    // Check if this index is within filtered range
    if (workerFilteredIndices && index < totalFiltered) {
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
    return null
  }
  
  return (
    <div 
      style={style} 
      className={`flex border-b border-gray-700 hover:bg-gray-900 ${
        row.final_allowed === false ? 'bg-red-900/20' : ''
      }`}
    >
      {columnsToShow.map(col => {
        let value = row[col]
        // Handle column name variations
        if (col === 'Symbol' && !value) {
          value = row['Instrument'] || ''
        }
        if (col === 'Date' && value) {
          try {
            const date = new Date(value)
            if (!isNaN(date.getTime())) {
              value = date.toLocaleDateString() // Use cached formatter would be better but this is fine
            } else {
              value = '-'
            }
          } catch {
            value = '-'
          }
        }
        // Calculate DOW (Day of Week) from Date
        if (col === 'DOW') {
          const dateValue = row['Date']
          if (dateValue) {
            try {
              const date = new Date(dateValue)
              if (!isNaN(date.getTime())) {
                value = dowFormatter.format(date).toUpperCase()
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
        // Format numeric columns
        if (['Profit', 'Peak', 'Target', 'Range', 'StopLoss'].includes(col)) {
          const symbol = (row.Symbol || row.Instrument || '').toUpperCase()
          const baseSymbol = symbol.replace(/\d+$/, '') || symbol
          const isNG = baseSymbol === 'NG'
          const decimalPlaces = isNG ? 3 : 2
          
          if (value !== null && value !== undefined) {
            const numValue = typeof value === 'number' ? value : parseFloat(value)
            if (!isNaN(numValue) && isFinite(numValue)) {
              value = numValue.toFixed(decimalPlaces)
            } else {
              value = '-'
            }
          } else {
            value = '-'
          }
        }
        // Time Change
        if (col === 'Time Change') {
          if (value && typeof value === 'string' && value.trim() !== '') {
            value = value.trim()
          } else {
            value = ''
          }
        }
        // Format dollar columns (use memoized formatter)
        if (col === 'Profit ($)') {
          const profitValue = parseFloat(row.Profit) || 0
          const symbol = row.Symbol || row.Instrument || 'ES'
          const baseSymbol = symbol.replace(/\d+$/, '').toUpperCase() // Remove trailing numbers
          // NOTE: Contract values must match modules/matrix/statistics.py
          const contractValues = {
            'ES': 50, 'MES': 5, 'NQ': 10, 'MNQ': 2, 'YM': 5, 'MYM': 0.5,
            'CL': 1000, 'NG': 10000, 'GC': 100, 'RTY': 50
          }
          const contractValue = contractValues[baseSymbol] || 50
          const dollarValue = profitValue * contractValue
          value = currencyFormatter.format(dollarValue)
        }
        if (false) { // Removed health gate column formatting
          if (value !== null && value !== undefined && value !== '') {
            const numValue = typeof value === 'number' ? value : parseFloat(value)
            if (!isNaN(numValue) && isFinite(numValue)) {
              // Always show currency format, even for zero
              value = currencyFormatter.format(numValue)
            } else {
              // Invalid number - show as zero instead of dash
              value = currencyFormatter.format(0)
            }
          } else {
            // Null/undefined/empty - show as zero instead of dash
            value = currencyFormatter.format(0)
          }
        }
        // Format time slot columns
        if (col.includes(' Rolling') && value !== null && value !== undefined) {
          const numValue = parseFloat(value)
          if (!isNaN(numValue) && isFinite(numValue)) {
            value = numValue.toFixed(2)
          }
        }
        if (col.includes(' Points') && value !== null && value !== undefined) {
          const numValue = parseFloat(value)
          if (!isNaN(numValue) && isFinite(numValue)) {
            value = numValue.toFixed(0)
          }
        }
        
        return (
          <div 
            key={col} 
            className="p-2 border-r border-gray-700 flex-shrink-0 text-left text-sm" 
            style={{ width: `${getColumnWidth(col)}px` }}
          >
            {value !== null && value !== undefined ? String(value) : '-'}
          </div>
        )
      })}
    </div>
  )
}

const DataTable = memo(function DataTable({
  data,
  streamId,
  workerReady,
  workerFilteredRows,
  workerFilteredIndices,
  filteredLength,
  loadedRows,
  loadingMoreRows,
  selectedColumns,
  activeTab,
  onLoadMoreRows,
  showFilteredDays = true,
  getFilteredData = null
}) {
  // Use worker filtered data if available (much faster - off main thread)
  let filtered = []
  let totalFiltered = 0
  
  if (workerReady && workerFilteredIndices && workerFilteredIndices.length > 0) {
    // Use loaded rows (incrementally loaded as user scrolls)
    // Handle sparse array: loadedRows may have holes (undefined entries) for unloaded rows
    let baseFiltered = loadedRows.length > 0 ? loadedRows : (workerFilteredRows || [])
    
    // Apply stream filter when using worker data
    // The worker filters by stream filters but may return multiple streams when streamId is 'master'
    // When viewing a specific stream tab (like 'ES1'), we need to filter to that stream only
    // Note: Filter out undefined entries (unloaded rows) - they'll show as placeholders
    if (streamId && streamId !== 'master') {
      filtered = baseFiltered.filter(row => row && row.Stream === streamId)
      totalFiltered = filtered.length
    } else {
      // Keep sparse structure - undefined entries will show as loading placeholders
      filtered = baseFiltered
      // Use filteredLength from worker (authoritative count), fallback to array length
      totalFiltered = filteredLength || filtered.length
    }
  } else {
    // Fallback to main thread filtering only if worker not ready
    if (getFilteredData && typeof getFilteredData === 'function') {
      filtered = getFilteredData(data, streamId)
    } else {
      filtered = data || []
    }
    totalFiltered = filtered.length
  }
  
  // Apply "Show/Hide Filtered Days" toggle
  // When showFilteredDays is true (default), show ALL rows including those with final_allowed === false
  // When showFilteredDays is false, hide rows with final_allowed === false
  // NOTE: Worker should already filter by final_allowed, but we add a safety check here to ensure
  // no filtered days slip through (e.g., if worker cache is stale or there's a race condition)
  if (!showFilteredDays) {
    // Filter out rows with final_allowed === false
    // Create a new dense array (indices will shift, but that's okay for virtualization)
    const originalLength = filtered.length
    const filteredArray = []
    let filteredOutCount = 0
    
    for (let i = 0; i < filtered.length; i++) {
      const row = filtered[i]
      // Keep undefined entries (unloaded rows) and rows that aren't filtered out
      if (row === undefined || row === null) {
        filteredArray.push(row)
      } else if (row.final_allowed !== false) {
        filteredArray.push(row)
      } else {
        filteredOutCount++
      }
    }
    
    filtered = filteredArray
    
    // Adjust totalFiltered count based on how many rows were filtered out
    if (workerReady && workerFilteredIndices && workerFilteredIndices.length > 0 && originalLength > 0) {
      // Estimate: if X% of loaded rows were filtered out, assume same % of total
      const filterRatio = filteredOutCount / originalLength
      totalFiltered = Math.max(0, Math.floor(totalFiltered * (1 - filterRatio)))
    } else {
      // For non-worker data, use filtered array length directly
      totalFiltered = filtered.length
    }
  }
  // When showFilteredDays is true, show all rows (including final_allowed === false)
  
  if (totalFiltered === 0) {
    // Check if we have data but filters are excluding everything
    const hasDataButFilteredOut = (data && data.length > 0) || 
                                   (workerReady && workerFilteredIndices && workerFilteredIndices.length === 0)
    
    return (
      <div className="text-center py-8 text-gray-400">
        <div className="mb-2">No data available{streamId ? ` for ${streamId}` : ''}</div>
        <div className="text-xs text-gray-500 mt-2">
          {hasDataButFilteredOut
            ? 'Filters are excluding all rows. Check year filters and stream filters in the Filters section above.'
            : 'Data may not be loaded yet. Try clicking "Refresh Data" or "Full Rebuild".'}
        </div>
      </div>
    )
  }
  
  // Use selected columns for the current tab, fallback to default if none selected
  const tabId = streamId || 'master'
  const getSelectedColumnsForTab = (tabId) => {
    const cols = selectedColumns[tabId] || DEFAULT_COLUMNS
    // Ensure columns are always in the correct order
    return sortColumnsByDefaultOrder(cols)
  }
  let columnsToShow = getSelectedColumnsForTab(tabId)
  
  // Filter columns based on stream (only show relevant time slot columns)
  columnsToShow = getFilteredColumns(columnsToShow, streamId)
  
  // Remove 'SL' column if present (replaced by 'StopLoss')
  columnsToShow = columnsToShow.filter(col => col !== 'SL')
  
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
      'Time Change': 100,
      'Profit ($)': 120
    }
    return widths[col] || 120
  }
  
  const totalWidth = columnsToShow.reduce((sum, col) => sum + getColumnWidth(col), 0)
  
  return (
    <div className="overflow-x-auto">
      {/* Table header - sticky */}
      <div className="flex bg-gray-800 sticky top-0 z-10 border-b border-gray-700" style={{ width: `${totalWidth}px` }}>
        {columnsToShow.map(col => {
          // Map column names to display names
          const displayName = col === 'StopLoss' ? 'Stop Loss' : col
          return (
            <div 
              key={col} 
              className="p-2 font-medium border-r border-gray-700 flex-shrink-0 text-left text-sm" 
              style={{ width: `${getColumnWidth(col)}px` }}
            >
              {displayName}
            </div>
          )
        })}
      </div>
      {/* Virtualized table body */}
      <List
        rowCount={totalFiltered} // Use total filtered count, not just loaded rows
        rowHeight={35} // Fixed row height
        rowComponent={TableRow}
        rowProps={{ rows: filtered, columnsToShow, streamId, getColumnWidth, totalFiltered, workerFilteredIndices, showFilteredDays }}
        overscanCount={10} // Render 10 extra rows for smooth scrolling
        style={{ height: 600, width: `${totalWidth}px` }} // Fixed height and width for virtual list
        onRowsRendered={({ startIndex, stopIndex }) => {
          // Load more rows when user scrolls near the end
          // Trigger loading when we're within 50 rows of the end of loaded data
          if (workerReady && workerFilteredIndices && workerFilteredIndices.length > 0 && onLoadMoreRows) {
            const loadedCount = filtered.length
            const threshold = Math.max(50, Math.floor(totalFiltered * 0.1)) // Load when within 10% of end or 50 rows
            if (stopIndex >= loadedCount - threshold && loadedCount < totalFiltered) {
              onLoadMoreRows(startIndex, stopIndex)
            }
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
})

export default DataTable

