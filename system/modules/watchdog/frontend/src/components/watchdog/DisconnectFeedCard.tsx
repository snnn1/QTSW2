/**
 * DisconnectFeedCard - Chronological list of connection events for current session
 * Shows CONNECTION_LOST, CONNECTION_LOST_SUSTAINED, CONNECTION_RECOVERED, CONNECTIVITY_INCIDENT
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
      <div className="bg-gray-800 rounded-lg p-4">
        <h2 className="text-sm font-semibold text-gray-300 mb-2">Disconnect Feed</h2>
        <div className="text-gray-500 text-center py-6 text-sm">No disconnects this session</div>
      </div>
    )
  }

  return (
    <div className="bg-gray-800 rounded-lg p-4">
      <h2 className="text-sm font-semibold text-gray-300 mb-2">
        Disconnect Feed
        {tradingDate && (
          <span className="ml-2 text-xs font-normal text-gray-500">· {tradingDate}</span>
        )}
      </h2>
      <div className="overflow-y-auto max-h-64">
        <table className="w-full text-sm">
          <thead className="bg-gray-700 sticky top-0">
            <tr>
              <th className="px-2 py-1 text-left text-xs">Time (CT)</th>
              <th className="px-2 py-1 text-left text-xs">Level</th>
              <th className="px-2 py-1 text-left text-xs">Event</th>
              <th className="px-2 py-1 text-left text-xs">Message</th>
            </tr>
          </thead>
          <tbody>
            {filteredEvents.map((event) => {
              const level = getEventLevel(event.event_type)
              const message = extractDisconnectMessage(event)
              return (
                <tr
                  key={`${event.run_id}:${event.event_seq}`}
                  className="border-b border-gray-700"
                >
                  <td className="px-2 py-1 font-mono text-xs">
                    {formatChicagoDateTime(event.timestamp_chicago || event.timestamp_utc)}
                  </td>
                  <td className="px-2 py-1">
                    <span className={`px-1 py-0.5 rounded text-xs ${getLevelBadgeColor(level)}`}>
                      {level}
                    </span>
                  </td>
                  <td className="px-2 py-1 text-xs">{event.event_type}</td>
                  <td className="px-2 py-1 text-xs truncate max-w-[180px]" title={message}>
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
