/**
 * Hook for polling operator snapshot (Phase 2)
 * Polls every 5 seconds
 */
import { useState, useEffect, useRef } from 'react'
import { fetchOperatorSnapshot } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import type { OperatorSnapshotResponse } from '../services/watchdogApi'

export function useOperatorSnapshot(nEvents = 500) {
  const [snapshot, setSnapshot] = useState<Record<string, OperatorSnapshotResponse['snapshot'][string]> | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [lastSuccessfulFetchTimestamp, setLastSuccessfulFetchTimestamp] = useState<number | null>(null)
  const hasLoadedRef = useRef(false)

  const poll = async () => {
    if (!hasLoadedRef.current) {
      setLoading(true)
    }

    try {
      const { data, error: apiError } = await fetchOperatorSnapshot(nEvents)

      if (apiError) {
        setError(apiError)
        hasLoadedRef.current = true
        setLoading(false)
        return
      }
      if (data?.snapshot) {
        setSnapshot(data.snapshot)
        setError(null)
        setLastSuccessfulFetchTimestamp(Date.now())
      } else {
        setSnapshot({})
        setLastSuccessfulFetchTimestamp(Date.now())
      }
      hasLoadedRef.current = true
      setLoading(false)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error')
      hasLoadedRef.current = true
      setLoading(false)
    }
  }

  usePollingInterval(poll, 5000)

  useEffect(() => {
    poll()
  }, [])

  return {
    snapshot,
    loading,
    error,
    lastSuccessfulFetchTimestamp,
  }
}
