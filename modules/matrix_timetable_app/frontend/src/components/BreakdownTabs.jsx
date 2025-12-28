import ProfitTable from './ProfitTable'

export default function BreakdownTabs({
  activeTab,
  masterLoading,
  masterError,
  onRetryLoad,
  timeProfitAfter,
  dayProfitAfter,
  domProfitAfter,
  monthProfitAfter,
  yearProfitAfter,
  timeProfitBefore,
  dayProfitBefore,
  domProfitBefore,
  monthProfitBefore,
  yearProfitBefore
}) {
  const getTabTitle = () => {
    switch (activeTab) {
      case 'time': return 'Time'
      case 'day': return 'Day of Week (DOW)'
      case 'dom': return 'Day of Month'
      case 'month': return 'Month'
      case 'year': return 'Year'
      default: return ''
    }
  }

  const getAfterData = () => {
    switch (activeTab) {
      case 'time': return timeProfitAfter
      case 'day': return dayProfitAfter
      case 'dom': return domProfitAfter
      case 'month': return monthProfitAfter
      case 'year': return yearProfitAfter
      default: return {}
    }
  }

  const getBeforeData = () => {
    switch (activeTab) {
      case 'time': return timeProfitBefore
      case 'day': return dayProfitBefore
      case 'dom': return domProfitBefore
      case 'month': return monthProfitBefore
      case 'year': return yearProfitBefore
      default: return {}
    }
  }

  return (
    <div className="space-y-6">
      <div className="bg-gray-900 rounded-lg p-6">
        <h2 className="text-xl font-semibold mb-6">
          Profit by {getTabTitle()} - All Streams
        </h2>
        {masterLoading ? (
          <div className="text-center py-8">Loading data...</div>
        ) : masterError ? (
          <div className="text-center py-8 text-red-400">
            <div className="mb-4">{masterError}</div>
            <button
              onClick={onRetryLoad}
              className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded"
            >
              Retry Load
            </button>
          </div>
        ) : (
          <>
            {/* After Filters */}
            <div className="mb-8">
              <h3 className="text-lg font-semibold mb-4 text-green-400">After Filters</h3>
              <ProfitTable data={getAfterData()} periodType={activeTab} />
            </div>
            
            {/* Before Filters */}
            <div>
              <h3 className="text-lg font-semibold mb-4 text-blue-400">Before Filters</h3>
              <ProfitTable data={getBeforeData()} periodType={activeTab} />
            </div>
          </>
        )}
      </div>
    </div>
  )
}

























