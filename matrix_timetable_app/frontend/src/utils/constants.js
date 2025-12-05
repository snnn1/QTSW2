export const API_BASE = 'http://localhost:8000/api'

export const STREAMS = ['ES1', 'ES2', 'GC1', 'GC2', 'CL1', 'CL2', 'NQ1', 'NQ2', 'NG1', 'NG2', 'YM1', 'YM2']

export const DAYS_OF_WEEK = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']

export const AVAILABLE_TIMES = ['07:30', '08:00', '09:00', '09:30', '10:00', '10:30', '11:00']

// Analyzer column order (single source of truth)
export const ANALYZER_COLUMN_ORDER = [
  'TradeID', 'Date', 'Time', 'EntryTime', 'ExitTime', 'Instrument', 'Stream', 
  'Session', 'Direction', 'EntryPrice', 'ExitPrice', 'Target', 'Range', 'SL', 
  'Peak', 'Result', 'Profit'
]

// Default columns for all streams (exact structure as specified)
export const DEFAULT_COLUMNS = [
  'Date', 'DOW', 'Time', 'EntryTime', 'ExitTime', 'Instrument', 'Stream', 
  'Session', 'Direction', 'Target', 'Range', 'SL', 'Peak', 'Result', 
  'Profit', 'Time Change', 'Profit ($)'
]

// Storage keys
export const STORAGE_KEYS = {
  SELECTED_COLUMNS: 'matrix_selected_columns',
  SHOW_STATS: 'matrix_show_stats',
  STREAM_FILTERS: 'matrix_stream_filters',
  MASTER_CONTRACT_MULTIPLIER: 'matrix_master_contract_multiplier'
}




