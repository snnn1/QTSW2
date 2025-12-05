/**
 * Event filtering utility - single source of truth for event filtering logic
 * Used by reducer, components, and anywhere events need to be filtered
 */

/**
 * Check if a log event is verbose and should be hidden by default
 * @param {object} event - Event object
 * @returns {boolean} - True if event is verbose
 */
export function isVerboseLogEvent(event) {
  if (event.event !== 'log') {
    return false
  }

  const msg = (event.msg || event.message || '').toLowerCase()
  
  // Skip verbose analyzer date completion messages
  if (msg.includes('completed date') && msg.includes('processed slots')) {
    return true
  }
  
  // Skip verbose translator/analyzer output lines
  const verbosePatterns = [
    'processing:',
    'loaded:',
    'saving',
    'saved:',
    'format:',
    'set instrument',
    'rows',
    'file written',
    'traceback',
    'error output',
    'completed date',
  ]
  
  const isVerbosePattern = verbosePatterns.some(pattern => msg.includes(pattern))
  
  // Only show log events that are important milestones or errors
  const isImportantLog = 
    msg.includes('starting') ||
    (msg.includes('complete') && !msg.includes('completed date')) ||
    msg.includes('failed') ||
    msg.includes('error') ||
    msg.includes('warning') ||
    msg.includes('exception') ||
    msg.includes('timeout') ||
    msg.includes('stalled') ||
    msg.includes('running analyzer') ||
    msg.includes('analyzer for')
  
  // If it's a verbose pattern and not important, it's verbose
  return isVerbosePattern && !isImportantLog
}

/**
 * Check if an event should be shown (not filtered out)
 * @param {object} event - Event object
 * @param {object} options - Filtering options
 * @param {boolean} options.showVerbose - Show verbose events
 * @param {string} options.stageFilter - Stage filter ('all' or specific stage)
 * @returns {boolean} - True if event should be shown
 */
export function shouldShowEvent(event, options = {}) {
  const { showVerbose = false, stageFilter = 'all' } = options
  
  // Stage filter
  if (stageFilter !== 'all' && event.stage !== stageFilter) {
    return false
  }
  
  // Verbose filter
  if (!showVerbose && isVerboseLogEvent(event)) {
    return false
  }
  
  // Skip blank events unless they're critical
  const hasMessage = event.msg && event.msg.trim().length > 0
  const hasData = event.data && Object.keys(event.data).length > 0
  const isCriticalEvent = event.event === 'failure' || event.event === 'success' || event.event === 'start'
  
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

