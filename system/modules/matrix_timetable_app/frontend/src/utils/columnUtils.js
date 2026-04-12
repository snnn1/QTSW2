import { DEFAULT_COLUMNS } from './constants'

// Sort columns to maintain DEFAULT_COLUMNS order, then time slots sorted by time, then extras
// TradeID always comes first if selected
export function sortColumnsByDefaultOrder(columns) {
  const sorted = []
  const timeSlotColumns = []
  const extras = []
  
  // TradeID always comes first if it's selected
  if (columns.includes('TradeID')) {
    sorted.push('TradeID')
  }
  
  // Then add DEFAULT_COLUMNS in exact order (excluding TradeID if it was already added)
  DEFAULT_COLUMNS.forEach(defaultCol => {
    if (columns.includes(defaultCol)) {
      sorted.push(defaultCol)
    }
  })
  
  // Extract time slot columns (format: "HH:MM Rolling" or "HH:MM Points")
  const timeSlotRegex = /^(\d{2}:\d{2})\s+(Rolling|Points)$/
  columns.forEach(col => {
    const match = col.match(timeSlotRegex)
    if (match) {
      timeSlotColumns.push({ name: col, time: match[1], type: match[2] })
    } else if (col !== 'TradeID' && !DEFAULT_COLUMNS.includes(col) && !sorted.includes(col)) {
      extras.push(col)
    }
  })
  
  // Sort time slot columns: by time first, then by type (Points before Rolling)
  timeSlotColumns.sort((a, b) => {
    if (a.time !== b.time) {
      return a.time.localeCompare(b.time) // Sort by time
    }
    // Same time: Points comes before Rolling
    if (a.type === 'Points' && b.type === 'Rolling') return -1
    if (a.type === 'Rolling' && b.type === 'Points') return 1
    return 0
  })
  
  return [...sorted, ...timeSlotColumns.map(t => t.name), ...extras]
}

// Get selected columns for a tab (with defaults if none selected)
export function getSelectedColumnsForTab(selectedColumns, tabId) {
  const cols = selectedColumns[tabId] || DEFAULT_COLUMNS
  // Ensure columns are always in the correct order
  return sortColumnsByDefaultOrder(cols)
}

// Get relevant time slots for a stream (S1 or S2)
export function getRelevantTimeSlots(streamId) {
  if (!streamId || streamId === 'master') return null
  // Stream 1 (ES1, GC1, CL1, NQ1, NG1, YM1) -> S1 times: 07:30, 08:00, 09:00
  // Stream 2 (ES2, GC2, CL2, NQ2, NG2, YM2) -> S2 times: 09:30, 10:00, 10:30, 11:00
  const isStream1 = streamId.endsWith('1')
  if (isStream1) {
    return ['07:30', '08:00', '09:00']
  } else {
    return ['09:30', '10:00', '10:30', '11:00']
  }
}

// Filter columns based on stream (only show relevant time slot columns)
export function getFilteredColumns(columns, streamId) {
  if (!streamId || streamId === 'master') {
    return columns // Master shows all columns
  }
  
  const relevantTimes = getRelevantTimeSlots(streamId)
  if (!relevantTimes) return columns
  
  return columns.filter(col => {
    // Always include non-time-slot columns
    if (!col.includes(' Points') && !col.includes(' Rolling')) {
      return true
    }
    // For time slot columns, only include if time matches stream's session
    return relevantTimes.some(time => col.startsWith(time))
  })
}

