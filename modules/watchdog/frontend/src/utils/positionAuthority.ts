/**
 * Display-only helpers for POSITION_AUTHORITY_EVALUATED (robot is source of truth).
 * Do not use for gating or severity — observability only.
 */
import type { PositionAuthoritySnapshot } from '../types/watchdog'

/** If last event is older than this, UI shows STALE (avoids false confidence). */
export const AUTHORITY_STALE_MS = 8000

/** Badge label only updates after state is stable this long (reduces REAL/UNKNOWN flicker). */
export const AUTHORITY_DEBOUNCE_MS = 750

export function parseAuthorityUtcMs(iso: string | undefined): number | null {
  if (!iso || typeof iso !== 'string') return null
  const t = Date.parse(iso)
  return Number.isFinite(t) ? t : null
}

/** True when we should not treat authority_state as fresh (UI-only). */
export function isAuthorityStale(lastAuthorityTsUtc: string | undefined, nowMs: number = Date.now()): boolean {
  const ms = parseAuthorityUtcMs(lastAuthorityTsUtc)
  if (ms === null) return true
  return nowMs - ms > AUTHORITY_STALE_MS
}

export function positionAuthorityBadgeClass(
  authorityState: string | undefined,
  opts?: { stale?: boolean; unverified?: boolean },
): string {
  if (opts?.stale) {
    return 'bg-gray-600 text-gray-200 ring-1 ring-gray-500'
  }
  if (opts?.unverified) {
    return 'bg-amber-900/80 text-amber-100 ring-1 ring-amber-500'
  }
  const u = (authorityState ?? '').toUpperCase()
  if (u === 'REAL') return 'bg-emerald-700 text-white'
  if (u === 'RECOVERY') return 'bg-yellow-500 text-gray-900'
  if (u === 'UNKNOWN') return 'bg-red-700 text-white'
  return 'bg-gray-600 text-gray-300'
}

/** Human-readable interpretation for tooltips / panels (not used in automation). */
export function positionAuthorityInterpretation(authorityState: string | undefined): string {
  const u = (authorityState ?? '').toUpperCase()
  if (u === 'REAL') return 'Position explained by real (non-recovery) journal rows.'
  if (u === 'RECOVERY') return 'Broker position explained partly by recovery journal rows.'
  if (u === 'UNKNOWN') return 'Position not cleanly explained by journal vs broker.'
  return 'No authority snapshot for this instrument yet.'
}

export function positionAuthorityDetailTitle(pa: PositionAuthoritySnapshot): string {
  const stale = isAuthorityStale(pa.last_authority_ts_utc)
  const lines = [
    stale
      ? `STALE: last event > ${AUTHORITY_STALE_MS / 1000}s ago — do not treat as live truth.`
      : 'Fresh (within staleness window).',
    '',
    positionAuthorityInterpretation(pa.authority_state),
    '',
    `instrument=${pa.instrument ?? ''}`,
    `attachment=${pa.attachment_status ?? 'n/a'}`,
    `broker_qty=${pa.broker_qty ?? ''}`,
    `real_open_qty=${pa.real_open_qty ?? ''}`,
    `recovery_open_qty=${pa.recovery_open_qty ?? ''}`,
    `journal_open_qty=${pa.journal_open_qty ?? ''}`,
    `last_authority_ts_utc=${pa.last_authority_ts_utc ?? ''}`,
  ]
  return lines.join('\n')
}

/** Stable serialize for debounce dependency (identity of displayed authority). */
export function authoritySnapshotKey(pa: PositionAuthoritySnapshot | null | undefined): string {
  if (!pa) return ''
  return [
    pa.authority_state ?? '',
    pa.attachment_status ?? '',
    pa.last_authority_ts_utc ?? '',
    pa.instrument ?? '',
  ].join('|')
}
