// API base URL - uses environment variable if set, otherwise defaults to port 8000
const API_PORT = import.meta.env.VITE_API_PORT || '8000'
export const API_BASE = `http://localhost:${API_PORT}/api`

export const STREAMS = ['ES1', 'ES2', 'GC1', 'GC2', 'CL1', 'CL2', 'NQ1', 'NQ2', 'NG1', 'NG2', 'YM1', 'YM2', 'RTY1', 'RTY2']

export const DAYS_OF_WEEK = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday']

export const AVAILABLE_TIMES = ['07:30', '08:00', '09:00', '09:30', '10:00', '10:30', '11:00']

// Analyzer column order (single source of truth)
export const ANALYZER_COLUMN_ORDER = [
  'TradeID', 'Date', 'Time', 'EntryTime', 'ExitTime', 'Instrument', 'Stream', 
  'Session', 'Direction', 'EntryPrice', 'ExitPrice', 'Target', 'Range', 'StopLoss', 
  'Peak', 'Result', 'Profit'
]

// Default columns for all streams (exact structure as specified)
export const DEFAULT_COLUMNS = [
  'Date', 'DOW', 'Time', 'EntryTime', 'ExitTime', 'Instrument', 'Stream', 
  'Session', 'Direction', 'Target', 'Range', 'StopLoss', 'Peak', 'Result', 
  'Profit', 'Time Change', 'Profit ($)'
]

// Contract values (dollars per point) - must match modules/matrix/statistics.py
export const CONTRACT_VALUES = {
  ES: 50,
  MES: 5,
  NQ: 10,
  MNQ: 2,
  YM: 5,
  MYM: 0.5,
  CL: 1000,
  NG: 10000,
  GC: 100,
  RTY: 50
}
export const DEFAULT_CONTRACT_VALUE = 50

// Storage keys
export const STORAGE_KEYS = {
  SELECTED_COLUMNS: 'matrix_selected_columns',
  SHOW_STATS: 'matrix_show_stats',
  STREAM_FILTERS: 'matrix_stream_filters',
  MASTER_CONTRACT_MULTIPLIER: 'matrix_master_contract_multiplier'
}

























