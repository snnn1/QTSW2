/**
 * Hook for polling watchdog status
 * Polls every 5 seconds
 */
import { useState, useEffect, useRef } from 'react'
import { fetchWatchdogStatus } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import type { WatchdogStatus } from '../types/watchdog'

export function useWatchdogStatus() {
  const [status, setStatus] = useState<WatchdogStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const hasLoadedRef = useRef(false)
  
  const poll = async () => {
    // Only show loading on initial load, not on subsequent polls
    if (!hasLoadedRef.current) {
      setLoading(true)
    }
    
    try {
      const { data, error: apiError } = await fetchWatchdogStatus()
      
      if (apiError) {
        setError(apiError)
        hasLoadedRef.current = true // Mark as loaded even on error
        setLoading(false)
        return
      }
      if (data) {
        // Only update state if data actually changed (prevent unnecessary re-renders)
        setStatus(prevStatus => {
          // Deep equality check - only update if data actually changed
          if (prevStatus) {
            // Quick reference check first
            if (prevStatus === data) {
              return prevStatus
            }
            // Compare key fields that change frequently
            if (
              prevStatus.timestamp_chicago === data.timestamp_chicago &&
              prevStatus.engine_activity_state === data.engine_activity_state &&
              prevStatus.market_open === data.market_open &&
              prevStatus.engine_alive === data.engine_alive &&
              prevStatus.last_engine_tick_chicago === data.last_engine_tick_chicago &&
              JSON.stringify(prevStatus.data_stall_detected) === JSON.stringify(data.data_stall_detected) &&
              JSON.stringify(prevStatus.stuck_streams) === JSON.stringify(data.stuck_streams)
            ) {
              return prevStatus // Return previous reference if unchanged
            }
          }
          return data
        })
        setError(null)
      }
      // Always mark as loaded and clear loading state, even if data is null
      // This prevents infinite loading state if backend returns null data
      hasLoadedRef.current = true
      setLoading(false)
    } catch (error) {
      setError(error instanceof Error ? error.message : 'Unknown error')
      hasLoadedRef.current = true // Mark as loaded even on error
      setLoading(false)
    }
  }
  
  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 5000)
  
  // Initial load
  useEffect(() => {
    poll()
  }, [])
  
  return {
    status,
    loading,
    error,
    lastSuccessfulPollTimestamp
  }
}
