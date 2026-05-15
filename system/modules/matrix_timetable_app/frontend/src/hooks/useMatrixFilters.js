/**
 * Custom hook for managing matrix filters
 * 
 * This hook handles:
 * - Loading/saving filters from localStorage
 * - Updating filters for specific streams
 * - Providing filter state and update functions
 * 
 * Benefits:
 * - Separates filter logic from component
 * - Makes filter management reusable
 * - Easier to test filter logic independently
 */

import { useState, useEffect, useRef } from 'react'
import { loadAllFilters, saveAllFilters, getStreamFiltersFromStorage, getDefaultFilters } from '../utils/filterUtils'
import { getStreamFiltersConfig, saveStreamFiltersConfig } from '../api/matrixApi'

function hasExecutionFilterRules(filters = {}) {
  return Object.values(filters || {}).some((filter) => {
    if (!filter || typeof filter !== 'object') return false
    return (
      (Array.isArray(filter.exclude_days_of_week) && filter.exclude_days_of_week.length > 0) ||
      (Array.isArray(filter.exclude_days_of_month) && filter.exclude_days_of_month.length > 0) ||
      (Array.isArray(filter.exclude_times) && filter.exclude_times.length > 0) ||
      (Array.isArray(filter.include_streams) && filter.include_streams.length > 0)
    )
  })
}

function mergeServerFiltersIntoLocal(localFilters = {}, serverFilters = {}) {
  const merged = { ...(localFilters || {}) }
  Object.entries(serverFilters || {}).forEach(([streamId, serverFilter]) => {
    const localFilter = merged[streamId] || {}
    merged[streamId] = {
      ...getDefaultFilters(),
      ...localFilter,
      ...serverFilter,
      // include_years is UI-only and never written to configs/stream_filters.json.
      include_years: Array.isArray(localFilter.include_years) ? localFilter.include_years : []
    }
  })
  return merged
}

export function useMatrixFilters() {
  // Initialize filters from localStorage
  const [streamFilters, setStreamFilters] = useState(() => loadAllFilters())
  const [serverSyncReady, setServerSyncReady] = useState(false)
  const saveTimerRef = useRef(null)
  
  // Hydrate from durable server config when browser storage is empty/missing.
  useEffect(() => {
    let cancelled = false

    async function hydrateServerFilters() {
      try {
        if (typeof globalThis.fetch !== 'function') {
          return
        }
        const response = await getStreamFiltersConfig()
        if (cancelled) return
        const serverFilters = response?.stream_filters || {}
        setStreamFilters(prev => {
          const localHasRules = hasExecutionFilterRules(prev)
          const serverHasRules = hasExecutionFilterRules(serverFilters)
          if (!localHasRules && serverHasRules) {
            return mergeServerFiltersIntoLocal(prev, serverFilters)
          }
          return prev
        })
      } catch (error) {
        console.warn('Could not hydrate Matrix filters from backend config:', error)
      } finally {
        if (!cancelled) {
          setServerSyncReady(true)
        }
      }
    }

    hydrateServerFilters()
    return () => {
      cancelled = true
    }
  }, [])

  // Save filters locally immediately; persist execution-relevant filters to backend after hydration.
  useEffect(() => {
    saveAllFilters(streamFilters)
    if (!serverSyncReady || typeof globalThis.fetch !== 'function') {
      return
    }
    if (saveTimerRef.current) {
      clearTimeout(saveTimerRef.current)
    }
    saveTimerRef.current = setTimeout(() => {
      saveStreamFiltersConfig(streamFilters).catch(error => {
        console.warn('Could not persist Matrix filters to backend config:', error)
      })
    }, 250)
    return () => {
      if (saveTimerRef.current) {
        clearTimeout(saveTimerRef.current)
        saveTimerRef.current = null
      }
    }
  }, [streamFilters, serverSyncReady])
  
  // Update filter for a specific stream
  const updateStreamFilter = (streamId, filterType, value) => {
    setStreamFilters(prev => {
      const updated = { ...prev }
      
      // Initialize stream filters if they don't exist
      if (!updated[streamId]) {
        updated[streamId] = getDefaultFilters()
      }
      
      // Create a new filter object for this stream
      const currentFilters = {
        ...getDefaultFilters(),
        ...updated[streamId]
      }
      
      if (filterType === 'exclude_days_of_week') {
        const current = currentFilters.exclude_days_of_week || []
        if (current.includes(value)) {
          currentFilters.exclude_days_of_week = current.filter(d => d !== value)
        } else {
          currentFilters.exclude_days_of_week = [...current, value]
        }
      } else if (filterType === 'exclude_days_of_month') {
        const current = currentFilters.exclude_days_of_month || []
        const numValue = typeof value === 'number' ? value : parseInt(value)
        if (current.includes(numValue)) {
          currentFilters.exclude_days_of_month = current.filter(d => d !== numValue)
        } else {
          currentFilters.exclude_days_of_month = [...current, numValue]
        }
      } else if (filterType === 'exclude_times') {
        const current = currentFilters.exclude_times || []
        if (current.includes(value)) {
          currentFilters.exclude_times = current.filter(t => t !== value)
        } else {
          currentFilters.exclude_times = [...current, value]
        }
      } else if (filterType === 'include_years') {
        const current = currentFilters.include_years || []
        const numValue = typeof value === 'number' ? value : parseInt(value)
        if (current.includes(numValue)) {
          currentFilters.include_years = current.filter(y => y !== numValue)
        } else {
          currentFilters.include_years = [...current, numValue]
        }
      }
      
      return {
        ...updated,
        [streamId]: currentFilters
      }
    })
  }
  
  // Get filters for a specific stream
  const getFiltersForStream = (streamId) => {
    return getStreamFiltersFromStorage(streamFilters, streamId)
  }
  
  return {
    streamFilters,
    setStreamFilters,
    updateStreamFilter,
    getFiltersForStream
  }
}
























