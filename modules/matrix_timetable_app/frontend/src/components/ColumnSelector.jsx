import { DEFAULT_COLUMNS } from '../utils/constants'

export default function ColumnSelector({
  showColumnSelector,
  availableColumns,
  activeTab,
  selectedColumns,
  onToggleColumn,
  onClose,
  getFilteredColumns,
  sortColumnsByDefaultOrder,
  getSelectedColumnsForTab
}) {
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
          onClick={onClose}
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
                onChange={() => onToggleColumn(col)}
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
            onToggleColumn(null, true) // true = reset to default
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
            onToggleColumn(null, false, sortedCols) // false = select all
          }}
          className="px-3 py-1 text-sm bg-gray-800 hover:bg-gray-800 rounded"
        >
          Select All
        </button>
      </div>
    </div>
  )
}

























