/**
 * Worker Message Contract
 * 
 * Centralized definition of all message types and payloads used for communication
 * between the React hook (useMatrixWorker) and the Web Worker (matrixWorker.js).
 * 
 * This ensures type safety and makes it easy to see all worker operations in one place.
 */

import { CONTRACT_VALUES } from '../utils/constants'

// Re-export for worker/hook consumers
export { CONTRACT_VALUES }

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
