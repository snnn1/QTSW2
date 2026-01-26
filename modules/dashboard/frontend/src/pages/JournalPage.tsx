/**
 * JournalPage - Execution Journal page
 */
import { useState } from 'react'
import { useExecutionJournal } from '../hooks/useJournalData'
import { WatchdogNavigationBar } from '../components/shared/WatchdogNavigationBar'

export function JournalPage() {
  const [tradingDate, setTradingDate] = useState('')
  const [stream, setStream] = useState<string>('')
  const [intentId, setIntentId] = useState<string>('')
  
  const { entries, loading, error } = useExecutionJournal(tradingDate, stream || undefined, intentId || undefined)
  
  return (
    <div className="min-h-screen bg-black text-white">
      <WatchdogNavigationBar />
      <div className="p-8 pt-14">
        <h1 className="text-2xl font-bold mb-6">Execution Journal</h1>
      
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
            <input
              type="text"
              value={stream}
              onChange={(e) => setStream(e.target.value)}
              placeholder="e.g., NQ1"
              className="w-full px-3 py-2 bg-gray-700 border border-gray-600 rounded text-white"
            />
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
                  <th className="px-4 py-2 text-left">Direction</th>
                  <th className="px-4 py-2 text-left">Entry → Exit</th>
                  <th className="px-4 py-2 text-left">Slippage</th>
                  <th className="px-4 py-2 text-left">Fees</th>
                  <th className="px-4 py-2 text-left">Total Cost</th>
                  <th className="px-4 py-2 text-left">Outcome</th>
                </tr>
              </thead>
              <tbody>
                {entries.map((entry) => (
                  <tr key={entry.intent_id} className="border-b border-gray-700">
                    <td className="px-4 py-2">
                      {entry.direction ? (
                        <span className={`px-2 py-1 rounded text-xs ${
                          entry.direction === 'Long' ? 'bg-green-700' : 'bg-red-700'
                        }`}>
                          {entry.direction}
                        </span>
                      ) : '-'}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {entry.expected_entry_price || '-'} → {entry.actual_fill_price || '-'}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {entry.slippage_points !== null ? `${entry.slippage_points} pts` : '-'}
                      {entry.slippage_dollars !== null && ` ($${entry.slippage_dollars.toFixed(2)})`}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {entry.commission !== null && `Comm: $${entry.commission.toFixed(2)}`}
                      {entry.fees !== null && ` Fees: $${entry.fees.toFixed(2)}`}
                      {entry.commission === null && entry.fees === null && '-'}
                    </td>
                    <td className="px-4 py-2 font-mono">
                      {entry.total_cost !== null ? `$${entry.total_cost.toFixed(2)}` : '-'}
                    </td>
                    <td className="px-4 py-2">
                      {entry.entry_filled ? 'Filled' : entry.rejected ? 'Rejected' : entry.entry_submitted ? 'Submitted' : 'Pending'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      </div>
    </div>
  )
}
