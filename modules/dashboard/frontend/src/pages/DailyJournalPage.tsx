/**
 * DailyJournalPage - Unified daily journal: streams, trades, total PnL
 */
import { useState } from 'react'
import { useDailyJournal } from '../hooks/useJournalData'
import { WatchdogNavigationBar } from '../components/shared/WatchdogNavigationBar'
import { formatChicagoTime } from '../utils/timeUtils'

export function DailyJournalPage() {
  const [tradingDate, setTradingDate] = useState(() => {
    const today = new Date()
    return today.toISOString().slice(0, 10)
  })
  const { journal, loading, error } = useDailyJournal(tradingDate)

  return (
    <div className="min-h-screen bg-black text-white">
      <WatchdogNavigationBar />
      <div className="p-8 pt-14">
        <h1 className="text-2xl font-bold mb-6">Daily Journal</h1>

        {/* Date Picker */}
        <div className="bg-gray-800 rounded-lg p-4 mb-6">
          <label className="block text-sm font-semibold mb-2">Trading Date</label>
          <input
            type="date"
            value={tradingDate}
            onChange={(e) => setTradingDate(e.target.value)}
            className="px-3 py-2 bg-gray-700 border border-gray-600 rounded text-white"
          />
        </div>

        {loading && <div className="text-gray-500">Loading...</div>}
        {error && <div className="text-red-500">Error: {error}</div>}

        {!loading && !error && journal && (
          <>
            {/* Total PnL Card */}
            <div className="bg-gray-800 rounded-lg p-6 mb-6">
              <div className="text-sm text-gray-400">Total Realized P&L</div>
              <div
                className={`text-3xl font-bold ${journal.total_pnl >= 0 ? 'text-green-500' : 'text-red-500'}`}
              >
                ${journal.total_pnl.toFixed(2)}
              </div>
              <div className="text-xs text-gray-500 mt-1">{journal.trading_date}</div>
            </div>

            {/* Summary Cards (if available) */}
            {journal.summary && (
              <div className="grid grid-cols-4 gap-4 mb-6">
                <div className="bg-gray-800 rounded-lg p-4">
                  <div className="text-xs text-gray-400">Intents Executed</div>
                  <div className="text-xl font-bold">{journal.summary.intents_executed}</div>
                </div>
                <div className="bg-gray-800 rounded-lg p-4">
                  <div className="text-xs text-gray-400">Orders Rejected</div>
                  <div className="text-xl font-bold">{journal.summary.orders_rejected}</div>
                </div>
                <div className="bg-gray-800 rounded-lg p-4">
                  <div className="text-xs text-gray-400">Total Slippage</div>
                  <div className="text-xl font-bold">
                    ${(journal.summary.total_slippage_dollars ?? 0).toFixed(2)}
                  </div>
                </div>
                <div className="bg-gray-800 rounded-lg p-4">
                  <div className="text-xs text-gray-400">Execution Cost</div>
                  <div className="text-xl font-bold">
                    ${(journal.summary.total_execution_cost ?? 0).toFixed(2)}
                  </div>
                </div>
              </div>
            )}

            {/* Trades table - stream/instrument/status merged into rows */}
            <div className="bg-gray-800 rounded-lg overflow-hidden">
              {journal.streams.length === 0 ? (
                <div className="text-gray-500 text-center py-8">No streams for this date</div>
              ) : (
                <table className="w-full text-sm">
                  <thead className="bg-gray-700/50">
                    <tr>
                      <th className="px-4 py-2 text-left">Stream</th>
                      <th className="px-4 py-2 text-left">Instrument</th>
                      <th className="px-4 py-2 text-left">Outcome</th>
                      <th className="px-4 py-2 text-left">Intent</th>
                      <th className="px-4 py-2 text-left">Dir</th>
                      <th className="px-4 py-2 text-left">Entry → Exit</th>
                      <th className="px-4 py-2 text-left">Qty</th>
                      <th className="px-4 py-2 text-left">P&L</th>
                      <th className="px-4 py-2 text-left">Result</th>
                      <th className="px-4 py-2 text-left">Exit</th>
                      <th className="px-4 py-2 text-left">Time</th>
                      <th className="px-4 py-2 text-left">Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {journal.streams.flatMap((s) =>
                      s.trades.length > 0
                        ? s.trades.map((t) => (
                            <tr key={t.intent_id} className="border-b border-gray-700/50">
                              <td className="px-4 py-2 font-mono font-semibold">{s.stream}</td>
                              <td className="px-4 py-2 text-gray-400">{s.instrument}</td>
                              <td className="px-4 py-2">
                                <span className="px-2 py-0.5 rounded text-xs bg-gray-600">
                                  {s.commit_reason || s.state || '-'}
                                </span>
                              </td>
                              <td className="px-4 py-2 font-mono text-xs truncate max-w-[8rem]" title={t.intent_id}>
                                {t.intent_id?.slice(0, 8)}…
                              </td>
                              <td className="px-4 py-2">
                                <span
                                  className={`px-1.5 py-0.5 rounded text-xs ${
                                    t.direction === 'Long' ? 'bg-green-700' : 'bg-red-700'
                                  }`}
                                >
                                  {t.direction}
                                </span>
                              </td>
                              <td className="px-4 py-2 font-mono">
                                {t.entry_price ?? '-'} → {t.exit_price ?? '-'}
                              </td>
                              <td className="px-4 py-2 font-mono">{t.entry_qty ?? '-'}</td>
                              <td className="px-4 py-2 font-mono">
                                <span
                                  className={
                                    (t.realized_pnl ?? 0) >= 0 ? 'text-green-400' : 'text-red-400'
                                  }
                                >
                                  {t.realized_pnl != null ? `$${t.realized_pnl.toFixed(2)}` : '-'}
                                </span>
                              </td>
                              <td className="px-4 py-2">
                                <span
                                  className={`px-1.5 py-0.5 rounded text-xs font-medium ${
                                    t.result === 'Win' ? 'bg-green-700 text-green-100' :
                                    t.result === 'Loss' ? 'bg-red-700 text-red-100' :
                                    'bg-gray-600 text-gray-200'
                                  }`}
                                >
                                  {t.result ?? '-'}
                                </span>
                              </td>
                              <td className="px-4 py-2 text-gray-400 text-xs">{t.exit_order_type ?? '-'}</td>
                              <td className="px-4 py-2 font-mono text-xs">
                                {t.exit_filled_at ? formatChicagoTime(t.exit_filled_at) : '-'}
                              </td>
                              <td className="px-4 py-2">{t.status}</td>
                            </tr>
                          ))
                        : [
                            <tr key={s.stream} className="border-b border-gray-700/50">
                              <td className="px-4 py-2 font-mono font-semibold">{s.stream}</td>
                              <td className="px-4 py-2 text-gray-400">{s.instrument}</td>
                              <td className="px-4 py-2">
                                <span className="px-2 py-0.5 rounded text-xs bg-gray-600">
                                  {s.commit_reason || s.state || '-'}
                                </span>
                              </td>
                              <td colSpan={8} className="px-4 py-2 text-gray-500 italic">
                                No trade
                              </td>
                            </tr>
                          ]
                    )}
                  </tbody>
                </table>
              )}
            </div>
          </>
        )}

        {!loading && !error && !journal && tradingDate && (
          <div className="text-gray-500 text-center py-8">No data for selected date</div>
        )}
      </div>
    </div>
  )
}
