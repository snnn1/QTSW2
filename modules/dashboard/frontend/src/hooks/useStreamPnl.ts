/**
 * Hook for fetching stream P&L
 * On-demand only (no polling by default)
 */
import { useState, useEffect, useRef } from 'react'
import { fetchStreamPnl, type StreamPnl } from '../services/watchdogApi'
import type { StreamState } from '../types/watchdog'

/** Key for PnL lookup: stream only (single date) or "date_stream" (multi-date) */
function pnlKey(tradingDate: string, stream: string, datesCount: number): string {
  return datesCount > 1 ? `${tradingDate}_${stream}` : stream
}

export function useStreamPnl(tradingDate: string, stream?: string) {
  const [pnl, setPnl] = useState<Record<string, StreamPnl>>({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const hasLoadedRef = useRef(false)

  useEffect(() => {
    let cancelled = false

    async function load() {
      if (!hasLoadedRef.current) setLoading(true)
      setError(null)

      const { data, error: apiError } = await fetchStreamPnl(tradingDate, stream)

      if (cancelled) return

      if (apiError) {
        setError(apiError)
        setLoading(false)
        return
      }

      if (data) {
        const pnlMap: Record<string, StreamPnl> = {}
        if (stream) {
          const streamPnl: StreamPnl = {
            stream,
            realized_pnl: data.realized_pnl ?? 0,
            open_positions: data.open_positions ?? 0,
            total_costs_realized: data.total_costs_realized ?? 0,
            intent_count: data.intent_count ?? 0,
            closed_count: data.closed_count ?? 0,
            partial_count: data.partial_count ?? 0,
            open_count: data.open_count ?? 0,
            pnl_confidence: data.pnl_confidence ?? 'LOW'
          }
          pnlMap[stream] = streamPnl
        } else if (data.streams) {
          data.streams.forEach((s: StreamPnl) => {
            pnlMap[s.stream] = s
          })
        }
        setPnl(pnlMap)
        setError(null)
        hasLoadedRef.current = true
      }
      setLoading(false)
    }

    if (tradingDate) load()
    else setLoading(false)

    return () => { cancelled = true }
  }, [tradingDate, stream])

  return { pnl, loading, error }
}

/**
 * Fetch PnL for all streams, supporting multiple trading dates (e.g. carry-over).
 * Returns pnl keyed by stream or "date_stream" when streams span multiple dates.
 */
export function useStreamPnlForStreams(streams: StreamState[]) {
  const [pnl, setPnl] = useState<Record<string, StreamPnl>>({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const uniqueDates = [...new Set(streams.map(s => s.trading_date).filter(Boolean))] as string[]
  const multiDate = uniqueDates.length > 1

  useEffect(() => {
    if (uniqueDates.length === 0) {
      setPnl({})
      setLoading(false)
      return
    }

    let cancelled = false
    setLoading(true)
    setError(null)

    Promise.all(uniqueDates.map(d => fetchStreamPnl(d)))
      .then(results => {
        if (cancelled) return
        const merged: Record<string, StreamPnl> = {}
        results.forEach((res, i) => {
          const date = uniqueDates[i]
          if (res.data?.streams) {
            res.data.streams.forEach((s: StreamPnl) => {
              merged[pnlKey(date, s.stream, uniqueDates.length)] = s
            })
          }
        })
        setPnl(merged)
        setError(null)
      })
      .catch(err => {
        if (!cancelled) setError(err instanceof Error ? err.message : 'Unknown error')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => { cancelled = true }
  }, [uniqueDates.join(',')])

  return {
    pnl,
    loading,
    error,
    getPnl: (stream: StreamState) => pnl[pnlKey(stream.trading_date ?? '', stream.stream, uniqueDates.length)] ?? pnl[stream.stream]
  }
}
