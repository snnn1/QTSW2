/**
 * LiveEventFeed component
 * Contained operator timeline with severity/domain filters.
 */
import { memo, useEffect, useMemo, useRef, useState } from 'react'
import { formatChicagoDateTime, formatEventTimestamp } from '../../utils/timeUtils.ts'
import { getEventLevel, extractEventMessage } from '../../utils/eventUtils'
import type { WatchdogEvent } from '../../types/watchdog'

const HIDDEN_EVENT_TYPES = new Set([
  'ENGINE_TIMER_HEARTBEAT',
  'ENGINE_TICK_CALLSITE',
])

type EventFilter = 'critical' | 'warnings' | 'execution' | 'protection' | 'flatten' | 'all'

const FILTERS: Array<{ id: EventFilter; label: string }> = [
  { id: 'critical', label: 'Critical' },
  { id: 'warnings', label: 'Warnings' },
  { id: 'execution', label: 'Execution' },
  { id: 'protection', label: 'Protection' },
  { id: 'flatten', label: 'Flatten/Reentry' },
  { id: 'all', label: 'All' },
]

interface LiveEventFeedProps {
  events: WatchdogEvent[]
  onEventClick: (event: WatchdogEvent) => void
}

function eventLevel(event: WatchdogEvent) {
  return getEventLevel(event.event_type)
}

function eventType(event: WatchdogEvent) {
  return String(event.event_type ?? '').toUpperCase()
}

function eventDomain(event: WatchdogEvent) {
  const type = eventType(event)
  const message = extractEventMessage(event).toUpperCase()
  const combined = `${type} ${message}`
  return {
    execution:
      combined.includes('EXECUTION') ||
      combined.includes('ORDER') ||
      combined.includes('FILL') ||
      combined.includes('INTENT') ||
      combined.includes('ENTRY'),
    protection:
      combined.includes('PROTECT') ||
      combined.includes('STOP') ||
      combined.includes('TARGET') ||
      combined.includes('UNPROTECTED'),
    flatten:
      combined.includes('FLATTEN') ||
      combined.includes('REENTRY') ||
      combined.includes('TIME_EXIT') ||
      combined.includes('SHUTDOWN'),
  }
}

function filterMatches(event: WatchdogEvent, filter: EventFilter) {
  const level = eventLevel(event)
  const domain = eventDomain(event)
  switch (filter) {
    case 'critical':
      return level === 'ERROR'
    case 'warnings':
      return level === 'WARN'
    case 'execution':
      return domain.execution
    case 'protection':
      return domain.protection
    case 'flatten':
      return domain.flatten
    case 'all':
      return true
  }
}

function priority(event: WatchdogEvent) {
  const level = eventLevel(event)
  if (level === 'ERROR') return 0
  if (level === 'WARN') return 1
  return 2
}

