/**
 * Phase 3: Minimal Operator Console
 *
 * Action-first console answering at a glance:
 * - Is the system safe?
 * - Which instruments need action?
 * - What exact action is required?
 */
import { useMemo, useState } from 'react'
import { useOperatorSnapshot } from './hooks/useOperatorSnapshot'
import { useWatchdogStatus } from './hooks/useWatchdogStatus'
import { WatchdogNavigationBar } from './components/WatchdogNavigationBar'
import type { OperatorSnapshotInstrument } from './services/watchdogApi'

const STATUS_ORDER = ['CRITICAL', 'WARNING', 'SAFE'] as const
const ACTION_PRIORITY: Record<string, number> = {
  FLATTEN: 0,
  RESTART: 1,
  WAIT: 2,
  NONE: 3,
}

const STALENESS_THRESHOLD_MS = 30_000 // 30 seconds

function sortedInstruments(
  snapshot: Record<string, OperatorSnapshotInstrument>
): [string, OperatorSnapshotInstrument][] {
  const entries = Object.entries(snapshot)
  return entries.sort(([a, instA], [b, instB]) => {
    const statusA = STATUS_ORDER.indexOf(instA.status as (typeof STATUS_ORDER)[number])
    const statusB = STATUS_ORDER.indexOf(instB.status as (typeof STATUS_ORDER)[number])
    if (statusA !== statusB) return statusA - statusB
    const actionA = ACTION_PRIORITY[instA.action_required] ?? 4
    const actionB = ACTION_PRIORITY[instB.action_required] ?? 4
    if (actionA !== actionB) return actionA - actionB
    return a.localeCompare(b)
  })
}

function deriveConnectionState(status: { connection_status?: string; recovery_state?: string; derived_connection_state?: string } | null): 'OK' | 'LOST' | 'RECOVERING' {
  if (!status) return 'OK'
  // Prefer authoritative derived_connection_state when available
  const derived = status.derived_connection_state
  if (derived === 'LOST') return 'LOST'
  if (derived === 'RECOVERING') return 'RECOVERING'
  if (derived === 'STABLE') return 'OK'
  // Fallback for older API responses
  if (status.recovery_state === 'RECOVERY_RUNNING') return 'RECOVERING'
  if (status.connection_status === 'ConnectionLost' || status.connection_status === 'ConnectionLostSustained') return 'LOST'
  return 'OK'
}

function deriveRecoveryState(status: { recovery_state?: string } | null): 'NONE' | 'ACTIVE' {
  if (!status) return 'NONE'
  if (status.recovery_state === 'RECOVERY_RUNNING' || status.recovery_state === 'DISCONNECT_FAIL_CLOSED') return 'ACTIVE'
  return 'NONE'
}

/** Banner override: FAIL_CLOSED or CONNECTION_LOST → CRITICAL, RECOVERY → WARNING, else derive from instruments */
function deriveOverallStatus(
  status: { connection_status?: string; recovery_state?: string; derived_connection_state?: string } | null,
  snapshot: Record<string, OperatorSnapshotInstrument> | null
): 'SAFE' | 'WARNING' | 'CRITICAL' | 'UNKNOWN' {
  const connState = deriveConnectionState(status)
  const recoveryState = deriveRecoveryState(status)
  if (status?.recovery_state === 'DISCONNECT_FAIL_CLOSED' || connState === 'LOST') return 'CRITICAL'
  if (recoveryState === 'ACTIVE' || connState === 'RECOVERING') return 'WARNING'
  if (!snapshot || Object.keys(snapshot).length === 0) return 'SAFE'
  const statuses = Object.values(snapshot).map((s) => s.status)
  if (statuses.includes('CRITICAL')) return 'CRITICAL'
  if (statuses.includes('WARNING')) return 'WARNING'
  return 'SAFE'
}

/** Display label: WAIT → WAIT (SYSTEM) */
function displayActionLabel(label: string): string {
  return label === 'WAIT' ? 'WAIT (SYSTEM)' : label
}

