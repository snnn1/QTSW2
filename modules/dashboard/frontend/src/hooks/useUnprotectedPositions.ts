/**
 * Hook for polling unprotected positions
 * Polls every 2 seconds
 */
import { useState, useEffect, useRef } from 'react'
import { fetchUnprotectedPositions } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import type { UnprotectedPosition } from '../types/watchdog'

export function useUnprotectedPositions() {
  const [positions, setPositions] = useState<UnprotectedPosition[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const hasScrolledRef = useRef(false)
  const hasLoadedRef = useRef(false)
  
  const poll = async () => {
    // Only show loading on initial load, not on subsequent polls
    if (!hasLoadedRef.current) {
      setLoading(true)
    }
    const { data, error: apiError } = await fetchUnprotectedPositions()
    if (apiError) {
      setError(apiError)
      setLoading(false)
      return
    }
    if (data) {
      setPositions(data.unprotected_positions || [])
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
  
  // Auto-scroll to panel if unprotected positions exist
  useEffect(() => {
    if (positions.length > 0 && !hasScrolledRef.current) {
      const panel = document.getElementById('active-intent-panel')
      if (panel) {
        panel.scrollIntoView({ behavior: 'smooth', block: 'start' })
        hasScrolledRef.current = true
      }
    } else if (positions.length === 0) {
      hasScrolledRef.current = false
    }
  }, [positions])
  
  return {
    positions,
    loading,
    error,
    lastSuccessfulPollTimestamp
  }
}
