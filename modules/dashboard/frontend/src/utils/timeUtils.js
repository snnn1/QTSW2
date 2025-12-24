/**
 * Time utility functions for pipeline dashboard
 */

/**
 * Parse and format Chicago time
 * @returns {string} Formatted Chicago time string
 */
export const parseChicagoTime = () => {
  const now = new Date()
  return now.toLocaleString('en-US', {
    timeZone: 'America/Chicago',
    hour12: false,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  })
}

/**
 * Compute elapsed time in seconds from a start timestamp
 * @param {number} startTime - Start timestamp in milliseconds
 * @returns {number} Elapsed time in seconds
 */
export const computeElapsed = (startTime) => {
  if (!startTime) return 0
  const elapsed = Math.floor((Date.now() - startTime) / 1000)
  // Cap elapsed time at 24 hours (86400 seconds) to prevent display issues
  // If it's larger, it's likely a calculation error
  return elapsed >= 0 && elapsed < 86400 ? elapsed : 0
}

/**
 * Sync elapsed time from backend-reported elapsed time
 * Adjusts the start time to match backend's elapsed time calculation
 * @param {number} elapsedMinutes - Elapsed time in minutes from backend
 * @returns {number} Adjusted start timestamp in milliseconds
 */
export const syncElapsedFromBackend = (elapsedMinutes) => {
  const elapsedSeconds = elapsedMinutes * 60
  // Calculate what the start time should be to match backend's elapsed time
  return Date.now() - (elapsedSeconds * 1000)
}

/**
 * Format elapsed time as human-readable string
 * @param {number} seconds - Elapsed time in seconds
 * @returns {string} Formatted time string (e.g., "1h 23m 45s")
 */
export const formatElapsedTime = (seconds) => {
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const secs = seconds % 60
  if (hours > 0) {
    return `${hours}h ${minutes}m ${secs}s`
  } else if (minutes > 0) {
    return `${minutes}m ${secs}s`
  } else {
    return `${secs}s`
  }
}

/**
 * Format event timestamp for display
 * @param {string} timestamp - ISO timestamp string from backend
 * @returns {string} Formatted time string (e.g., "22:30:15.123")
 */
export const formatEventTimestamp = (timestamp) => {
  if (!timestamp) return ''
  
  try {
    const date = new Date(timestamp)
    if (isNaN(date.getTime())) return timestamp // Return original if invalid
    
    // Format as HH:mm:ss.mmm in Chicago timezone
    const formatter = new Intl.DateTimeFormat('en-US', {
      timeZone: 'America/Chicago',
      hour12: false,
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit'
    })
    
    const parts = formatter.formatToParts(date)
    const hour = parts.find(p => p.type === 'hour')?.value || '00'
    const minute = parts.find(p => p.type === 'minute')?.value || '00'
    const second = parts.find(p => p.type === 'second')?.value || '00'
    
    // Get milliseconds from original date
    const milliseconds = String(date.getMilliseconds()).padStart(3, '0')
    
    return `${hour}:${minute}:${second}.${milliseconds}`
  } catch (e) {
    // Fallback: try to extract time from ISO string
    try {
      // ISO format: 2025-12-08T22:30:15.123456-06:00
      const match = timestamp.match(/(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?/)
      if (match) {
        const [, h, m, s, ms = '000'] = match
        const milliseconds = ms.substring(0, 3).padEnd(3, '0')
        return `${h}:${m}:${s}.${milliseconds}`
      }
      return timestamp
    } catch (e2) {
      return timestamp // Return original if all parsing fails
    }
  }
}


