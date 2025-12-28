export default function TimetableTab({
  currentTradingDay,
  currentTime,
  masterLoading,
  masterError,
  workerTimetableLoading,
  workerTimetable,
  onRetryLoad
}) {
  return (
    <div className="space-y-6">
      <div className="bg-gray-900 rounded-lg p-6">
        <div className="flex items-center justify-between mb-6">
          <h2 className="text-xl font-semibold">Trading Timetable</h2>
          <div className="text-center">
            <div className="text-lg font-semibold text-gray-300">
              {currentTradingDay.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })}
            </div>
          </div>
          <div className="text-right">
            <div className="text-lg font-mono font-semibold text-gray-300">
              {currentTime.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })}
            </div>
            <div className="text-2xl font-mono font-bold text-blue-400">
              {currentTime.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })}
            </div>
          </div>
        </div>
        
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
        ) : workerTimetableLoading ? (
          <div className="text-center py-8 text-gray-400">Calculating timetable...</div>
        ) : !workerTimetable || workerTimetable.length === 0 ? (
          <div className="text-center py-8 text-gray-400">No timetable data available</div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead>
                <tr className="bg-gray-800">
                  <th className="px-4 py-3 text-left font-semibold">Stream</th>
                  <th className="px-4 py-3 text-left font-semibold">Time</th>
                </tr>
              </thead>
              <tbody>
                {workerTimetable.map((row, idx) => (
                  <tr key={`${row.Stream}-${idx}`} className="border-b border-gray-700 hover:bg-gray-750">
                    <td className="px-4 py-3">{row.Stream}</td>
                    <td className="px-4 py-3 font-mono">{row.Time}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  )
}

























