/**
 * Hook for fetching recent incidents (Phase 6)
 */
import { useState, useEffect } from 'react'
import { fetchIncidents, type IncidentsResponse } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'

export function useIncidents(limit = 50) {
  const [data, setData] = useState<IncidentsResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const poll = async () => {
    try {
      const { data: res, error: apiError } = await fetchIncidents(limit)
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

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 30000)

  useEffect(() => {
    poll()
  }, [limit])

  return {
    incidents: data?.incidents ?? [],
    loading,
    error,
    lastSuccessfulPollTimestamp,
  }
}
