/**
 * Hooks for on-demand journal data queries (no polling)
 */
import { useState, useEffect } from 'react'
import {
  fetchExecutionJournal,
  fetchStreamJournal,
  fetchExecutionSummary
} from '../services/watchdogApi'
import type {
  ExecutionJournalEntry,
  StreamJournal,
  ExecutionSummary
} from '../types/watchdog'

/**
 * Hook for fetching execution journal entries
 */
export function useExecutionJournal(
  tradingDate: string,
  stream?: string,
  intentId?: string
) {
  const [entries, setEntries] = useState<ExecutionJournalEntry[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  
  useEffect(() => {
    if (!tradingDate) {
      setEntries([])
      return
    }
    
    setLoading(true)
    fetchExecutionJournal(tradingDate, stream, intentId).then(({ data, error: apiError }) => {
      if (apiError) {
        setError(apiError)
        setEntries([])
      } else if (data) {
        setEntries(data.entries || [])
        setError(null)
      }
      setLoading(false)
    })
  }, [tradingDate, stream, intentId])
  
  return { entries, loading, error }
}

/**
 * Hook for fetching stream journal
 */
export function useStreamJournal(tradingDate: string) {
  const [streams, setStreams] = useState<StreamJournal[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  
  useEffect(() => {
    if (!tradingDate) {
      setStreams([])
      return
    }
    
    setLoading(true)
    fetchStreamJournal(tradingDate).then(({ data, error: apiError }) => {
      if (apiError) {
        setError(apiError)
        setStreams([])
      } else if (data) {
        setStreams(data.streams || [])
        setError(null)
      }
      setLoading(false)
    })
  }, [tradingDate])
  
  return { streams, loading, error }
}

/**
 * Hook for fetching execution summary
 */
export function useExecutionSummary(tradingDate: string) {
  const [summary, setSummary] = useState<ExecutionSummary | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  
  useEffect(() => {
    if (!tradingDate) {
      setSummary(null)
      return
    }
    
    setLoading(true)
    fetchExecutionSummary(tradingDate).then(({ data, error: apiError }) => {
      if (apiError) {
        setError(apiError)
        setSummary(null)
      } else if (data) {
        setSummary(data)
        setError(null)
      }
      setLoading(false)
    })
  }, [tradingDate])
  
  return { summary, loading, error }
}
