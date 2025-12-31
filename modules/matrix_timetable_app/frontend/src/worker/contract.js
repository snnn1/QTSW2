/**
 * Worker Message Contract
 * 
 * Centralized definition of all message types and payloads used for communication
 * between the React hook (useMatrixWorker) and the Web Worker (matrixWorker.js).
 * 
 * This ensures type safety and makes it easy to see all worker operations in one place.
 */

// Message types sent TO worker
export const WORKER_MESSAGE_TYPES = {
  INIT_DATA: 'INIT_DATA',
  FILTER: 'FILTER',
  GET_ROWS: 'GET_ROWS',
  CALCULATE_STATS: 'CALCULATE_STATS',
  CALCULATE_PROFIT_BREAKDOWN: 'CALCULATE_PROFIT_BREAKDOWN',
  CALCULATE_TIMETABLE: 'CALCULATE_TIMETABLE'
}

// Message types received FROM worker
export const WORKER_RESPONSE_TYPES = {
  DATA_INITIALIZED: 'DATA_INITIALIZED',
  FILTERED: 'FILTERED',
  ROWS: 'ROWS',
  STATS: 'STATS',
  PROFIT_BREAKDOWN: 'PROFIT_BREAKDOWN',
  TIMETABLE: 'TIMETABLE',
  ERROR: 'ERROR'
}

// Contract values for profit calculations
// NOTE: These must match the canonical values in modules/matrix/statistics.py (_ensure_profit_dollars_column)
// Canonical source: modules/matrix/statistics.py line ~144
export const CONTRACT_VALUES = {
  'ES': 50,
  'MES': 5,
  'NQ': 10,
  'MNQ': 2,
  'YM': 5,
  'MYM': 0.5,
  'CL': 1000,
  'NG': 10000,
  'GC': 100,
  'RTY': 50
}

/**
 * Create a worker message with type and payload
 */
export function createWorkerMessage(type, payload) {
  return { type, payload }
}

/**
 * Validate worker message structure
 */
export function isValidWorkerMessage(message) {
  return message && 
         typeof message === 'object' && 
         typeof message.type === 'string' && 
         'payload' in message
}

/**
 * Validate worker response structure
 */
export function isValidWorkerResponse(response) {
  return response && 
         typeof response === 'object' && 
         typeof response.type === 'string' && 
         'payload' in response
}
