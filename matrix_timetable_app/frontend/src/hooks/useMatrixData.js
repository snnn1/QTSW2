/**
 * Custom hook for managing matrix data loading
 * 
 * This hook handles:
 * - Loading data from the API
 * - Managing loading and error states
 * - Retry logic
 * - Available years extraction
 * 
 * Benefits:
 * - Separates data fetching logic from UI
 * - Makes data loading reusable
 * - Easier to test data fetching independently
 */

import { useState, useEffect, useCallback } from 'react'

const API_BASE = 'http://localhost:8000/api'

export function useMatrixData() {
  const [masterData, setMasterData] = useState([])
  const [masterLoading, setMasterLoading] = useState(false)
  const [masterError, setMasterError] = useState(null)
  const [availableYearsFromAPI, setAvailableYearsFromAPI] = useState([])
  
  // Load master matrix data
  const loadMasterMatrix = useCallback(async (rebuild = false, streamId = null) => {
    setMasterLoading(true)
    setMasterError(null)
    
    try {
      const endpoint = rebuild 
        ? `${API_BASE}/rebuild-master-matrix${streamId ? `?stream=${streamId}` : ''}`
        : `${API_BASE}/master-matrix`
      
      const response = await fetch(endpoint)
      
      if (!response.ok) {
        if (response.status === 404 && !rebuild) {
          setMasterError('No data available. Click "Build Matrix" to generate it.')
          setMasterData([])
          setMasterLoading(false)
          return
        }
        throw new Error(`HTTP error! status: ${response.status}`)
      }
      
      const data = await response.json()
      setMasterData(data.matrix || [])
      setAvailableYearsFromAPI(data.available_years || [])
      setMasterError(null)
    } catch (error) {
      console.error('Error loading master matrix:', error)
      setMasterError(error.message || 'Failed to load data')
      setMasterData([])
    } finally {
      setMasterLoading(false)
    }
  }, [])
  
  // Retry loading
  const retryLoad = useCallback(() => {
    loadMasterMatrix(false)
  }, [loadMasterMatrix])
  
  // Get available years from data
  const getAvailableYears = useCallback(() => {
    // Use years from API response if available
    if (availableYearsFromAPI && availableYearsFromAPI.length > 0) {
      return [...availableYearsFromAPI].sort((a, b) => b - a) // Newest first
    }
    
    // Fallback: extract from master data
    if (!masterData || masterData.length === 0) return []
    const years = new Set()
    masterData.forEach(row => {
      if (row.Date) {
        try {
          const date = new Date(row.Date)
          if (!isNaN(date.getTime())) {
            years.add(date.getFullYear())
          } else if (typeof row.Date === 'string') {
            const match = row.Date.match(/(\d{4})/)
            if (match) {
              years.add(parseInt(match[1]))
            }
          }
        } catch (e) {
          // Skip invalid dates
        }
      }
    })
    return Array.from(years).sort((a, b) => b - a)
  }, [masterData, availableYearsFromAPI])
  
  return {
    masterData,
    masterLoading,
    masterError,
    availableYearsFromAPI,
    loadMasterMatrix,
    retryLoad,
    getAvailableYears
  }
}




