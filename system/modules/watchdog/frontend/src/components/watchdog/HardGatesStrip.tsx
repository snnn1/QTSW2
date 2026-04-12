/**
 * Compact live safety / control strip (from GET /status + /risk-gates + slot lifecycle).
 */
import type { RiskGateStatus, WatchdogStatus } from '../../types/watchdog'
import type { SlotLifecycleSlot } from '../../services/watchdogApi'

export interface HardGatesStripProps {
  status: WatchdogStatus | null
  slotLifecycle: SlotLifecycleSlot[]
}

export function HardGatesStrip({ status, slotLifecycle }: HardGatesStripProps) {
  const tradingAllowed = Boolean(status?.execution_safe)
  const killOn = Boolean(status?.kill_switch_active)
  const recon = status?.reconciliation_gate_state ?? 'OK'
  const mismatchActive = recon === 'ENGAGED' || recon === 'FAIL_CLOSED'
  const recoveryActive = (status?.recovery_state ?? '') === 'RECOVERY_RUNNING'
  const flattenInProgress = slotLifecycle.some(
    (sl) => Boolean(sl.flatten_triggered_time) && !sl.flatten_completed_time
  )

  return (
    <div className="rounded-lg border border-gray-700 bg-gray-900/60 p-3">
      <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500">Live safety</div>
      <div className="flex flex-wrap gap-2">
        <span
          className={`rounded px-2 py-1 text-xs font-medium ${
            tradingAllowed ? 'bg-emerald-900/50 text-emerald-200' : 'bg-red-900/50 text-red-200'
          }`}
        >
          Trading allowed: {tradingAllowed ? 'YES' : 'NO'}
        </span>
        <span
          className={`rounded px-2 py-1 text-xs font-medium ${
            !killOn ? 'bg-emerald-900/50 text-emerald-200' : 'bg-red-900/50 text-red-200'
          }`}
        >
          Kill switch: {killOn ? 'ON' : 'off'}
        </span>
        <span
          className={`rounded px-2 py-1 text-xs font-medium ${
            !mismatchActive
              ? 'bg-emerald-900/50 text-emerald-200'
              : recon === 'FAIL_CLOSED'
                ? 'bg-red-900/50 text-red-200'
                : 'bg-amber-900/50 text-amber-200'
          }`}
        >
          Mismatch active: {mismatchActive ? (recon === 'FAIL_CLOSED' ? 'FAIL_CLOSED' : 'yes') : 'no'}
        </span>
        <span
          className={`rounded px-2 py-1 text-xs font-medium ${
            recoveryActive ? 'bg-amber-900/50 text-amber-100' : 'bg-emerald-900/50 text-emerald-200'
          }`}
        >
          Recovery active: {recoveryActive ? 'yes' : 'no'}
        </span>
        <span
          className={`rounded px-2 py-1 text-xs font-medium ${
            flattenInProgress ? 'bg-amber-900/50 text-amber-100' : 'bg-emerald-900/50 text-emerald-200'
          }`}
        >
          Flatten in progress: {flattenInProgress ? 'yes' : 'no'}
        </span>
      </div>
      {!status && <div className="mt-2 text-xs text-gray-500">Waiting for status…</div>}
    </div>
  )
}
