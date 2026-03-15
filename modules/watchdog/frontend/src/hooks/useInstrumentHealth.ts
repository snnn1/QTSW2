/**
 * Hook for fetching instrument health (Phase 6)
 */
import { useState, useEffect } from 'react'
import { fetchInstrumentHealth, type InstrumentHealthResponse } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'

export function useInstrumentHealth() {
  const [data, setData] = useState<InstrumentHealthResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const poll = async () => {
    try {
      const { data: res, error: apiError } = await fetchInstrumentHealth()
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

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 10000)

  useEffect(() => {
    poll()
  }, [])

  return {
    instruments: data?.instruments ?? [],
    loading,
    error,
    lastSuccessfulPollTimestamp,
  }
}
