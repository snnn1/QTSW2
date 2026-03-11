/**
 * JournalPage - Execution Journal page (per-trade detail)
 */
import { useState, useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useExecutionJournal, useDailyJournal } from './hooks/useJournalData'
import { WatchdogNavigationBar } from './components/WatchdogNavigationBar'

export function JournalPage() {
  const [tradingDate, setTradingDate] = useState(() => new Date().toISOString().slice(0, 10))
  const [stream, setStream] = useState<string>('')
  const [intentId, setIntentId] = useState<string>('')
  
  const { entries, loading, error } = useExecutionJournal(tradingDate, stream || undefined, intentId || undefined)
  const { journal: dailyJournal } = useDailyJournal(tradingDate)
  
  const streamOptions = useMemo(() => dailyJournal?.streams?.map((s) => s.stream) ?? [], [dailyJournal])
  const totalPnl = useMemo(
    () => entries.reduce((sum, e) => sum + ((e as any).realized_pnl_net ?? (e as any).RealizedPnLNet ?? 0), 0),
    [entries]
  )
  
  return (
    <div className="min-h-screen bg-black text-white">
      <WatchdogNavigationBar />
      <div className="p-8 pt-14">
        <div className="flex items-center justify-between mb-6">
          <h1 className="text-2xl font-bold">Execution Journal</h1>
          <Link
            to="/daily"
            className="text-sm text-blue-400 hover:text-blue-300"
          >
            View Daily Journal →
          </Link>
        </div>
      
      {/* Filters */}
      <div className="bg-gray-800 rounded-lg p-4 mb-6">
        <div className="grid grid-cols-4 gap-4">
          <div>
            <label className="block text-sm font-semibold mb-1">Trading Date</label>
            <input
              type="date"
              value={tradingDate}
              onChange={(e) => setTradingDate(e.target.value)}
              className="w-full px-3 py-2 bg-gray-700 border border-gray-600 rounded text-white"
              required
            />
          </div>
          <div>
            <label className="block text-sm font-semibold mb-1">Stream (optional)</label>
            <select
              value={stream}
              onChange={(e) => setStream(e.target.value)}
              className="w-full px-3 py-2 bg-gray-700 border border-gray-600 rounded text-white"
            >
              <option value="">All streams</option>
              {streamOptions.map((s) => (
                <option key={s} value={s}>{s}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="block text-sm font-semibold mb-1">Intent ID (optional)</label>
            <input
              type="text"
              value={intentId}
              onChange={(e) => setIntentId(e.target.value)}
              placeholder="Intent ID"
              className="w-full px-3 py-2 bg-gray-700 border border-gray-600 rounded text-white font-mono text-sm"
            />
          </div>
        </div>
      </div>
      
      {/* Table */}
      {loading && <div className="text-gray-500">Loading...</div>}
      {error && <div className="text-red-500">Error: {error}</div>}
      {!loading && !error && entries.length === 0 && (
        <div className="text-gray-500 text-center py-8">No entries found</div>
      )}
      {!loading && !error && entries.length > 0 && (
        <div className="bg-gray-800 rounded-lg overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full">
              <thead className="bg-gray-700">
                <tr>
                  <th className="px-4 py-2 text-left">Stream</th>
                  <th className="px-4 py-2 text-left">Direction</th>
                  <th className="px-4 py-2 text-left">Entry → Exit</th>
                  <th className="px-4 py-2 text-left">Slippage</th>
                  <th className="px-4 py-2 text-left">Fees</th>
                  <th className="px-4 py-2 text-left">Total Cost</th>
                  <th className="px-4 py-2 text-left">P&L</th>
                  <th className="px-4 py-2 text-left">Outcome</th>
                </tr>
              </thead>
              <tbody>
                {entries.map((entry) => {
                  const e = entry as any
                  const streamVal = e.stream ?? e.Stream ?? '-'
                  const direction = e.direction ?? e.Direction
                  const expectedEntry = e.expected_entry_price ?? e.ExpectedEntryPrice
                  const actualFill = e.actual_fill_price ?? e.ActualFillPrice ?? e.FillPrice
                  const slippagePts = e.slippage_points ?? e.SlippagePoints
                  const slippageDollars = e.slippage_dollars ?? e.SlippageDollars
                  const commission = e.commission ?? e.Commission
                  const fees = e.fees ?? e.Fees
                  const totalCost = e.total_cost ?? e.TotalCost
                  const realizedPnl = e.realized_pnl_net ?? e.RealizedPnLNet
                  const entryFilled = e.entry_filled ?? e.EntryFilled
                  const rejected = e.rejected ?? e.Rejected
                  const entrySubmitted = e.entry_submitted ?? e.EntrySubmitted
                  return (
                  <tr key={e.intent_id ?? e.IntentId} className="border-b border-gray-700">
                    <td className="px-4 py-2 font-mono">{streamVal}</td>
                    <td className="px-4 py-2">
                      {direction ? (
                        <span className={`px-2 py-1 rounded text-xs ${
                          direction === 'Long' ? 'bg-green-700' : 'bg-red-700'
                        }`}>
                          {direction}
                        </span>
                      ) : '-'}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {expectedEntry ?? '-'} → {actualFill ?? '-'}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {slippagePts != null ? `${slippagePts} pts` : '-'}
                      {slippageDollars != null && ` ($${slippageDollars.toFixed(2)})`}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {commission != null && `Comm: $${commission.toFixed(2)}`}
                      {fees != null && ` Fees: $${fees.toFixed(2)}`}
                      {commission == null && fees == null && '-'}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {totalCost != null ? `$${totalCost.toFixed(2)}` : '-'}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {realizedPnl != null ? (
                        <span className={realizedPnl >= 0 ? 'text-green-400' : 'text-red-400'}>
                          ${realizedPnl.toFixed(2)}
                        </span>
                      ) : '-'}
                    </td>
                    <td className="px-4 py-2">
                      {entryFilled ? 'Filled' : rejected ? 'Rejected' : entrySubmitted ? 'Submitted' : 'Pending'}
                    </td>
                  </tr>
                )})}
              </tbody>
            </table>
          </div>
          {entries.length > 0 && (
            <div className="px-4 py-2 bg-gray-700/50 border-t border-gray-600 font-mono text-sm">
              Total P&L: <span className={totalPnl >= 0 ? 'text-green-400' : 'text-red-400'}>
                ${totalPnl.toFixed(2)}
              </span>
            </div>
          )}
        </div>
      )}
      </div>
    </div>
  )
}
