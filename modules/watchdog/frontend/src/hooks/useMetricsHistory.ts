/**
 * Hook for fetching metrics history (Phase 8)
 */
import { useState, useEffect } from 'react'
import { fetchMetricsHistory, type MetricsHistoryResponse } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'

export function useMetricsHistory(granularity: 'week' | 'month' = 'week', limit = 12) {
  const [data, setData] = useState<MetricsHistoryResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const poll = async () => {
    try {
      const { data: res, error: apiError } = await fetchMetricsHistory(granularity, limit)
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

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 300000) // 5 min

  useEffect(() => {
    poll()
  }, [granularity, limit])

  return {
    byPeriod: data?.by_period ?? [],
    storedHistory: data?.stored_history ?? [],
    loading,
    error,
    lastSuccessfulPollTimestamp,
  }
}
