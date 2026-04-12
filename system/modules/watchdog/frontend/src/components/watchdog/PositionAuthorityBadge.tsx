/**
 * Debounced authority badge + staleness + UNVERIFIED styling (display-only).
 * First snapshot for a row applies immediately; subsequent changes wait AUTHORITY_DEBOUNCE_MS (reduces flicker).
 */
import { useEffect, useRef, useState } from 'react'
import type { PositionAuthoritySnapshot } from '../../types/watchdog'
import {
  AUTHORITY_DEBOUNCE_MS,
  authoritySnapshotKey,
  isAuthorityStale,
  positionAuthorityBadgeClass,
  positionAuthorityDetailTitle,
} from '../../utils/positionAuthority.ts'

type Props = {
  snapshot: PositionAuthoritySnapshot | null | undefined
  /** Re-render staleness periodically so STALE appears without user action */
  tickMs?: number
}

export function PositionAuthorityBadge({ snapshot, tickMs = 2000 }: Props) {
  const [debounced, setDebounced] = useState(snapshot)
  const [nowMs, setNowMs] = useState(() => Date.now())
  const hadDataKey = useRef(false)

  useEffect(() => {
    const k = authoritySnapshotKey(snapshot)
    if (!k) {
      setDebounced(snapshot)
      hadDataKey.current = false
      return
    }
    if (!hadDataKey.current) {
      setDebounced(snapshot)
      hadDataKey.current = true
      return
    }
    const t = window.setTimeout(() => setDebounced(snapshot), AUTHORITY_DEBOUNCE_MS)
    return () => clearTimeout(t)
  }, [authoritySnapshotKey(snapshot)])

  useEffect(() => {
    const id = window.setInterval(() => setNowMs(Date.now()), tickMs)
    return () => clearInterval(id)
  }, [tickMs])

  const pa = debounced
  if (!pa?.authority_state && !pa?.last_authority_ts_utc) {
    return <span className="text-gray-500 text-xs">—</span>
  }

  const stale = isAuthorityStale(pa.last_authority_ts_utc, nowMs)
  const unverified = pa.attachment_status === 'UNVERIFIED'
  const label = stale ? 'STALE' : String(pa.authority_state ?? '—')

  return (
    <span
      className={`inline-block px-1.5 py-0.5 rounded text-[10px] font-semibold ${positionAuthorityBadgeClass(
        pa.authority_state,
        { stale, unverified: unverified && !stale },
      )}`}
      title={positionAuthorityDetailTitle(pa)}
    >
      {label}
    </span>
  )
}