function InstrumentCard({
  instrument,
  data,
  expanded,
  onToggle,
}: {
  instrument: string
  data: OperatorSnapshotInstrument
  expanded: boolean
  onToggle: () => void
}) {
  const statusColors = {
    CRITICAL: 'border-red-500 bg-red-950/30',
    WARNING: 'border-amber-500 bg-amber-950/20',
    SAFE: 'border-emerald-600/50 bg-emerald-950/10',
  }
  const statusColor = statusColors[data.status] ?? 'border-gray-600 bg-gray-900/50'
  const actionLabel = displayActionLabel(data.action_label)

  return (
    <div className={`rounded-lg border-l-4 p-3 ${statusColor}`}>
      <div className="flex items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
          {/* ACTION first (primary visual) */}
          <span className={`font-bold ${
            data.action_label === 'FLATTEN NOW' ? 'text-red-400' :
            data.action_label === 'RESTART ROBOT' ? 'text-red-400' :
            data.action_label === 'WAIT' ? 'text-amber-400' :
            'text-gray-400'
          }`}>
            {actionLabel}
          </span>
          <span className="font-mono font-semibold">{instrument}</span>
          <span className={`rounded px-1.5 py-0.5 text-xs font-medium ${
            data.status === 'CRITICAL' ? 'bg-red-600 text-white' :
            data.status === 'WARNING' ? 'bg-amber-600 text-white' :
            'bg-emerald-700/60 text-emerald-100'
          }`}>
            {data.status}
          </span>
          <span className="text-gray-400">exposure: {data.exposure.quantity}</span>
          <span className="text-gray-400">ownership: {data.ownership}</span>
          <span className="text-gray-400">protectives: {data.protectives}</span>
          <span className="text-gray-500 text-xs">({data.confidence})</span>
        </div>
        <button
          type="button"
          onClick={onToggle}
          className="text-xs text-gray-500 hover:text-gray-300"
        >
          {expanded ? '▲' : '▼'}
        </button>
      </div>
      {expanded && (
        <div className="mt-2 border-t border-gray-700/50 pt-2 text-xs text-gray-500">
          system_state: {data.system_state} · last_event: {data.last_event}
        </div>
      )}
    </div>
  )
}

