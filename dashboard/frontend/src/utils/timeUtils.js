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