function getLevelBadgeColor(level: string) {
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

function LiveEventFeedComponent({ events, onEventClick }: LiveEventFeedProps) {
  const [activeFilter, setActiveFilter] = useState<EventFilter>('critical')
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const lastEventCountRef = useRef(0)
  const shouldAutoScrollRef = useRef(true)

  const displayEvents = useMemo(
    () => (events ?? []).filter((e) => e && !HIDDEN_EVENT_TYPES.has(e.event_type)),
    [events]
  )

  const filterCounts = useMemo(() => {
    return FILTERS.reduce<Record<EventFilter, number>>((acc, filter) => {
      acc[filter.id] = displayEvents.filter((event) => filterMatches(event, filter.id)).length
      return acc
    }, {
      critical: 0,
      warnings: 0,
      execution: 0,
      protection: 0,
      flatten: 0,
      all: 0,
    })
  }, [displayEvents])

  const filteredEvents = useMemo(() => {
    return displayEvents
      .filter((event) => filterMatches(event, activeFilter))
      .sort((a, b) => {
        const priorityDelta = priority(a) - priority(b)
        if (priorityDelta !== 0) return priorityDelta
        return (b.event_seq ?? 0) - (a.event_seq ?? 0)
      })
  }, [activeFilter, displayEvents])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    const handleScroll = () => {
      const isAtBottom = container.scrollHeight - container.scrollTop - container.clientHeight < 50
      shouldAutoScrollRef.current = isAtBottom
    }

    container.addEventListener('scroll', handleScroll)
    return () => container.removeEventListener('scroll', handleScroll)
  }, [])

  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container || filteredEvents.length === 0) return

    if (filteredEvents.length > lastEventCountRef.current && shouldAutoScrollRef.current) {
      requestAnimationFrame(() => {
        if (container) container.scrollTop = 0
      })
    }

    lastEventCountRef.current = filteredEvents.length
  }, [filteredEvents.length])

  return (
    <section className="watchdog-panel min-w-0">
      <div className="watchdog-panel-header">
        <div>
          <div className="watchdog-panel-kicker">Operator Timeline</div>
          <div className="watchdog-panel-title">Event Feed</div>
        </div>
        <div className="watchdog-panel-meta">
          {filteredEvents.length} shown / {displayEvents.length} total
        </div>
      </div>

      <div className="mb-3 flex flex-wrap gap-1.5">
        {FILTERS.map((filter) => (
          <button
            key={filter.id}
            type="button"
            onClick={() => setActiveFilter(filter.id)}
            className={`rounded-full border px-2.5 py-1 text-[11px] font-semibold ${
              activeFilter === filter.id
                ? 'border-cyan-400/70 bg-cyan-500/15 text-cyan-100'
                : 'border-slate-700 bg-slate-900/65 text-slate-400 hover:text-slate-100'
            }`}
          >
            {filter.label} <span className="font-mono">{filterCounts[filter.id]}</span>
          </button>
        ))}
      </div>

      {displayEvents.length === 0 ? (
        <div className="rounded-lg border border-slate-800 bg-slate-900/45 py-8 text-center text-sm text-gray-500">
          No events yet
        </div>
      ) : filteredEvents.length === 0 ? (
        <div className="rounded-lg border border-slate-800 bg-slate-900/45 py-8 text-center text-sm text-gray-500">
          No events match this filter
        </div>
      ) : (
        <div ref={scrollContainerRef} className="max-h-[22rem] overflow-auto rounded-lg border border-slate-800">
          <table className="w-full min-w-[860px] text-sm">
            <thead className="sticky top-0 bg-gray-800/98">
              <tr>
                <th className="px-2 py-1.5 text-left text-[10px] uppercase tracking-wide text-gray-400">Time CT</th>
                <th className="px-2 py-1.5 text-left text-[10px] uppercase tracking-wide text-gray-400">Level</th>
                <th className="px-2 py-1.5 text-left text-[10px] uppercase tracking-wide text-gray-400">Stream</th>
                <th className="px-2 py-1.5 text-left text-[10px] uppercase tracking-wide text-gray-400">Type</th>
                <th className="px-2 py-1.5 text-left text-[10px] uppercase tracking-wide text-gray-400">Message</th>
              </tr>
            </thead>
            <tbody>
              {filteredEvents.map((event) => {
                const level = eventLevel(event)
                const message = extractEventMessage(event)
                const repetitiveCount = (event.data as any)?._repetitive_count
                const firstTs = (event.data as any)?._repetitive_first_timestamp
                const lastTs = (event.data as any)?._repetitive_last_timestamp
                const isCollapsed = repetitiveCount != null && repetitiveCount > 1

                return (
                  <tr
                    key={`${event.run_id}:${event.event_seq}`}
                    onClick={() => onEventClick(event)}
                    className="cursor-pointer border-b border-gray-700/70 hover:bg-gray-700/80"
                  >
                    <td className="px-2 py-1.5 font-mono text-xs">
                      {isCollapsed && firstTs
                        ? `${formatEventTimestamp(firstTs)} - ${formatEventTimestamp(lastTs)}`
                        : formatChicagoDateTime(event.timestamp_chicago)}
                    </td>
                    <td className="px-2 py-1.5">
                      <span className={`rounded px-1 py-0.5 text-xs ${getLevelBadgeColor(level)}`}>
                        {level}
                      </span>
                    </td>
                    <td className="px-2 py-1.5 font-mono text-xs">{event.stream || 'ENGINE'}</td>
                    <td className="px-2 py-1.5 font-mono text-[11px] text-slate-300">
                      {event.event_type}
                      {isCollapsed && (
                        <span className="ml-1.5 text-amber-400 font-semibold" title={`${repetitiveCount} events collapsed`}>
                          [x{repetitiveCount}]
                        </span>
                      )}
                    </td>
                    <td className="px-2 py-1.5 text-xs" title={message}>
                      <span className="block truncate">{message}</span>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

function areEventsEqual(prevProps: LiveEventFeedProps, nextProps: LiveEventFeedProps) {
  if (prevProps.onEventClick !== nextProps.onEventClick) return false

  const prevEvents = prevProps.events ?? []
  const nextEvents = nextProps.events ?? []
  if (prevEvents.length !== nextEvents.length) return false
  if (prevEvents.length === 0) return true

  const prevLast = prevEvents[prevEvents.length - 1]
  const nextLast = nextEvents[nextEvents.length - 1]
  if (!prevLast || !nextLast) return false

  return (
    prevLast.run_id === nextLast.run_id &&
    prevLast.event_seq === nextLast.event_seq &&
    (prevLast.timestamp_chicago || prevLast.timestamp_utc) === (nextLast.timestamp_chicago || nextLast.timestamp_utc) &&
    (prevLast.data as any)?._repetitive_count === (nextLast.data as any)?._repetitive_count
  )
}

export const LiveEventFeed = memo(LiveEventFeedComponent, areEventsEqual)
LiveEventFeed.displayName = 'LiveEventFeed'
