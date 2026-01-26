/**
 * Hook for polling risk gate status
 * Polls every 5 seconds
 */
import { useState, useEffect, useRef } from 'react'
import { fetchRiskGates } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import type { RiskGateStatus } from '../types/watchdog'

export function useRiskGates() {
  const [gates, setGates] = useState<RiskGateStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const hasLoadedRef = useRef(false)
  
  const poll = async () => {
    // Only show loading on initial load, not on subsequent polls
    if (!hasLoadedRef.current) {
      setLoading(true)
    }
    const { data, error: apiError } = await fetchRiskGates()
    if (apiError) {
      setError(apiError)
      setLoading(false)
      return
    }
    if (data) {
      // Only update state if data actually changed (prevent unnecessary re-renders)
      setGates(prevGates => {
        if (prevGates && JSON.stringify(prevGates) === JSON.stringify(data)) {
          return prevGates // Return previous reference if unchanged
        }
        return data
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
    gates,
    loading,
    error,
    lastSuccessfulPollTimestamp
  }
}
