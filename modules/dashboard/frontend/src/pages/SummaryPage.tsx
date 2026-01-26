/**
 * SummaryPage - Daily Summary page
 */
import { useState, useMemo } from 'react'
import { useExecutionSummary } from '../hooks/useJournalData'
import { useStreamPnl } from '../hooks/useStreamPnl'
import { WatchdogNavigationBar } from '../components/shared/WatchdogNavigationBar'

export function SummaryPage() {
  const [tradingDate, setTradingDate] = useState('')
  const { summary, loading, error } = useExecutionSummary(tradingDate)
  const { pnl } = useStreamPnl(tradingDate)
  
  // Calculate total P&L
  const totalPnl = useMemo(() => {
    return Object.values(pnl).reduce((sum, s) => {
      return sum + (s.realized_pnl || 0)
    }, 0)
  }, [pnl])
  
  return (
    <div className="min-h-screen bg-black text-white">
      <WatchdogNavigationBar />
      <div className="p-8 pt-14">
        <h1 className="text-2xl font-bold mb-6">Daily Summary</h1>
      
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
      
      {!loading && !error && summary && (
        <>
          {/* Cards */}
          <div className="grid grid-cols-3 gap-4 mb-6">
            <div className="bg-gray-800 rounded-lg p-4">
              <div className="text-sm text-gray-400">Intents Seen</div>
              <div className="text-2xl font-bold">{summary.intents_seen}</div>
            </div>
            <div className="bg-gray-800 rounded-lg p-4">
              <div className="text-sm text-gray-400">Intents Executed</div>
              <div className="text-2xl font-bold">{summary.intents_executed}</div>
            </div>
            <div className="bg-gray-800 rounded-lg p-4">
              <div className="text-sm text-gray-400">Orders Rejected</div>
              <div className="text-2xl font-bold">{summary.orders_rejected}</div>
            </div>
            <div className="bg-gray-800 rounded-lg p-4">
              <div className="text-sm text-gray-400">Protective Failures</div>
              <div className="text-2xl font-bold">{summary.orders_blocked}</div>
            </div>
            <div className="bg-gray-800 rounded-lg p-4">
              <div className="text-sm text-gray-400">Total Slippage</div>
              <div className="text-2xl font-bold">${summary.total_slippage_dollars.toFixed(2)}</div>
            </div>
            <div className="bg-gray-800 rounded-lg p-4">
              <div className="text-sm text-gray-400">Total Execution Cost</div>
              <div className="text-2xl font-bold">${summary.total_execution_cost.toFixed(2)}</div>
            </div>
            <div className="bg-gray-800 rounded-lg p-4">
              <div className="text-sm text-gray-400">Total Realized P&L</div>
              <div className={`text-2xl font-bold ${totalPnl >= 0 ? 'text-green-500' : 'text-red-500'}`}>
                ${totalPnl.toFixed(2)}
              </div>
            </div>
          </div>
          
          {/* Intent Breakdown Table */}
          <div className="bg-gray-800 rounded-lg p-4 mb-6">
            <h2 className="text-lg font-semibold mb-4">Intent Breakdown</h2>
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="bg-gray-700">
                  <tr>
                    <th className="px-4 py-2 text-left">Intent ID</th>
                    <th className="px-4 py-2 text-left">Stream</th>
                    <th className="px-4 py-2 text-left">Executed</th>
                    <th className="px-4 py-2 text-left">Rejected</th>
                    <th className="px-4 py-2 text-left">Blocked</th>
                    <th className="px-4 py-2 text-left">Slippage $</th>
                    <th className="px-4 py-2 text-left">Total Cost $</th>
                  </tr>
                </thead>
                <tbody>
                  {summary.intent_details.map((intent) => (
                    <tr key={intent.intent_id} className="border-b border-gray-700">
                      <td className="px-4 py-2 font-mono text-xs">{intent.intent_id}</td>
                      <td className="px-4 py-2 font-mono">{intent.stream}</td>
                      <td className="px-4 py-2">{intent.executed ? '✅' : '❌'}</td>
                      <td className="px-4 py-2">{intent.orders_rejected}</td>
                      <td className="px-4 py-2">{intent.blocked ? '⚠️' : '-'}</td>
                      <td className="px-4 py-2 font-mono">
                        {intent.slippage_dollars !== null ? `$${intent.slippage_dollars.toFixed(2)}` : '-'}
                      </td>
                      <td className="px-4 py-2 font-mono">
                        {intent.total_cost !== null ? `$${intent.total_cost.toFixed(2)}` : '-'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
          
          {/* Blocked by Reason Table */}
          {Object.keys(summary.blocked_by_reason).length > 0 && (
            <div className="bg-gray-800 rounded-lg p-4">
              <h2 className="text-lg font-semibold mb-4">Blocked by Reason</h2>
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead className="bg-gray-700">
                    <tr>
                      <th className="px-4 py-2 text-left">Reason</th>
                      <th className="px-4 py-2 text-left">Count</th>
                    </tr>
                  </thead>
                  <tbody>
                    {Object.entries(summary.blocked_by_reason).map(([reason, count]) => (
                      <tr key={reason} className="border-b border-gray-700">
                        <td className="px-4 py-2">{reason}</td>
                        <td className="px-4 py-2 font-mono">{count}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}
      
      {!loading && !error && !summary && tradingDate && (
        <div className="text-gray-500 text-center py-8">No summary data for selected date</div>
      )}
      </div>
    </div>
  )
}
