/**
 * Event filtering utility - single source of truth for event filtering logic
 */

/**
 * Check if an event should be shown (not filtered out)
 * @param {object} event - Event object
 * @param {object} options - Filtering options
 * @param {string} options.stageFilter - Stage filter ('all' or specific stage)
 * @returns {boolean} - True if event should be shown
 */
export function shouldShowEvent(event, options = {}) {
  const { stageFilter = 'all' } = options
  
  // Filter out all log events
  if (event.event === 'log') {
    return false
  }
  
  // Stage filter
  if (stageFilter !== 'all' && event.stage !== stageFilter) {
    return false
  }
  
  // Filter out expected analyzer failures (incomplete data is normal)
  if (event.stage === 'analyzer' && event.event === 'failure') {
    const msg = (event.msg || '').toLowerCase()
    if (msg.includes('data ends') || 
        msg.includes('before expected end time') ||
        (msg.includes('analyzer failed') && msg.includes('failed:'))) {
      return false
    }
  }
  
  // Filter out analyzer file_finish events that indicate failures
  if (event.stage === 'analyzer' && event.event === 'file_finish') {
    const msg = (event.msg || '').toLowerCase()
    const data = event.data || {}
    if (msg.includes('failed') || data.status === 'failed') {
      return false
    }
  }
  
  // Skip blank events unless they're critical
  const hasMessage = event.msg && event.msg.trim().length > 0
  const hasData = event.data && Object.keys(event.data).length > 0
  const isCriticalEvent = event.event === 'failure' || event.event === 'success' || event.event === 'start' || 
    event.event === 'state_change' || event.event === 'timeout_warning' || event.event === 'heartbeat' ||
    event.event === 'file_start' || 
    (event.event === 'file_finish' && event.data && event.data.status === 'success')
  
  if (!hasMessage && !hasData && !isCriticalEvent) {
    return false
  }
  
  return true
}

/**
 * Filter events array based on options
 * @param {Array} events - Array of events
 * @param {object} options - Filtering options
 * @returns {Array} - Filtered events
 */
export function filterEvents(events, options = {}) {
  return events.filter(event => shouldShowEvent(event, options))
}

