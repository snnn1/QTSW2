/**
 * Single tick: /status + /stream-states + /slot-lifecycle so the live page sees one consistent refresh.
 * Backend also returns snapshot_utc on status and stream-states (separate HTTP calls ≈ ms apart).
 */
import { useCallback, useRef, useState } from 'react'
import { fetchWatchdogStatus, fetchStreamStates, fetchSlotLifecycle, type SlotLifecycleSlot } from '../services/watchdogApi'
import { usePollingInterval } from './usePollingInterval'
import type {
  ExecutionExpectationGap,
  OutOfTimetableActiveStream,
  StreamState,
  WatchdogStatus,
} from '../types/watchdog'

const POLL_MS = 5000

export function useWatchdogLiveSnapshot() {
  const [status, setStatus] = useState<WatchdogStatus | null>(null)
  const [streams, setStreams] = useState<StreamState[]>([])
  const [timetableUnavailable, setTimetableUnavailable] = useState(false)
  const [outOfTimetableActiveStreams, setOutOfTimetableActiveStreams] = useState<OutOfTimetableActiveStream[]>([])
  const [executionExpectationGaps, setExecutionExpectationGaps] = useState<ExecutionExpectationGap[]>([])
  const [slotLifecycle, setSlotLifecycle] = useState<SlotLifecycleSlot[]>([])
  const [statusError, setStatusError] = useState<string | null>(null)
  const [streamsError, setStreamsError] = useState<string | null>(null)
  const [slotLifecycleError, setSlotLifecycleError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const hasLoadedRef = useRef(false)

  const poll = useCallback(async () => {
    if (!hasLoadedRef.current) {
      setLoading(true)
    }

    const [st, ss, sl] = await Promise.all([
      fetchWatchdogStatus(),
      fetchStreamStates(),
      fetchSlotLifecycle(),
    ])

    if (st.data) {
      setStatus(st.data)
      setStatusError(null)
    } else {
      setStatusError(st.error ?? 'Status request failed')
    }

    if (ss.data) {
      setStreams(ss.data.streams ?? [])
      setOutOfTimetableActiveStreams(ss.data.out_of_timetable_active_streams ?? [])
      setExecutionExpectationGaps(ss.data.execution_expectation_gaps ?? [])
      setTimetableUnavailable(Boolean(ss.data.timetable_unavailable || ss.data.enabled_streams_unknown))
      setStreamsError(null)
    } else {
      setStreamsError(ss.error ?? 'Stream states request failed')
      setOutOfTimetableActiveStreams([])
      setExecutionExpectationGaps([])
    }

    if (sl.data) {
      setSlotLifecycle(sl.data)
      setSlotLifecycleError(null)
    } else {
      setSlotLifecycleError(sl.error ?? 'Slot lifecycle request failed')
    }

    hasLoadedRef.current = true
    setLoading(false)
  }, [])

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, POLL_MS)

  return {
    status,
    streams,
    outOfTimetableActiveStreams,
    executionExpectationGaps,
    timetableUnavailable,
    slotLifecycle,
    statusError,
    streamsError,
    slotLifecycleError,
    loading,
    lastSuccessfulPollTimestamp,
  }
}
