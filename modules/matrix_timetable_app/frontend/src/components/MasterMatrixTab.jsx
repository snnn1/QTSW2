import ColumnSelector from './ColumnSelector'
import StatsPanel from './StatsPanel'
import FiltersPanel from './FiltersPanel'
import DataTable from './DataTable'
import { sortColumnsByDefaultOrder, getFilteredColumns, getSelectedColumnsForTab } from '../utils/columnUtils'

export default function MasterMatrixTab({
  masterLoading,
  masterError,
  masterData,
  showColumnSelector,
  setShowColumnSelector,
  showStats,
  toggleStats,
  multiplierInput,
  setMultiplierInput,
  masterContractMultiplier,
  setMasterContractMultiplier,
  onLoadMasterMatrix,
  onRetryLoad,
  filteredDataLength,
  selectedColumns,
  activeTab,
  workerReady,
  workerFilteredRows,
  workerFilteredIndices,
  loadedRows,
  loadingMoreRows,
  onLoadMoreRows,
  streamFilters,
  setStreamFilters,
  updateStreamFilter,
  getAvailableYears,
  getRelevantTimeSlots,
  availableColumns,
  formatWorkerStats,
  workerStats,
  statsLoading,
  toggleColumn
}) {
  const masterStats = workerStats && formatWorkerStats ? formatWorkerStats(workerStats, 'master') : null
  return (
    <div className="space-y-4">
      <div className="bg-gray-900 rounded-lg p-6">
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
              className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-800"
            >
              {showColumnSelector ? 'Hide' : 'Show'} Columns
            </button>
            <button
              onClick={() => onLoadMasterMatrix(true)}
              disabled={masterLoading}
              className={`px-4 py-2 rounded font-medium text-sm ${
                masterLoading
                  ? 'bg-gray-700 cursor-not-allowed'
                  : 'bg-blue-600 hover:bg-blue-700'
              }`}
            >
              {masterLoading ? 'Loading...' : 'Rebuild Matrix'}
            </button>
          </div>
        </div>
        
        <ColumnSelector
          streamId="master"
          selectedColumns={selectedColumns}
          showColumnSelector={showColumnSelector}
          setShowColumnSelector={setShowColumnSelector}
          availableColumns={availableColumns}
          activeTab="master"
          onToggleColumn={toggleColumn}
          onClose={() => setShowColumnSelector(false)}
          getFilteredColumns={getFilteredColumns}
          sortColumnsByDefaultOrder={sortColumnsByDefaultOrder}
          getSelectedColumnsForTab={getSelectedColumnsForTab}
        />
        
        {/* Stats Toggle for Master */}
        <div className="mb-4">
          <button
            onClick={() => toggleStats('master')}
            className="flex items-center justify-between w-full px-4 py-2 bg-gray-800 hover:bg-gray-800 rounded text-left"
          >
            <span className="font-medium">Statistics (All Streams)</span>
            <span>{showStats['master'] ? '▼' : '▶'}</span>
          </button>
          {showStats['master'] && (
            <StatsPanel 
              streamId="master" 
              stats={masterStats}
              loading={statsLoading}
              error={null}
              noData={!masterStats}
            />
          )}
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
              onClick={() => {
                const value = parseFloat(multiplierInput)
                if (!isNaN(value) && value > 0) {
                  const clamped = Math.max(0.1, Math.min(100, value))
                  setMasterContractMultiplier(clamped)
                  setMultiplierInput(clamped)
                } else {
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
        
        {/* Filters for Master */}
        <FiltersPanel 
          streamId="master"
          streamFilters={streamFilters}
          setStreamFilters={setStreamFilters}
          updateStreamFilter={updateStreamFilter}
          getAvailableYears={getAvailableYears}
          getRelevantTimeSlots={getRelevantTimeSlots}
        />
        
        {masterLoading ? (
          <div className="text-center py-8">Loading master matrix...</div>
        ) : masterError ? (
          <div className="text-center py-8 text-red-400">
            <div className="mb-4">{masterError}</div>
            <div className="flex gap-2 justify-center">
              <button
                onClick={onRetryLoad}
                className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded"
              >
                Retry Load
              </button>
              {masterError.includes('No data') && (
                <button
                  onClick={() => onLoadMasterMatrix(true)}
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
              Showing {Math.min(filteredDataLength || 0, filteredDataLength || 0)} of {filteredDataLength || 0} trades
            </div>
            <DataTable
              data={masterData}
              streamId="master"
              workerReady={workerReady}
              workerFilteredRows={workerFilteredRows}
              workerFilteredIndices={workerFilteredIndices}
              filteredLength={filteredDataLength}
              loadedRows={loadedRows}
              loadingMoreRows={loadingMoreRows}
              selectedColumns={selectedColumns}
              activeTab={activeTab}
              onLoadMoreRows={onLoadMoreRows}
            />
          </>
        )}
      </div>
    </div>
  )
}

