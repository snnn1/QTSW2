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
    const { data, error: apiError } = await fetchWatchdogStatus()
    if (apiError) {
      setError(apiError)
      setLoading(false)
      return
    }
    if (data) {
      setStatus(data)
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
    status,
    loading,
    error,
    lastSuccessfulPollTimestamp
  }
}
