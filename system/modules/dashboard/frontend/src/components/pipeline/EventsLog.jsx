import { useEffect, useRef, useState, useMemo } from 'react'
import { shouldShowEvent } from '../../utils/eventFilter'

/**
 * Events Log component - receives pre-formatted events
 * Optimized for performance with filtering and cleaner display
 */
export function EventsLog({ eventsFormatted }) {
  const scrollContainerRef = useRef(null)
  const shouldAutoScrollRef = useRef(true)
  const prevEventsLengthRef = useRef(0)
  const [filterStage, setFilterStage] = useState('all')

  // Check if user is at bottom of scroll container
  const isAtBottom = (element) => {
    const threshold = 50 // pixels from bottom
    return element.scrollHeight - element.scrollTop - element.clientHeight < threshold
  }

  // Handle scroll events - track if user manually scrolled up
  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    const handleScroll = () => {
      shouldAutoScrollRef.current = isAtBottom(container)
    }

    container.addEventListener('scroll', handleScroll)
    return () => container.removeEventListener('scroll', handleScroll)
  }, [])

  // Auto-scroll to bottom during snapshot loading, otherwise only if user is at bottom
  useEffect(() => {
    const container = scrollContainerRef.current
    if (!container) return

    const hasNewEvents = eventsFormatted.length > prevEventsLengthRef.current
    const prevLength = prevEventsLengthRef.current
    const newLength = eventsFormatted.length
    
    // Detect if this is snapshot loading (large increase in events)
    const isSnapshotLoad = newLength > prevLength + 5
    
    // Update prev length
    prevEventsLengthRef.current = eventsFormatted.length
    
    // After DOM updates, handle scroll
    setTimeout(() => {
      if (!container) return
      
      if (isSnapshotLoad) {
        // During snapshot loading - always stay at bottom
        container.scrollTop = container.scrollHeight
      } else if (shouldAutoScrollRef.current && hasNewEvents) {
        // Normal updates - only auto-scroll if user was at bottom
        container.scrollTop = container.scrollHeight
      }
      // Otherwise, don't change scroll position (user scrolled up, preserve their position)
    }, 0)
  }, [eventsFormatted])

  // Filter events for cleaner display using utility function
  const filteredEvents = useMemo(() => {
    return eventsFormatted.filter(event => 
      shouldShowEvent(event, {
        stageFilter: filterStage
      })
    )
  }, [eventsFormatted, filterStage])

  // Get unique stages for filter
  const stages = useMemo(() => {
    const uniqueStages = [...new Set(eventsFormatted.map(e => e.stage))].filter(Boolean)
    return uniqueStages.sort()
  }, [eventsFormatted])

  return (
    <div className="bg-gray-900 rounded-lg p-4 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-semibold text-gray-300">Live Events</h2>
        <div className="flex items-center gap-2">
          {/* Stage Filter */}
          <select
            value={filterStage}
            onChange={(e) => setFilterStage(e.target.value)}
            className="bg-gray-800 text-gray-300 text-xs px-2 py-1 rounded border border-gray-600"
          >
            <option value="all">All Stages</option>
            {stages.map(stage => (
              <option key={stage} value={stage}>{stage}</option>
            ))}
          </select>
        </div>
      </div>
      <div 
        ref={scrollContainerRef}
        className="bg-black rounded p-4 h-96 overflow-y-auto font-mono text-sm"
      >
        {filteredEvents.length === 0 ? (
          <div className="text-gray-500">No events yet...</div>
        ) : (
          filteredEvents.map((event, index) => (
            <div
              key={`${event.timestamp || index}-${index}`}
              className={`mb-1 p-1.5 rounded ${event.severityClass}`}
            >
              <div className="flex items-start gap-2">
                {event.formattedTimestamp && (
                  <span className="text-gray-500 text-xs flex-shrink-0">{event.formattedTimestamp}</span>
                )}
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 flex-wrap">
                    {event.stage && (
                      <span className="text-gray-400 capitalize text-xs font-semibold">{event.stage}</span>
                    )}
                    {event.event && (
                      <span className="text-gray-500 capitalize text-xs">{event.event}</span>
                    )}
                    {event.message && (
                      <span className="text-gray-200 text-xs break-words">{event.message}</span>
                    )}
                  </div>
                  {event.dataSummary && (
                    <div className="text-gray-400 text-xs mt-0.5 ml-4 font-mono">{event.dataSummary}</div>
                  )}
                </div>
              </div>
            </div>
          ))
        )}
      </div>
      {filteredEvents.length < eventsFormatted.length && (
        <div className="text-xs text-gray-500 mt-2">
          Showing {filteredEvents.length} of {eventsFormatted.length} events
        </div>
      )}
    </div>
  )
}

