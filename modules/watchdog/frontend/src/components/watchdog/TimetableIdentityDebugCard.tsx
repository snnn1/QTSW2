/**
 * Debug / status: explicit timetable hash families (publisher vs content) for operators.
 */
import type { WatchdogStatus } from '../../types/watchdog'
import { CopyableText } from '../ui/CopyableText'

function row(label: string, value: string | null | undefined, mono = true) {
  const display = value == null || value === '' ? '—' : value
  return (
    <div className="flex flex-col gap-0.5 text-xs">
      <span className="text-gray-500">{label}</span>
      {mono && display !== '—' ? (
        <CopyableText text={display} className="font-mono text-[11px] break-all" />
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

  return (
    <div className="bg-gray-900 border border-gray-700 rounded-lg p-4 space-y-3">
      <h3 className="text-sm font-semibold text-gray-200">Timetable identity (debug)</h3>
      <p className="text-[11px] text-gray-500 leading-snug">
        Drift uses publisher identity vs robot heartbeat only. Content hash is separate.
      </p>
      <div className="space-y-2">
        {row('session_trading_date', status.session_trading_date ?? null)}
        {row('timetable_publisher_hash', publisher)}
        {row('timetable_content_hash', content)}
        {row('robot_timetable_hash', status.robot_timetable_hash ?? null)}
        {row('timetable_source', status.timetable_source ?? null)}
        {row('timetable_last_ok_utc', status.timetable_last_ok_utc ?? null)}
      </div>
    </div>
  )
}
