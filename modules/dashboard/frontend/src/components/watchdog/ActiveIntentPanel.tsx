/**
 * ActiveIntentPanel component
 * Shows active intents and unprotected positions
 */
import { useState, useEffect } from 'react'
import { formatChicagoTime } from '../../utils/timeUtils'
import type { IntentExposure, UnprotectedPosition } from '../../types/watchdog'

interface ActiveIntentPanelProps {
  intents: IntentExposure[]
  unprotectedPositions: UnprotectedPosition[]
}

export function ActiveIntentPanel({ intents, unprotectedPositions }: ActiveIntentPanelProps) {
  const [, forceUpdate] = useState(0)
  
  // Force re-render every second for blinking timer
  useEffect(() => {
    const interval = setInterval(() => {
      forceUpdate(prev => prev + 1)
    }, 1000)
    return () => clearInterval(interval)
  }, [])
  
  // Empty state
  if (intents.length === 0) {
    return (
      <div id="active-intent-panel" className="bg-gray-800 rounded-lg p-4">
        <h2 className="text-lg font-semibold mb-4">Active Intents</h2>
        <div className="text-gray-500 text-center py-4">No active intents</div>
      </div>
    )
  }
  
  const isUnprotected = (intentId: string) => {
    return unprotectedPositions.some(p => p.intent_id === intentId)
  }
  
  return (
    <div id="active-intent-panel" className="bg-gray-800 rounded-lg p-4">
      <h2 className="text-lg font-semibold mb-4">Active Intents</h2>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-gray-700">
            <tr>
              <th className="px-2 py-1 text-left">Stream</th>
              <th className="px-2 py-1 text-left">Dir</th>
              <th className="px-2 py-1 text-left">Qty</th>
              <th className="px-2 py-1 text-left">Filled</th>
              <th className="px-2 py-1 text-left">Remaining</th>
              <th className="px-2 py-1 text-left">Protected</th>
              <th className="px-2 py-1 text-left">Since</th>
            </tr>
          </thead>
          <tbody>
            {intents.map((intent) => {
              const unprotected = isUnprotected(intent.intent_id)
              return (
                <tr key={intent.intent_id} className="border-b border-gray-700">
                  <td className="px-2 py-1 font-mono">{intent.stream_id}</td>
                  <td className="px-2 py-1">
                    <span className={`px-1 py-0.5 rounded text-xs ${
                      intent.direction === 'Long' ? 'bg-green-700' : 'bg-red-700'
                    }`}>
                      {intent.direction}
                    </span>
                  </td>
                  <td className="px-2 py-1">{intent.quantity || 0}</td>
                  <td className="px-2 py-1">{intent.entry_filled_qty || 0}</td>
                  <td className="px-2 py-1">{intent.remaining_exposure || 0}</td>
                  <td className="px-2 py-1">
                    {unprotected ? (
                      <span className="text-red-500 blink font-semibold">🔴 UNPROTECTED</span>
                    ) : (
                      <span className="text-green-500">✅</span>
                    )}
                  </td>
                  <td className="px-2 py-1 font-mono text-xs">
                    {unprotectedPositions.find(p => p.intent_id === intent.intent_id)?.entry_filled_at_chicago
                      ? formatChicagoTime(unprotectedPositions.find(p => p.intent_id === intent.intent_id)!.entry_filled_at_chicago)
                      : '-'}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}
