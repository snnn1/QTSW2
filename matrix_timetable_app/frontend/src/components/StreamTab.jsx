import ColumnSelector from './ColumnSelector'
import StatsPanel from './StatsPanel'
import FiltersPanel from './FiltersPanel'
import DataTable from './DataTable'
import { sortColumnsByDefaultOrder, getFilteredColumns, getSelectedColumnsForTab } from '../utils/columnUtils'

export default function StreamTab({
  streamId,
  masterLoading,
  masterError,
  masterData,
  showColumnSelector,
  setShowColumnSelector,
  showStats,
  toggleStats,
  stats,
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
  toggleColumn
}) {
  return (
    <div className="space-y-4">
      <div className="bg-gray-900 rounded-lg p-6">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-xl font-semibold">Stream: {streamId}</h2>
          <div className="flex gap-2">
            <button
              onClick={() => setShowColumnSelector(!showColumnSelector)}
              className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-800"
            >
              {showColumnSelector ? 'Hide' : 'Show'} Columns
            </button>
            <button
              onClick={() => onLoadMasterMatrix(true, streamId)}
              disabled={masterLoading}
              className={`px-4 py-2 rounded font-medium text-sm ${
                masterLoading
                  ? 'bg-gray-700 cursor-not-allowed'
                  : 'bg-green-600 hover:bg-green-700'
              }`}
            >
              {masterLoading ? 'Rebuilding...' : 'Rebuild Stream'}
            </button>
          </div>
        </div>
        
        <ColumnSelector
          streamId={streamId}
          selectedColumns={selectedColumns}
          showColumnSelector={showColumnSelector}
          setShowColumnSelector={setShowColumnSelector}
          availableColumns={availableColumns}
          activeTab={activeTab}
          onToggleColumn={toggleColumn}
          onClose={() => setShowColumnSelector(false)}
          getFilteredColumns={getFilteredColumns}
          sortColumnsByDefaultOrder={sortColumnsByDefaultOrder}
          getSelectedColumnsForTab={getSelectedColumnsForTab}
        />
        
        {/* Stats Toggle */}
        <div className="mb-4">
          <button
            onClick={() => toggleStats(streamId)}
            className="flex items-center justify-between w-full px-4 py-2 bg-gray-800 hover:bg-gray-800 rounded text-left"
          >
            <span className="font-medium">Statistics</span>
            <span>{showStats[streamId] ? '▼' : '▶'}</span>
          </button>
          {showStats[streamId] && (
            <StatsPanel 
              streamId={streamId} 
              stats={stats}
              loading={false}
              error={null}
              noData={!stats}
            />
          )}
        </div>
        
        {/* Filters */}
        <FiltersPanel 
          streamId={streamId}
          streamFilters={streamFilters}
          setStreamFilters={setStreamFilters}
          updateStreamFilter={updateStreamFilter}
          getAvailableYears={getAvailableYears}
          getRelevantTimeSlots={getRelevantTimeSlots}
        />
        
        {/* Data Table */}
        {masterLoading ? (
          <div className="text-center py-8">Loading data...</div>
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
              streamId={streamId}
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

