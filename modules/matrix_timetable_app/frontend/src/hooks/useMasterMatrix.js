/**
 * Hook for managing master matrix data loading
 */
import { useState, useCallback } from 'react'

// API base URL - uses environment variable if set, otherwise defaults to port 8000
const API_PORT = import.meta.env.VITE_API_PORT || '8000'
const API_BASE = `http://localhost:${API_PORT}/api`

export const useMasterMatrix = () => {
  const [masterData, setMasterData] = useState([])
  const [masterLoading, setMasterLoading] = useState(false)
  const [masterError, setMasterError] = useState(null)
  const [availableYearsFromAPI, setAvailableYearsFromAPI] = useState([])
  
  return {
    masterData,
    masterLoading,
    masterError,
    availableYearsFromAPI,
    setMasterData,
    setMasterLoading,
    setMasterError,
    setAvailableYearsFromAPI
  }
}

