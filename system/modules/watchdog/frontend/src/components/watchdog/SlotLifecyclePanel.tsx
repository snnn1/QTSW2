/**
 * SlotLifecyclePanel - Forced flatten, reentry, slot expiry per stream
 */
import type { SlotLifecycleSlot } from '../../services/watchdogApi'

function flattenCell(slot: SlotLifecycleSlot): { text: string; color: string } {
  const completed = slot.flatten_completed_time
  const triggered = slot.flatten_triggered_time
  if (completed) return { text: `✔ ${completed}`, color: 'text-green-400' }
  if (triggered) return { text: `✔ ${triggered}`, color: 'text-amber-400' }
  return { text: '—', color: 'text-gray-500' }
}

function reentryCell(slot: SlotLifecycleSlot): { text: string; color: string } {
  const filled = slot.reentry_filled_time
  const submitted = slot.reentry_submitted_time
  if (filled) return { text: `✔ ${filled}`, color: 'text-green-400' }
  if (submitted) return { text: `✔ ${submitted}`, color: 'text-amber-400' }
  if (slot.status === 'BLOCKED') return { text: 'Blocked', color: 'text-red-400' }
  return { text: 'Pending', color: 'text-gray-500' }
}

function expiryCell(slot: SlotLifecycleSlot): { text: string; color: string } {
  const expiry = slot.slot_expiry_time
  if (expiry) return { text: `✔ ${expiry}`, color: 'text-green-400' }
  return { text: 'Pending', color: 'text-gray-500' }
}

function statusBadgeColor(status: string): string {
  switch (status) {
    case 'ACTIVE':
      return 'bg-blue-600 text-white'
    case 'FLATTENED':
      return 'bg-amber-600 text-white'
    case 'REENTERED':
      return 'bg-green-600 text-white'
    case 'EXPIRED':
      return 'bg-gray-600 text-white'
    case 'BLOCKED':
      return 'bg-red-600 text-white'
    case 'ERROR':
      return 'bg-red-700 text-white'
    default:
      return 'bg-gray-600 text-white'
  }
}

interface SlotLifecyclePanelProps {
  slots: SlotLifecycleSlot[]
  loading?: boolean
}

export function SlotLifecyclePanel({ slots, loading }: SlotLifecyclePanelProps) {
  if (loading) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">Slot Lifecycle</div>
        <div className="text-sm text-gray-500">Loading...</div>
      </div>
    )
  }
  if (slots.length === 0) {
    return (
      <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
        <div className="text-sm font-semibold text-gray-300 mb-2">Slot Lifecycle</div>
        <div className="text-sm text-gray-500">No slot lifecycle events yet</div>
      </div>
    )
  }

  return (
    <div className="rounded-lg p-4 border bg-gray-800 border-gray-700">
      <div className="text-sm font-semibold text-gray-300 mb-2">
        Slot Lifecycle
        <span className="ml-2 text-xs font-normal text-gray-500">
          {slots.length} slot(s)
        </span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-gray-700 sticky top-0">
            <tr>
              <th className="px-2 py-1 text-left">Stream</th>
              <th className="px-2 py-1 text-left">Slot</th>
              <th className="px-2 py-1 text-left">Flatten</th>
              <th className="px-2 py-1 text-left">Reentry</th>
              <th className="px-2 py-1 text-left">Expiry</th>
              <th className="px-2 py-1 text-left">Status</th>
            </tr>
          </thead>
          <tbody>
            {slots.map((slot) => {
              const flatten = flattenCell(slot)
              const reentry = reentryCell(slot)
              const expiry = expiryCell(slot)
              return (
                <tr key={`${slot.stream}-${slot.slot_time}-${slot.trading_date}`} className="border-b border-gray-700">
                  <td className="px-2 py-1 font-mono text-xs">{slot.stream}</td>
                  <td className="px-2 py-1 font-mono text-xs">{slot.slot_time || '—'}</td>
                  <td className={`px-2 py-1 text-xs ${flatten.color}`}>{flatten.text}</td>
                  <td className={`px-2 py-1 text-xs ${reentry.color}`}>{reentry.text}</td>
                  <td className={`px-2 py-1 text-xs ${expiry.color}`}>{expiry.text}</td>
                  <td className="px-2 py-1">
                    <span className={`px-1 py-0.5 rounded text-xs ${statusBadgeColor(slot.status)}`}>
                      {slot.status}
                    </span>
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
