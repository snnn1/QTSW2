/**
 * Hook for fetching stream P&L
 * Polls every 60s when market is open to refresh PnL during session
 */
import { useState, useEffect, useRef } from 'react'
import { fetchStreamPnl, type StreamPnl } from '../services/watchdogApi'

const PNL_POLL_INTERVAL_MS = 60000 // 60 seconds when market open

export function useStreamPnl(tradingDate: string, stream?: string, marketOpen?: boolean | null) {
  const [pnl, setPnl] = useState<Record<string, StreamPnl>>({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const hasLoadedRef = useRef(false)
  
  useEffect(() => {
    let cancelled = false
    
    async function load() {
      // Only show loading on initial load
      if (!hasLoadedRef.current) {
        setLoading(true)
      }
      setError(null)
      
      const { data, error: apiError } = await fetchStreamPnl(tradingDate, stream)
      
      if (cancelled) return
      
      if (apiError) {
        setError(apiError)
        setLoading(false)
        return
      }
      
      if (data) {
        if (stream) {
          // Single stream response
          const streamPnl: StreamPnl = {
            stream: stream,
            realized_pnl: data.realized_pnl || 0,
            open_positions: data.open_positions || 0,
            total_costs_realized: data.total_costs_realized || 0,
            intent_count: data.intent_count || 0,
            closed_count: data.closed_count || 0,
            partial_count: data.partial_count || 0,
            open_count: data.open_count || 0,
            pnl_confidence: data.pnl_confidence || 'LOW',
            exit_type: data.exit_type ?? null,
            entry_price: data.entry_price ?? null,
            exit_price: data.exit_price ?? null,
          }
          setPnl({ [stream]: streamPnl })
        } else {
          // Multiple streams response
          const pnlMap: Record<string, StreamPnl> = {}
          if (data.streams) {
            data.streams.forEach((s: StreamPnl) => {
              pnlMap[s.stream] = s
            })
          }
          setPnl(pnlMap)
        }
        setError(null)
        hasLoadedRef.current = true
      }
      
      setLoading(false)
    }
    
    if (tradingDate) {
      load()
    } else {
      setLoading(false)
    }
    
    return () => {
      cancelled = true
    }
  }, [tradingDate, stream])

  // Poll for PnL refresh when market is open
  useEffect(() => {
    if (!tradingDate || marketOpen !== true) return
    const interval = setInterval(() => {
      fetchStreamPnl(tradingDate, stream).then(({ data, error: apiError }) => {
        if (apiError || !data) return
        if (stream) {
          const streamPnl: StreamPnl = {
            stream: stream,
            realized_pnl: data.realized_pnl || 0,
            open_positions: data.open_positions || 0,
            total_costs_realized: data.total_costs_realized || 0,
            intent_count: data.intent_count || 0,
            closed_count: data.closed_count || 0,
            partial_count: data.partial_count || 0,
            open_count: data.open_count || 0,
            pnl_confidence: data.pnl_confidence || 'LOW',
            exit_type: data.exit_type ?? null,
            entry_price: data.entry_price ?? null,
            exit_price: data.exit_price ?? null,
          }
          setPnl((prev) => ({ ...prev, [stream]: streamPnl }))
        } else if (data.streams) {
          const pnlMap: Record<string, StreamPnl> = {}
          data.streams.forEach((s: StreamPnl) => {
            pnlMap[s.stream] = s
          })
          setPnl(pnlMap)
        }
      })
    }, PNL_POLL_INTERVAL_MS)
    return () => clearInterval(interval)
  }, [tradingDate, stream, marketOpen])
  
  return { pnl, loading, error }
}
