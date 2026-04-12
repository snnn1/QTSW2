/**
 * Hook for slot lifecycle (forced flatten, reentry, slot expiry)
 */
import { useState, useEffect } from 'react'
import { fetchSlotLifecycle, type SlotLifecycleSlot } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'

export function useSlotLifecycle() {
  const [slots, setSlots] = useState<SlotLifecycleSlot[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const poll = async () => {
    try {
      const { data, error: apiError } = await fetchSlotLifecycle()
      if (apiError) {
        setError(apiError)
        setLoading(false)
        return
      }
      if (data) {
        setSlots(data)
        setError(null)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    }
    setLoading(false)
  }

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, 5000)

  useEffect(() => {
    poll()
  }, [])

  return {
    slots,
    loading,
    error,
    lastSuccessfulPollTimestamp,
  }
}
