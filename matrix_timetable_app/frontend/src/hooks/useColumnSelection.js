/**
 * Custom hook for managing column selection
 * 
 * This hook handles:
 * - Loading/saving selected columns from localStorage
 * - Toggling column visibility
 * - Managing column selector visibility state
 * 
 * Benefits:
 * - Separates column logic from component
 * - Makes column management reusable
 * - Easier to test column logic independently
 */

import { useState, useEffect } from 'react'
import { DEFAULT_COLUMNS } from '../utils/constants'
import { sortColumnsByDefaultOrder } from '../utils/columnUtils'

export function useColumnSelection() {
  // Per-stream selected columns (persisted in localStorage)
  const [selectedColumns, setSelectedColumns] = useState(() => {
    const saved = localStorage.getItem('matrix_selected_columns')
    if (saved) {
      try {
        return JSON.parse(saved)
      } catch {
        return {}
      }
    }
    return {}
  })
  
  // Column selector visibility
  const [showColumnSelector, setShowColumnSelector] = useState(false)
  
  // Save selected columns to localStorage whenever they change
  useEffect(() => {
    localStorage.setItem('matrix_selected_columns', JSON.stringify(selectedColumns))
  }, [selectedColumns])
  
  // Toggle column visibility
  const toggleColumn = (col, activeTab) => {
    setSelectedColumns(prev => {
      const currentTab = activeTab
      const currentCols = prev[currentTab] || DEFAULT_COLUMNS
      
      // Toggle column
      const newCols = currentCols.includes(col)
        ? currentCols.filter(c => c !== col)
        : [...currentCols, col]
      
      // Sort to maintain DEFAULT_COLUMNS order
      const sortedCols = sortColumnsByDefaultOrder(newCols)
      
      return {
        ...prev,
        [currentTab]: sortedCols
      }
    })
  }
  
  return {
    selectedColumns,
    setSelectedColumns,
    showColumnSelector,
    setShowColumnSelector,
    toggleColumn
  }
}




