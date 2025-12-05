/**
 * Hook for managing master matrix data loading
 */
import { useState, useCallback } from 'react'

const API_BASE = 'http://localhost:8000/api'

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

