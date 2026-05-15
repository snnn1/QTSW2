/**
 * ActiveIntentPanel component
 */
import { useEffect, useState } from 'react'
import { formatChicagoTime } from '../../utils/timeUtils.ts'
import type { IntentExposure, UnprotectedPosition } from '../../types/watchdog'

interface ActiveIntentPanelProps {
  intents: IntentExposure[]
  unprotectedPositions: UnprotectedPosition[]
}

export function ActiveIntentPanel({ intents, unprotectedPositions }: ActiveIntentPanelProps) {
  const [, forceUpdate] = useState(0)

  useEffect(() => {
    if (intents.length === 0 && unprotectedPositions.length === 0) return
    const interval = setInterval(() => {
      forceUpdate((prev) => prev + 1)
    }, 1000)
    return () => clearInterval(interval)
  }, [intents.length, unprotectedPositions.length])

  const isUnprotected = (intentId: string) =>
    unprotectedPositions.some((position) => position.intent_id === intentId)

  if (intents.length === 0) {
    return (
      <div id="active-intent-panel" className="watchdog-panel">
        <div className="watchdog-panel-header mb-0">
          <div>
            <div className="watchdog-panel-kicker">Exposure</div>
            <div className="watchdog-panel-title">Active Intents</div>
          </div>
          <div className="watchdog-panel-meta text-emerald-300">0 live</div>
        </div>
        <div className="mt-2 rounded-lg border border-emerald-800/50 bg-emerald-950/10 px-3 py-2 text-xs text-emerald-200">
          No active intent exposure observed.
        </div>
      </div>
    )
  }

  return (
    <div id="active-intent-panel" className="watchdog-panel">
      <div className="watchdog-panel-header">
        <div>
          <div className="watchdog-panel-kicker">Exposure</div>
          <div className="watchdog-panel-title">Active Intents</div>
        </div>
        <div className="watchdog-panel-meta">{intents.length} live</div>
      </div>
      <div className="overflow-x-auto rounded-xl border border-gray-700/70">
        <table className="watchdog-panel-table">
          <thead>
            <tr>
              <th className="px-2 py-2 text-left">Stream</th>
              <th className="px-2 py-2 text-left">Dir</th>
              <th className="px-2 py-2 text-left">Qty</th>
              <th className="px-2 py-2 text-left">Filled</th>
              <th className="px-2 py-2 text-left">Remaining</th>
              <th className="px-2 py-2 text-left">Protected</th>
              <th className="px-2 py-2 text-left">Since</th>
            </tr>
          </thead>
          <tbody>
            {intents.map((intent) => {
              const unprotected = isUnprotected(intent.intent_id)
              const unprotectedEntry = unprotectedPositions.find((p) => p.intent_id === intent.intent_id)
              return (
                <tr key={intent.intent_id} className="border-b border-gray-700/70">
                  <td className="px-2 py-2 font-mono text-gray-200">{intent.stream_id}</td>
                  <td className="px-2 py-2">
                    <span
                      className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${
                        intent.direction === 'Long' ? 'bg-green-700 text-white' : 'bg-red-700 text-white'
                      }`}
                    >
                      {intent.direction}
                    </span>
                  </td>
                  <td className="px-2 py-2 text-gray-300">{intent.quantity || 0}</td>
                  <td className="px-2 py-2 text-gray-300">{intent.entry_filled_qty || 0}</td>
                  <td className="px-2 py-2 text-gray-300">{intent.remaining_exposure || 0}</td>
                  <td className="px-2 py-2">
                    {unprotected ? (
                      <span className="blink rounded-full bg-red-500/15 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-red-300">
                        Unprotected
                      </span>
                    ) : (
                      <span className="rounded-full bg-green-500/15 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-green-300">
                        Protected
                      </span>
                    )}
                  </td>
                  <td className="px-2 py-2 font-mono text-xs text-gray-400">
                    {unprotectedEntry?.entry_filled_at_chicago
                      ? formatChicagoTime(unprotectedEntry.entry_filled_at_chicago)
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
