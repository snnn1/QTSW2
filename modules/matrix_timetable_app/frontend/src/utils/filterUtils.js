// Default filter structure for any stream
export function getDefaultFilters() {
  return {
    exclude_days_of_week: [],
    exclude_days_of_month: [],
    exclude_times: [],
    include_years: [],
    include_streams: [] // Only for master stream - empty means all streams included
  }
}

// Storage key for filters
export const FILTER_STORAGE_KEY = 'matrix_stream_filters'

// Load all filters from localStorage on startup
export function loadAllFilters() {
  try {
    const saved = localStorage.getItem(FILTER_STORAGE_KEY)
    if (saved) {
      const parsed = JSON.parse(saved)
      // Validate structure - ensure all entries have the correct shape
      const validated = {}
      Object.keys(parsed).forEach(streamId => {
        const filters = parsed[streamId]
        validated[streamId] = {
          exclude_days_of_week: Array.isArray(filters.exclude_days_of_week) ? filters.exclude_days_of_week : [],
          exclude_days_of_month: Array.isArray(filters.exclude_days_of_month) ? filters.exclude_days_of_month : [],
          exclude_times: Array.isArray(filters.exclude_times) ? filters.exclude_times : [],
          include_years: Array.isArray(filters.include_years) ? filters.include_years : [],
          include_streams: Array.isArray(filters.include_streams) ? filters.include_streams : []
        }
      })
      return validated
    }
  } catch (error) {
    console.error('Error loading filters from localStorage:', error)
  }
  return {}
}

// Save all filters to localStorage
export function saveAllFilters(filters) {
  try {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(filters))
  } catch (error) {
    console.error('Error saving filters to localStorage:', error)
  }
}

// Get filters for a specific stream (with defaults if not exists)
export function getStreamFiltersFromStorage(allFilters, streamId) {
  if (allFilters[streamId]) {
    return {
      ...getDefaultFilters(),
      ...allFilters[streamId]
    }
  }
  return getDefaultFilters()
}

























