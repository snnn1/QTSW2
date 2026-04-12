/**
 * Time utility functions for Watchdog UI
 * All timestamps displayed in America/Chicago timezone
 */

/**
 * Format date as Chicago time (HH:mm:ss CT)
 */
export function formatChicagoTime(date: Date | string): string {
  const d = typeof date === 'string' ? new Date(date) : date
  return d.toLocaleTimeString('en-US', {
    timeZone: 'America/Chicago',
    hour12: false,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }) + ' CT'
}

/**
 * Format date as Chicago time with milliseconds (HH:mm:ss.mmm CT)
 */
export function formatChicagoTimeWithMs(date: Date | string): string {
  const d = typeof date === 'string' ? new Date(date) : date
  const timeStr = d.toLocaleTimeString('en-US', {
    timeZone: 'America/Chicago',
    hour12: false,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  })
  const ms = String(d.getMilliseconds()).padStart(3, '0')
  return `${timeStr}.${ms} CT`
}

/**
 * Format date as full Chicago datetime (YYYY-MM-DD HH:mm:ss.mmm CT)
 */
export function formatChicagoDateTime(date: Date | string): string {
  const d = typeof date === 'string' ? new Date(date) : date
  
  // Use Intl.DateTimeFormat for consistent formatting
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Chicago',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false
  })
  
  const parts = formatter.formatToParts(d)
  const year = parts.find(p => p.type === 'year')?.value || '0000'
  const month = parts.find(p => p.type === 'month')?.value || '01'
  const day = parts.find(p => p.type === 'day')?.value || '01'
  const hour = parts.find(p => p.type === 'hour')?.value || '00'
  const minute = parts.find(p => p.type === 'minute')?.value || '00'
  const second = parts.find(p => p.type === 'second')?.value || '00'
  
  // Add milliseconds
  const ms = String(d.getMilliseconds()).padStart(3, '0')
  
  return `${year}-${month}-${day} ${hour}:${minute}:${second}.${ms} CT`
}

/**
 * Parse Chicago timestamp string to Date
 */
export function parseChicagoTimestamp(isoString: string): Date {
  return new Date(isoString)
}

/**
 * Parse and format current Chicago time
 * Returns formatted Chicago time string (YYYY-MM-DD HH:mm:ss)
 */
export function parseChicagoTime(): string {
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
 * Compute time in state (seconds elapsed)
 */
export function computeTimeInState(entryTimeUtc: string): number {
  const entry = new Date(entryTimeUtc)
  const now = new Date()
  return Math.floor((now.getTime() - entry.getTime()) / 1000)
}

/**
 * Format duration as human-readable string (e.g., "5m 23s")
 */
export function formatDuration(seconds: number): string {
  if (seconds < 60) {
    return `${seconds}s`
  }
  const minutes = Math.floor(seconds / 60)
  const secs = seconds % 60
  if (minutes < 60) {
    return secs > 0 ? `${minutes}m ${secs}s` : `${minutes}m`
  }
  const hours = Math.floor(minutes / 60)
  const mins = minutes % 60
  return mins > 0 ? `${hours}h ${mins}m` : `${hours}h`
}

/**
 * Get current Chicago time string (HH:mm:ss CT)
 */
export function getCurrentChicagoTime(): string {
  return formatChicagoTime(new Date())
}

/**
 * Get current Chicago time with milliseconds (HH:mm:ss.mmm CT)
 */
export function getCurrentChicagoTimeWithMs(): string {
  return formatChicagoTimeWithMs(new Date())
}

/**
 * Format event timestamp for display (HH:mm:ss.mmm)
 * Used by WebSocketContext and other components
 */
export function formatEventTimestamp(timestamp: string): string {
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
