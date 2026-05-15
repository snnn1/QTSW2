/**
 * Debug/status: explicit timetable hash families (publisher vs content).
 */
import type { WatchdogStatus } from '../../types/watchdog'
import { CopyableText } from '../ui/CopyableText'

function row(label: string, value: string | null | undefined, mono = true) {
  const display = value == null || value === '' ? '-' : value
  return (
    <div className="flex flex-col gap-0.5 text-xs">
      <span className="text-gray-500">{label}</span>
      {mono && display !== '-' ? (
        <CopyableText text={display} className="break-all font-mono text-[11px]" />
      ) : (
        <span className={mono ? 'font-mono' : ''}>{display}</span>
      )}
    </div>
  )
}

export function TimetableIdentityDebugCard({ status }: { status: WatchdogStatus | null | undefined }) {
  if (!status) return null

  const publisher =
    status.timetable_publisher_hash ?? status.current_timetable_hash ?? null
  const content = status.timetable_content_hash ?? null
  const drift = status.timetable_drift === true

  return (
    <details
      className={`rounded-lg border p-4 ${
        drift ? 'border-red-700/70 bg-red-950/25' : 'border-gray-700 bg-gray-900/55'
      }`}
      open={drift}
    >
      <summary className="cursor-pointer text-sm font-semibold text-gray-200">
        Timetable identity {drift ? '(drift)' : '(debug)'}
      </summary>
      <p className="mt-2 text-[11px] leading-snug text-gray-500">
        Drift uses publisher identity vs robot heartbeat. Content hash is separate.
      </p>
      <div className="mt-3 space-y-2">
        {row('session_trading_date', status.session_trading_date ?? null)}
        {row('timetable_publisher_hash', publisher)}
        {row('timetable_content_hash', content)}
        {row('robot_timetable_hash', status.robot_timetable_hash ?? null)}
        {row('timetable_source', status.timetable_source ?? null)}
        {row('timetable_last_ok_utc', status.timetable_last_ok_utc ?? null)}
      </div>
    </details>
  )
}
