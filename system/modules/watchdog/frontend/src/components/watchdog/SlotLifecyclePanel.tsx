/**
 * SlotLifecyclePanel - Forced flatten, reentry, slot expiry per stream
 */
import type { SlotLifecycleSlot } from '../../services/watchdogApi'

function flattenCell(slot: SlotLifecycleSlot): { text: string; color: string } {
  const completed = slot.flatten_completed_time
  const triggered = slot.flatten_triggered_time
  if (completed) return { text: `Done ${completed}`, color: 'text-green-400' }
  if (triggered) return { text: `Triggered ${triggered}`, color: 'text-amber-400' }
  return { text: '-', color: 'text-gray-500' }
}

function reentryCell(slot: SlotLifecycleSlot): { text: string; color: string } {
  const filled = slot.reentry_filled_time
  const submitted = slot.reentry_submitted_time
  if (filled) return { text: `Filled ${filled}`, color: 'text-green-400' }
  if (submitted) return { text: `Submitted ${submitted}`, color: 'text-amber-400' }
  if (slot.status === 'BLOCKED') return { text: 'Blocked', color: 'text-red-400' }
  return { text: 'Pending', color: 'text-gray-500' }
}

function expiryCell(slot: SlotLifecycleSlot): { text: string; color: string } {
  const expiry = slot.slot_expiry_time
  if (expiry) return { text: `Done ${expiry}`, color: 'text-green-400' }
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
      <div className="watchdog-panel">
        <div className="watchdog-panel-header">
          <div>
            <div className="watchdog-panel-kicker">Lifecycle</div>
            <div className="watchdog-panel-title">Slot Lifecycle</div>
          </div>
        </div>
        <div className="text-sm text-gray-500">Loading...</div>
      </div>
    )
  }

  if (slots.length === 0) {
    return (
      <div className="watchdog-panel">
        <div className="watchdog-panel-header">
          <div>
            <div className="watchdog-panel-kicker">Lifecycle</div>
            <div className="watchdog-panel-title">Slot Lifecycle</div>
          </div>
        </div>
        <div className="text-sm text-gray-500">No slot lifecycle events yet</div>
      </div>
    )
  }

  return (
    <div className="watchdog-panel">
      <div className="watchdog-panel-header">
        <div>
          <div className="watchdog-panel-kicker">Lifecycle</div>
          <div className="watchdog-panel-title">Slot Lifecycle</div>
        </div>
        <div className="watchdog-panel-meta">{slots.length} slots</div>
      </div>
      <div className="overflow-x-auto rounded-xl border border-gray-700/70">
        <table className="watchdog-panel-table">
          <thead>
            <tr>
              <th className="px-2 py-2 text-left">Stream</th>
              <th className="px-2 py-2 text-left">Slot</th>
              <th className="px-2 py-2 text-left">Flatten</th>
              <th className="px-2 py-2 text-left">Reentry</th>
              <th className="px-2 py-2 text-left">Expiry</th>
              <th className="px-2 py-2 text-left">Status</th>
            </tr>
          </thead>
          <tbody>
            {slots.map((slot) => {
              const flatten = flattenCell(slot)
              const reentry = reentryCell(slot)
              const expiry = expiryCell(slot)
              return (
                <tr
                  key={`${slot.stream}-${slot.slot_time}-${slot.trading_date}`}
                  className="border-b border-gray-700/70"
                >
                  <td className="px-2 py-2 font-mono text-xs text-gray-200">{slot.stream}</td>
                  <td className="px-2 py-2 font-mono text-xs text-gray-300">{slot.slot_time || '-'}</td>
                  <td className={`px-2 py-2 text-xs ${flatten.color}`}>{flatten.text}</td>
                  <td className={`px-2 py-2 text-xs ${reentry.color}`}>{reentry.text}</td>
                  <td className={`px-2 py-2 text-xs ${expiry.color}`}>{expiry.text}</td>
                  <td className="px-2 py-2">
                    <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${statusBadgeColor(slot.status)}`}>
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
