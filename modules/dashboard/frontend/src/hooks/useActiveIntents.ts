/**
 * Hook for polling active intents
 * Polls every 2 seconds
 */
import { useState, useEffect, useRef } from 'react'
import { fetchActiveIntents } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import type { IntentExposure } from '../types/watchdog'

export function useActiveIntents() {
  const [intents, setIntents] = useState<IntentExposure[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const hasLoadedRef = useRef(false)
  
  const poll = async () => {
    // Only show loading on initial load, not on subsequent polls
    if (!hasLoadedRef.current) {
      setLoading(true)
    }
    const { data, error: apiError } = await fetchActiveIntents()
    if (apiError) {
      setError(apiError)
      setLoading(false)
      return
    }
    if (data) {
      // Only update state if data actually changed (prevent unnecessary re-renders)
      setIntents(prevIntents => {
        const newIntents = data.intents || []
        if (prevIntents.length === newIntents.length && 
            JSON.stringify(prevIntents) === JSON.stringify(newIntents)) {
          return prevIntents // Return previous reference if unchanged
        }
        return newIntents
      })
      setError(null)
      hasLoadedRef.current = true
    }
    setLoading(false)
  }
  
  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 2000)
  
  // Initial load
  useEffect(() => {
    poll()
  }, [])
  
  return {
    intents,
    loading,
    error,
    lastSuccessfulPollTimestamp
  }
}
