/**
 * DisconnectFeedCard - Chronological list of connection events for current session
 */
import { memo, useMemo } from 'react'
import { formatChicagoDateTime } from '../../utils/timeUtils.ts'
import { getEventLevel } from '../../utils/eventUtils'
import type { WatchdogEvent } from '../../types/watchdog'

const DISCONNECT_EVENT_TYPES = new Set([
  'CONNECTION_LOST',
  'CONNECTION_LOST_SUSTAINED',
  'CONNECTION_RECOVERED',
  'CONNECTION_RECOVERED_NOTIFICATION',
  'CONNECTIVITY_INCIDENT',
])

const MAX_DISCONNECT_EVENTS = 50

interface DisconnectFeedCardProps {
  events: WatchdogEvent[]
  tradingDate: string | null
}

function extractDisconnectMessage(event: WatchdogEvent): string {
  const data = event.data || {}
  const connName = data.connection_name || 'Unknown'
  const elapsed = data.elapsed_seconds ?? data.elapsed

  switch (event.event_type) {
    case 'CONNECTION_LOST':
      return `Connection lost: ${connName}`
    case 'CONNECTION_LOST_SUSTAINED':
      return elapsed != null
        ? `Sustained (${Math.round(Number(elapsed))}s): ${connName}`
        : `Sustained: ${connName}`
    case 'CONNECTION_RECOVERED':
    case 'CONNECTION_RECOVERED_NOTIFICATION':
      return `Recovered: ${connName}`
    case 'CONNECTIVITY_INCIDENT':
      return data.trigger === 'disconnect_count_5_in_hour'
        ? `5+ disconnects in 1 hour (${data.disconnect_count_in_window ?? '?'} detected)`
        : data.trigger === 'disconnect_duration_120s'
          ? `Single disconnect >120s (${data.disconnect_duration_seconds ?? '?'}s)`
          : 'Connectivity incident'
    default:
      return event.event_type.replace(/_/g, ' ').toLowerCase()
  }
}

function DisconnectFeedCardComponent({ events, tradingDate }: DisconnectFeedCardProps) {
  const filteredEvents = useMemo(() => {
    let list = events.filter((e) => DISCONNECT_EVENT_TYPES.has(e.event_type))

    if (tradingDate) {
      list = list.filter((e) => {
        const evDate = e.trading_date ?? (e.data as Record<string, unknown>)?.trading_date
        return evDate === tradingDate
      })
    }

    const sorted = [...list].sort((a, b) => {
      const tsA = a.timestamp_utc || a.timestamp_chicago || ''
      const tsB = b.timestamp_utc || b.timestamp_chicago || ''
      return tsA.localeCompare(tsB)
    })

    return sorted.slice(-MAX_DISCONNECT_EVENTS)
  }, [events, tradingDate])

  const getLevelBadgeColor = (level: string) => {
    switch (level) {
      case 'ERROR':
        return 'bg-red-600 text-white'
      case 'WARN':
        return 'bg-amber-500 text-black'
      case 'INFO':
        return 'bg-blue-600 text-white'
      default:
        return 'bg-gray-600 text-white'
    }
  }

  if (filteredEvents.length === 0) {
    return (
      <div className="watchdog-panel">
        <div className="watchdog-panel-header">
          <div>
            <div className="watchdog-panel-kicker">Feed</div>
            <div className="watchdog-panel-title">Disconnect Feed</div>
          </div>
          <div className="watchdog-panel-meta">No events</div>
        </div>
        <div className="py-6 text-center text-sm text-gray-500">No disconnects this session</div>
      </div>
    )
  }

  return (
    <div className="watchdog-panel">
      <div className="watchdog-panel-header">
        <div>
          <div className="watchdog-panel-kicker">Feed</div>
          <div className="watchdog-panel-title">Disconnect Feed</div>
        </div>
        <div className="watchdog-panel-meta">{tradingDate ?? 'Current session'}</div>
      </div>
      <div className="max-h-64 overflow-y-auto rounded-xl border border-gray-700/70">
        <table className="watchdog-panel-table">
          <thead className="sticky top-0">
            <tr>
              <th className="px-2 py-2 text-left">Time</th>
              <th className="px-2 py-2 text-left">Level</th>
              <th className="px-2 py-2 text-left">Event</th>
              <th className="px-2 py-2 text-left">Message</th>
            </tr>
          </thead>
          <tbody>
            {filteredEvents.map((event) => {
              const level = getEventLevel(event.event_type)
              const message = extractDisconnectMessage(event)
              return (
                <tr
                  key={`${event.run_id}:${event.event_seq}`}
                  className="border-b border-gray-700/70 text-sm"
                >
                  <td className="px-2 py-2 font-mono text-xs text-gray-300">
                    {formatChicagoDateTime(event.timestamp_chicago || event.timestamp_utc)}
                  </td>
                  <td className="px-2 py-2">
                    <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${getLevelBadgeColor(level)}`}>
                      {level}
                    </span>
                  </td>
                  <td className="px-2 py-2 text-xs text-gray-300">{event.event_type}</td>
                  <td className="max-w-[220px] truncate px-2 py-2 text-xs text-gray-400" title={message}>
                    {message}
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

export const DisconnectFeedCard = memo(DisconnectFeedCardComponent)
DisconnectFeedCard.displayName = 'DisconnectFeedCard'
