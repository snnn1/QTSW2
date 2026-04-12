/**
 * Hook for fetching active (ongoing) incidents
 */
import { useState, useEffect } from 'react'
import { fetchActiveIncidents, type ActiveIncidentsResponse } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'

export function useActiveIncidents() {
  const [data, setData] = useState<ActiveIncidentsResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const poll = async () => {
    try {
      const { data: res, error: apiError } = await fetchActiveIncidents()
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

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 5000) // Poll every 5s when active

  useEffect(() => {
    poll()
  }, [])

  return {
    active: data?.active ?? [],
    loading,
    error,
    lastSuccessfulPollTimestamp,
  }
}
