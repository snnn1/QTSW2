/**
 * Hook for fetching reliability metrics (Phase 6)
 */
import { useState, useEffect } from 'react'
import { fetchReliabilityMetrics, type ReliabilityMetrics } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'

export function useReliabilityMetrics(windowHours = 24) {
  const [data, setData] = useState<ReliabilityMetrics | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const poll = async () => {
    try {
      const { data: res, error: apiError } = await fetchReliabilityMetrics(windowHours)
      if (apiError) {
        setError(apiError)
        setLoading(false)
        return
      }
      if (res) {
        setData(res)
        setError(null)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    }
    setLoading(false)
  }

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 60000)

  useEffect(() => {
    poll()
  }, [windowHours])

  return {
    metrics: data,
    loading,
    error,
    lastSuccessfulPollTimestamp,
  }
}
