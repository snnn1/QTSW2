/**
 * Hook for polling stream states
 * Polls every 5 seconds
 */
import { useState, useEffect, useRef } from 'react'
import { fetchStreamStates } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import type { StreamState } from '../types/watchdog'

export function useStreamStates() {
  const [streams, setStreams] = useState<StreamState[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const hasLoadedRef = useRef(false)
  
  const poll = async () => {
    // Only show loading on initial load, not on subsequent polls
    if (!hasLoadedRef.current) {
      setLoading(true)
    }
    const { data, error: apiError } = await fetchStreamStates()
    if (apiError) {
      setError(apiError)
      setLoading(false)
      return
    }
    if (data) {
      // Only update state if data actually changed (prevent unnecessary re-renders)
      setStreams(prevStreams => {
        const newStreams = data.streams || []
        if (prevStreams.length === newStreams.length && 
            JSON.stringify(prevStreams) === JSON.stringify(newStreams)) {
          return prevStreams // Return previous reference if unchanged
        }
        return newStreams
      })
      setError(null)
      hasLoadedRef.current = true
    }
    setLoading(false)
  }
  
  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 5000)
  
  // Initial load
  useEffect(() => {
    poll()
  }, [])
  
  return {
    streams,
    loading,
    error,
    lastSuccessfulPollTimestamp
  }
}