export function OperatorConsolePage() {
  const { snapshot, loading, error, lastSuccessfulFetchTimestamp } = useOperatorSnapshot()
  const { status } = useWatchdogStatus()
  const [expanded, setExpanded] = useState<Set<string>>(new Set())

  const sorted = useMemo(() => {
    if (!snapshot) return []
    return sortedInstruments(snapshot)
  }, [snapshot])

  const overallStatus = useMemo(
    () => deriveOverallStatus(status, snapshot),
    [status, snapshot]
  )

  const { criticalActions, waitingCount } = useMemo(() => {
    if (!snapshot) return { criticalActions: 0, waitingCount: 0 }
    let critical = 0
    let waiting = 0
    for (const s of Object.values(snapshot)) {
      if (s.action_required === 'FLATTEN' || s.action_required === 'RESTART') critical++
      else if (s.action_required === 'WAIT') waiting++
    }
    return { criticalActions: critical, waitingCount: waiting }
  }, [snapshot])

  const isStale = useMemo(() => {
    if (!lastSuccessfulFetchTimestamp) return false
    return Date.now() - lastSuccessfulFetchTimestamp > STALENESS_THRESHOLD_MS
  }, [lastSuccessfulFetchTimestamp])

  const connectionState = deriveConnectionState(status)
  const recoveryState = deriveRecoveryState(status)

  const toggleExpand = (inst: string) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(inst)) next.delete(inst)
      else next.add(inst)
      return next
    })
  }

  // Fix 5: API failure → UNKNOWN / LOADING
  if (loading && !snapshot) {
    return (
      <div className="min-h-screen bg-black text-white">
        <WatchdogNavigationBar />
        <div className="container mx-auto px-4 pt-24 pb-8">
          <div className="text-center text-gray-400 py-12">Loading operator snapshot...</div>
        </div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="min-h-screen bg-black text-white">
        <WatchdogNavigationBar />
        <div className="container mx-auto px-4 pt-24 pb-8">
          <div className="rounded-lg border border-amber-600 bg-amber-950/20 p-4">
            <div className="font-medium text-amber-200">API failure — no data</div>
            <div className="text-sm text-amber-200/80 mt-1">{error}</div>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="min-h-screen bg-black text-white">
      <WatchdogNavigationBar />
      <div className="container mx-auto px-4 pt-24 pb-8 max-w-4xl">
        {/* Fix 6: Staleness indicator */}
        {isStale && (
          <div className="rounded-lg border border-amber-600 bg-amber-950/20 p-2 mb-4 text-amber-200 text-sm">
            Snapshot may be stale (last update {lastSuccessfulFetchTimestamp ? new Date(lastSuccessfulFetchTimestamp).toLocaleTimeString() : '—'})
          </div>
        )}

        {/* Global status banner */}
        <div
          className={`rounded-lg border-l-4 p-4 mb-6 ${
            overallStatus === 'CRITICAL'
              ? 'border-red-500 bg-red-950/30'
              : overallStatus === 'WARNING'
              ? 'border-amber-500 bg-amber-950/20'
              : overallStatus === 'UNKNOWN'
              ? 'border-gray-500 bg-gray-900/50'
              : 'border-emerald-600 bg-emerald-950/10'
          }`}
        >
          <div className="flex flex-wrap items-center gap-x-6 gap-y-2">
            <div>
              <span className="text-xs text-gray-400 uppercase tracking-wider">System</span>
              <div className={`text-xl font-bold ${
                overallStatus === 'CRITICAL' ? 'text-red-300' :
                overallStatus === 'WARNING' ? 'text-amber-300' :
                overallStatus === 'UNKNOWN' ? 'text-gray-400' :
                'text-emerald-300'
              }`}>
                {overallStatus}
              </div>
            </div>
            <div>
              <span className="text-xs text-gray-400 uppercase tracking-wider">Connection</span>
              <div className={`font-medium ${
                connectionState === 'OK' ? 'text-emerald-400' :
                connectionState === 'LOST' ? 'text-red-400' :
                'text-amber-400'
              }`}>
                {connectionState}
              </div>
            </div>
            <div>
              <span className="text-xs text-gray-400 uppercase tracking-wider">Recovery</span>
              <div className="font-medium text-gray-300">{recoveryState}</div>
            </div>
            <div>
              <span className="text-xs text-gray-400 uppercase tracking-wider">Critical Actions</span>
              <div className={`font-bold ${criticalActions > 0 ? 'text-red-400' : 'text-gray-500'}`}>
                {criticalActions}
              </div>
            </div>
            <div>
              <span className="text-xs text-gray-400 uppercase tracking-wider">Waiting</span>
              <div className={`font-bold ${waitingCount > 0 ? 'text-amber-400' : 'text-gray-500'}`}>
                {waitingCount}
              </div>
            </div>
            {lastSuccessfulFetchTimestamp && (
              <div className="ml-auto text-xs text-gray-500">
                Updated {new Date(lastSuccessfulFetchTimestamp).toLocaleTimeString()}
              </div>
            )}
          </div>
        </div>

        {/* Instrument cards — Fix 5: Empty instruments → SAFE, show calm state */}
        {sorted.length === 0 ? (
          <div className="rounded-lg border border-gray-700 bg-gray-900/30 p-8 text-center text-gray-500">
            No active instruments
          </div>
        ) : (
          <div className="space-y-2">
            {sorted.map(([inst, data]) => (
              <InstrumentCard
                key={inst}
                instrument={inst}
                data={data}
                expanded={expanded.has(inst)}
                onToggle={() => toggleExpand(inst)}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
