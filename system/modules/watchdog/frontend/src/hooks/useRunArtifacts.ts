/**
 * Poll engine run artifacts: summary.json, KEY_EVENTS.jsonl tail, recent runs (display-only).
 */
import { useCallback, useState } from 'react'
import { fetchRunSummary, fetchKeyEvents, fetchRecentRuns } from '../services/watchdogApi'
import type { KeyEventsResponse, RecentRunsResponse, RunSummaryResult } from '../types/watchdog'
import { usePollingInterval } from './usePollingInterval'

const POLL_MS = 5000
const KEY_EVENTS_LIMIT = 50

export function useRunArtifacts(peekRunRoot: string | null) {
  const [summary, setSummary] = useState<RunSummaryResult | null>(null)
  const [summaryError, setSummaryError] = useState<string | null>(null)
  const [keyEvents, setKeyEvents] = useState<KeyEventsResponse | null>(null)
  const [keyEventsError, setKeyEventsError] = useState<string | null>(null)
  const [recentRuns, setRecentRuns] = useState<RecentRunsResponse | null>(null)
  const [recentError, setRecentError] = useState<string | null>(null)
  const [activeSummary, setActiveSummary] = useState<RunSummaryResult | null>(null)
  const [activePersistenceRoot, setActivePersistenceRoot] = useState<string | null>(null)

  const poll = useCallback(async () => {
    const root = peekRunRoot
    const [s, k, r, activeSummaryResponse, activeKeyEventsResponse] = await Promise.all([
      fetchRunSummary(root),
      fetchKeyEvents(KEY_EVENTS_LIMIT, root),
      fetchRecentRuns(5),
      fetchRunSummary(),
      fetchKeyEvents(1),
    ])

    if (s.data) {
      setSummary(s.data)
      setSummaryError(null)
    } else {
      setSummaryError(s.error ?? 'run-summary failed')
    }

    if (k.data) {
      setKeyEvents(k.data)
      setKeyEventsError(null)
    } else {
      setKeyEventsError(k.error ?? 'key-events failed')
    }

    if (r.data) {
      setRecentRuns(r.data)
      setRecentError(null)
    } else {
      setRecentError(r.error ?? 'recent-runs failed')
    }

    if (activeSummaryResponse.data) {
      setActiveSummary(activeSummaryResponse.data)
    } else {
      setActiveSummary(null)
    }

    if (activeKeyEventsResponse.data?.persistence_root) {
      setActivePersistenceRoot(activeKeyEventsResponse.data.persistence_root)
    } else {
      setActivePersistenceRoot(null)
    }
  }, [peekRunRoot])

  const { lastSuccessfulPollTimestamp } = usePollingInterval(poll, POLL_MS)

  return {
    summary,
    summaryError,
    keyEvents,
    keyEventsError,
    recentRuns,
    recentError,
    activeSummary,
    activePersistenceRoot,
    lastPollTime: lastSuccessfulPollTimestamp,
    refresh: poll,
  }
}
