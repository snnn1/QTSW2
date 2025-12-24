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

import { useState, useEffect } from 'react'
import { loadAllFilters, saveAllFilters, getStreamFiltersFromStorage, getDefaultFilters } from '../utils/filterUtils'

export function useMatrixFilters() {
  // Initialize filters from localStorage
  const [streamFilters, setStreamFilters] = useState(() => loadAllFilters())
  
  // Save filters to localStorage whenever they change
  useEffect(() => {
    saveAllFilters(streamFilters)
  }, [streamFilters])
  
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

























