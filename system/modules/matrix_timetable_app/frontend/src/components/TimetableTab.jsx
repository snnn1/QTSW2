import { formatChicagoTime } from '../utils/dateUtils'

/**
 * Presentational Trading Timetable grid.
 *
 * **Rows must be derived only from `GET /api/timetable/current`** (e.g. map
 * `apiDocToExecutionTimetable` → `{ Stream, Time, Enabled, BlockReason }`).
 * Do **not** pass worker / matrix recomputed timetable data — that path is deprecated
 * and removed from the main app.
 *
 * `App.jsx` inlines the timetable tab; this component exists for extraction / reuse
 * without reintroducing worker execution logic.
 *
 * @typedef {{ Stream: string, Time: string, Enabled: boolean, BlockReason: string|null }} TimetableRow
 */

export default function TimetableTab({
  currentTradingDay,
  currentTime,
  masterLoading = false,
  masterError = null,
  onRetryLoad,
  /** Loading state for GET /api/timetable/current */
  timetableApiLoading = false,
  /** Error message when API missing or failed */
  timetableApiError = null,
  /** @type {TimetableRow[]} */
  timetableRows = [],
  showBlockedTimetableRows = true,
}) {
  const utcDateStr = currentTime.toLocaleDateString('en-US', {
    weekday: 'long',
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    timeZone: 'UTC',
  })
  const utcTimeStr = currentTime.toLocaleTimeString('en-US', {
    hour12: false,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    timeZone: 'UTC',
  })

  const chicagoDateStr = formatChicagoTime(currentTime, {
    weekday: 'long',
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  })
  const chicagoTimeStr = formatChicagoTime(currentTime, {
    hour12: false,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })

  const visibleRows = timetableRows.filter(
    (row) => showBlockedTimetableRows || row.Enabled !== false
  )

  return (
    <div className="space-y-6">
      <div className="bg-gray-900 rounded-lg p-6">
        <div className="flex items-center justify-between mb-6">
          <h2 className="text-xl font-semibold">Trading Timetable</h2>
          <div className="text-center">
            <div className="text-lg font-semibold text-gray-300">
              {currentTradingDay
                ? currentTradingDay.toLocaleDateString('en-US', {
                    weekday: 'long',
                    year: 'numeric',
                    month: 'long',
                    day: 'numeric',
                  })
                : 'Loading session…'}
            </div>
          </div>
          <div className="text-right">
            <div className="mb-2">
              <div className="text-xs font-semibold text-gray-500 uppercase mb-1">UTC</div>
              <div className="text-sm font-mono font-semibold text-gray-400">{utcDateStr}</div>
              <div className="text-xl font-mono font-bold text-blue-400">{utcTimeStr}</div>
            </div>
            <div>
              <div className="text-xs font-semibold text-gray-500 uppercase mb-1">Chicago</div>
              <div className="text-sm font-mono font-semibold text-gray-400">{chicagoDateStr}</div>
              <div className="text-xl font-mono font-bold text-green-400">{chicagoTimeStr}</div>
            </div>
          </div>
        </div>

        {masterLoading ? (
          <div className="text-center py-8">Loading data...</div>
        ) : masterError ? (
          <div className="text-center py-8 text-red-400">
            <div className="mb-4">{masterError}</div>
            <button
              type="button"
              onClick={onRetryLoad}
              className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded"
            >
              Retry Load
            </button>
          </div>
        ) : timetableApiLoading ? (
          <div className="text-center py-8 text-gray-400">Loading timetable from API…</div>
        ) : timetableApiError ? (
          <div className="text-center py-8 text-amber-400">
            No timetable available (API not loaded): {timetableApiError}
          </div>
        ) : !timetableRows.length ? (
          <div className="text-center py-8 text-gray-400">
            No timetable rows — API returned no streams.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead>
                <tr className="bg-gray-800">
                  <th className="px-4 py-3 text-left font-semibold">Stream</th>
                  <th className="px-4 py-3 text-left font-semibold">Time</th>
                  <th className="px-4 py-3 text-left font-semibold">Status</th>
                </tr>
              </thead>
              <tbody>
                {visibleRows.map((row, idx) => {
                  const isDisabled = row.Enabled === false
                  return (
                    <tr
                      key={`${row.Stream}-${idx}`}
                      className={`border-b border-gray-700 hover:bg-gray-750 ${isDisabled ? 'opacity-50 bg-gray-800' : ''}`}
                      title={isDisabled && row.BlockReason ? `Blocked: ${row.BlockReason}` : ''}
                    >
                      <td className="px-4 py-3">{row.Stream}</td>
                      <td className="px-4 py-3 font-mono">{row.Time}</td>
                      <td className="px-4 py-3">
                        {isDisabled ? (
                          <span className="text-red-400 text-sm" title={row.BlockReason || 'Blocked'}>
                            Blocked {row.BlockReason && `(${row.BlockReason})`}
                          </span>
                        ) : (
                          <span className="text-green-400 text-sm">Enabled</span>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  )
}
