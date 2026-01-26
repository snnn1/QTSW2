/**
 * LiveEventFeed component
 * Scrollable list of last ~200 events, sorted by event_seq ASC
 */
import { memo, useRef, useEffect } from 'react'
import { formatChicagoDateTime } from '../../utils/timeUtils'
import { getEventLevel, extractEventMessage } from '../../utils/eventUtils'
import type { WatchdogEvent } from '../../types/watchdog'

interface LiveEventFeedProps {
  events: WatchdogEvent[]
  onEventClick: (event: WatchdogEvent) => void
}

function LiveEventFeedComponent({ events, onEventClick }: LiveEventFeedProps) {
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const lastEventCountRef = useRef(0)
  const shouldAutoScrollRef = useRef(true)
  
  // Track if user has scrolled up (don't auto-scroll if they have)
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
  
  // Auto-scroll to bottom when new events arrive (only if user is at bottom)
  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container || events.length === 0) return
    
    // Only auto-scroll if new events were added and user is at bottom
    if (events.length > lastEventCountRef.current && shouldAutoScrollRef.current) {
      // Use requestAnimationFrame to ensure DOM has updated
      requestAnimationFrame(() => {
        if (container) {
          container.scrollTop = container.scrollHeight
        }
      })
    }
    
    lastEventCountRef.current = events.length
  }, [events.length])
  // Empty state
  if (events.length === 0) {
    return (
      <div className="bg-gray-800 rounded-lg p-4">
        <h2 className="text-lg font-semibold mb-4">Live Event Feed</h2>
        <div className="text-gray-500 text-center py-8">No events yet</div>
      </div>
    )
  }
  
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
  
  return (
    <div className="bg-gray-800 rounded-lg p-4">
      <h2 className="text-lg font-semibold mb-4">Live Event Feed</h2>
      <div ref={scrollContainerRef} className="overflow-y-auto max-h-96">
        <table className="w-full text-sm">
          <thead className="bg-gray-700 sticky top-0">
            <tr>
              <th className="px-2 py-1 text-left">Date & Time (CT)</th>
              <th className="px-2 py-1 text-left">Level</th>
              <th className="px-2 py-1 text-left">Stream</th>
              <th className="px-2 py-1 text-left">Event Type</th>
              <th className="px-2 py-1 text-left">Message</th>
            </tr>
          </thead>
          <tbody>
            {events.map((event) => {
              const level = getEventLevel(event.event_type)
              const message = extractEventMessage(event)
              const repetitiveCount = (event.data as any)?._repetitive_count
              
              return (
                <tr
                  key={`${event.run_id}:${event.event_seq}`}
                  onClick={() => onEventClick(event)}
                  className="border-b border-gray-700 hover:bg-gray-750 cursor-pointer"
                >
                  <td className="px-2 py-1 font-mono text-xs">
                    {formatChicagoDateTime(event.timestamp_chicago)}
                  </td>
                  <td className="px-2 py-1">
                    <span className={`px-1 py-0.5 rounded text-xs ${getLevelBadgeColor(level)}`}>
                      {level}
                    </span>
                  </td>
                  <td className="px-2 py-1 font-mono text-xs">
                    {event.stream || 'ENGINE'}
                  </td>
                  <td className="px-2 py-1 text-xs">{event.event_type}</td>
                  <td className="px-2 py-1 text-xs truncate max-w-xs" title={message}>
                    {message}
                    {repetitiveCount && repetitiveCount > 1 && (
                      <span className="ml-2 text-amber-400 font-semibold">
                        (×{repetitiveCount})
                      </span>
                    )}
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

// Custom comparison function for memo - only re-render if events actually changed
function areEventsEqual(prevProps: LiveEventFeedProps, nextProps: LiveEventFeedProps) {
  // Compare callback function (should be stable, but check anyway)
  if (prevProps.onEventClick !== nextProps.onEventClick) {
    return false
  }
  
  // If array lengths differ, events changed
  if (prevProps.events.length !== nextProps.events.length) {
    return false
  }
  
  // If both empty, they're equal
  if (prevProps.events.length === 0) {
    return true
  }
  
  // Compare by checking if the last event (highest seq) is the same
  // This is efficient because new events are always appended
  const prevLast = prevProps.events[prevProps.events.length - 1]
  const nextLast = nextProps.events[nextProps.events.length - 1]
  
  if (!prevLast || !nextLast) {
    return false
  }
  
  // Events are equal if the last event has the same run_id and event_seq
  // This means no new events were added
  return (
    prevLast.run_id === nextLast.run_id &&
    prevLast.event_seq === nextLast.event_seq
  )
}

// Export memoized component with custom comparison for stable rendering
export const LiveEventFeed = memo(LiveEventFeedComponent, areEventsEqual)
LiveEventFeed.displayName = 'LiveEventFeed'
