/**
 * Worker Request Manager
 * 
 * Centralized request ID management and response acceptance logic for worker operations.
 * Replaces multiple refs with a single manager that handles:
 * - Request ID generation
 * - Active request tracking per operation type
 * - Lenient acceptance rules (e.g., for breakdown types)
 */

import { useRef } from 'react'

export class WorkerRequestManager {
  constructor() {
    this.requestCounter = 0
    this.activeRequests = new Map() // operation -> requestId
    this.lenientAcceptanceKeys = new Map() // operation -> Set of acceptable keys
  }

  /**
   * Generate a new request ID
   */
  nextRequestId() {
    this.requestCounter += 1
    return this.requestCounter
  }

  /**
   * Register an active request for an operation
   */
  setActiveRequest(operation, requestId, lenientKeys = null) {
    this.activeRequests.set(operation, requestId)
    if (lenientKeys) {
      if (!this.lenientAcceptanceKeys.has(operation)) {
        this.lenientAcceptanceKeys.set(operation, new Set())
      }
      const keySet = this.lenientAcceptanceKeys.get(operation)
      if (Array.isArray(lenientKeys)) {
        lenientKeys.forEach(key => keySet.add(key))
      } else {
        keySet.add(lenientKeys)
      }
    }
  }

  /**
   * Check if a response should be accepted for an operation
   */
  shouldAcceptResponse(operation, requestId, lenientKey = null) {
    const activeRequestId = this.activeRequests.get(operation)
    
    // Exact match always accepted
    if (requestId !== undefined && requestId === activeRequestId) {
      return true
    }

    // Lenient matching (for breakdown types)
    if (lenientKey && this.lenientAcceptanceKeys.has(operation)) {
      const keySet = this.lenientAcceptanceKeys.get(operation)
      if (keySet.has(lenientKey)) {
        return true
      }
    }

    return false
  }

  /**
   * Clear lenient keys for an operation (e.g., when switching tabs)
   */
  clearLenientKeys(operation, baseKey = null) {
    if (!this.lenientAcceptanceKeys.has(operation)) {
      return
    }

    const keySet = this.lenientAcceptanceKeys.get(operation)
    
    if (baseKey) {
      // Clear only keys that don't match the base key
      const keysToRemove = []
      keySet.forEach(key => {
        if (!key.startsWith(baseKey)) {
          keysToRemove.push(key)
        }
      })
      keysToRemove.forEach(key => keySet.delete(key))
    } else {
      // Clear all lenient keys for this operation
      keySet.clear()
    }
  }

  /**
   * Clear all active requests (e.g., on worker reset)
   */
  reset() {
    this.activeRequests.clear()
    this.lenientAcceptanceKeys.clear()
  }
}

/**
 * Hook to create a request manager instance
 */
export function useWorkerRequestManager() {
  const managerRef = useRef(null)
  if (!managerRef.current) {
    managerRef.current = new WorkerRequestManager()
  }
  return managerRef.current
}
