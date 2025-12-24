import { useCallback } from 'react'
import { List } from 'react-window'
import { getFilteredColumns } from '../utils/columnUtils'
import { DEFAULT_COLUMNS } from '../utils/constants'
import { sortColumnsByDefaultOrder } from '../utils/columnUtils'

// Row component for virtual scrolling
function TableRow({ index, style, rows, columnsToShow, streamId, getColumnWidth, totalFiltered, workerFilteredIndices }) {
  // If row not loaded yet, show loading placeholder
  if (index >= rows.length && workerFilteredIndices && index < totalFiltered) {
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
        // Format numeric columns
        if (['Profit', 'Peak', 'Target', 'Range', 'StopLoss'].includes(col)) {
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
        // Time Change
        if (col === 'Time Change') {
          if (value && typeof value === 'string' && value.trim() !== '') {
            value = value.trim()
          } else {
            value = ''
          }
        }
        // Format dollar columns
        if (col === 'Profit ($)') {
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
        // Format time slot columns
        if (col.includes(' Rolling') && value !== null && value !== undefined) {
          const numValue = parseFloat(value)
          if (!isNaN(numValue)) value = numValue.toFixed(2)
        }
        if (col.includes(' Points') && value !== null && value !== undefined) {
          const numValue = parseFloat(value)
          if (!isNaN(numValue)) value = numValue.toFixed(0)
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

export default function DataTable({
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
  onLoadMoreRows
}) {
  // Use worker filtered data if available (much faster - off main thread)
  let filtered = []
  let totalFiltered = 0
  
  if (workerReady && workerFilteredIndices.length > 0) {
    // Use loaded rows (incrementally loaded as user scrolls)
    filtered = loadedRows.length > 0 ? loadedRows : (workerFilteredRows || [])
    totalFiltered = filteredLength || workerFilteredIndices.length
  } else {
    // Fallback to main thread filtering only if worker not ready
    filtered = data
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
        rowProps={{ rows: filtered, columnsToShow, streamId, getColumnWidth, totalFiltered, workerFilteredIndices }}
        overscanCount={10} // Render 10 extra rows for smooth scrolling
        style={{ height: 600, width: `${totalWidth}px` }} // Fixed height and width for virtual list
        onRowsRendered={({ startIndex, stopIndex }) => {
          // Load more rows when user scrolls near the end
          if (workerReady && workerFilteredIndices && workerFilteredIndices.length > 0 && onLoadMoreRows) {
            onLoadMoreRows(startIndex, stopIndex)
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

