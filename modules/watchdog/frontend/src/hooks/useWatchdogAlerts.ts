/**
 * Hook for fetching watchdog alerts (Phase 1 ledger)
 */
import { useState, useEffect } from 'react'
import { fetchWatchdogAlerts, type AlertsResponse } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'

export function useWatchdogAlerts(sinceHours = 24, limit = 30) {
  const [data, setData] = useState<AlertsResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const poll = async () => {
    try {
      const { data: res, error: apiError } = await fetchWatchdogAlerts(false, sinceHours, limit)
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

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 30000) // every 30s

  useEffect(() => {
    poll()
  }, [sinceHours, limit])

  return {
    activeAlerts: data?.active_alerts ?? [],
    recentAlerts: data?.recent ?? [],
    loading,
    error,
    lastSuccessfulPollTimestamp,
  }
}
