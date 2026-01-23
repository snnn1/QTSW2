import { DAYS_OF_WEEK, AVAILABLE_TIMES } from '../utils/constants'
import { getStreamFiltersFromStorage } from '../utils/filterUtils'

export default function FiltersPanel({
  streamId,
  streamFilters,
  setStreamFilters,
  updateStreamFilter,
  getAvailableYears,
  getRelevantTimeSlots
}) {
  const filters = getStreamFiltersFromStorage(streamFilters, streamId)
  const availableYears = getAvailableYears()
  const hasFilters = (filters.exclude_days_of_week && filters.exclude_days_of_week.length > 0) || 
                    (filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0) || 
                    (filters.exclude_times && filters.exclude_times.length > 0) ||
                    (filters.include_years && filters.include_years.length > 0)
  
  return (
    <div className="bg-gray-800 rounded-lg p-4 mb-4">
      <div className="flex items-center justify-between mb-3">
        <h4 className="font-medium text-sm">Filters for {streamId}</h4>
        {hasFilters && (
          <span className="text-xs bg-blue-600 px-2 py-1 rounded">Active</span>
        )}
      </div>
      
      <div className={`grid grid-cols-1 md:grid-cols-4 gap-6`}>
        {/* Years Filter */}
        <div className="space-y-2">
          <div className="flex items-center justify-between mb-2">
            <label className="block text-xs font-medium text-gray-400">Isolate Years</label>
            {filters.include_years && filters.include_years.length > 0 && (
              <button
                type="button"
                onClick={(e) => {
                  e.preventDefault()
                  e.stopPropagation()
                  // Clear all year filters
                  setStreamFilters(prev => {
                    const updated = { ...prev }
                    if (updated[streamId]) {
                      updated[streamId] = {
                        ...updated[streamId],
                        include_years: []
                      }
                    } else {
                      updated[streamId] = {
                        exclude_days_of_week: [],
                        exclude_days_of_month: [],
                        exclude_times: [],
                        include_years: []
                      }
                    }
                    return updated
                  })
                }}
                className="text-xs text-blue-400 hover:text-blue-300 underline"
                title="Show all years"
              >
                Show All
              </button>
            )}
          </div>
          <div className="flex flex-wrap gap-1 max-h-32 overflow-y-auto">
            {availableYears.length > 0 ? (
              availableYears.map(year => {
                const isSelected = filters.include_years && filters.include_years.includes(year)
                return (
                  <button
                    key={year}
                    type="button"
                    onClick={(e) => {
                      e.preventDefault()
                      e.stopPropagation()
                      updateStreamFilter(streamId, 'include_years', year)
                    }}
                    className={`px-2 py-1 text-xs rounded cursor-pointer ${
                      isSelected
                        ? 'bg-green-600 text-white'
                        : 'bg-gray-700 hover:bg-gray-600'
                    }`}
                  >
                    {year}
                  </button>
                )
              })
            ) : (
              <span className="text-xs text-gray-500">No years available</span>
            )}
          </div>
          {filters.include_years && filters.include_years.length > 0 && (
            <div className="mt-2 text-xs text-gray-400">
              Selected: {filters.include_years.sort((a, b) => b - a).join(', ')}
            </div>
          )}
        </div>
        
        {/* Days of Week */}
        <div className="space-y-2">
          <label className="block text-xs font-medium mb-2 text-gray-400">Exclude Days</label>
          <div className="flex flex-wrap gap-1">
            {DAYS_OF_WEEK.map(dow => {
              const isExcluded = filters.exclude_days_of_week && filters.exclude_days_of_week.includes(dow)
              return (
                <button
                  key={dow}
                  type="button"
                  onClick={(e) => {
                    e.preventDefault()
                    e.stopPropagation()
                      updateStreamFilter(streamId, 'exclude_days_of_week', dow)
                  }}
                  className={`px-2 py-1 text-xs rounded cursor-pointer transition-colors ${
                    isExcluded
                      ? 'bg-red-600 text-white'
                      : 'bg-gray-700 hover:bg-gray-600'
                  }`}
                >
                  {dow.substring(0, 3)}
                </button>
              )
            })}
          </div>
        </div>
        
        {/* Days of Month */}
        <div className="relative dom-dropdown-container space-y-2">
          <div className="flex items-center justify-between mb-2">
            <label className="block text-xs font-medium text-gray-400">Exclude DOM</label>
            {filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0 && (
              <button
                type="button"
                onClick={(e) => {
                  e.preventDefault()
                  e.stopPropagation()
                  // Clear all day of month filters
                  setStreamFilters(prev => {
                    const updated = { ...prev }
                    if (updated[streamId]) {
                      updated[streamId] = {
                        ...updated[streamId],
                        exclude_days_of_month: []
                      }
                    } else {
                      updated[streamId] = {
                        exclude_days_of_week: [],
                        exclude_days_of_month: [],
                        exclude_times: [],
                        include_years: []
                      }
                    }
                    return updated
                  })
                }}
                className="text-xs text-blue-400 hover:text-blue-300 underline"
                title="Clear all excluded days"
              >
                Clear All
              </button>
            )}
          </div>
          <button
            type="button"
            onClick={(e) => {
              e.preventDefault()
              e.stopPropagation()
              const currentShow = streamFilters[streamId]?._showDomDropdown || false
              setStreamFilters(prev => {
                const currentFilters = prev[streamId] || {
                  exclude_days_of_week: [],
                  exclude_days_of_month: [],
                  exclude_times: [],
                  include_years: []
                }
                return {
                  ...prev,
                  [streamId]: {
                    ...currentFilters,
                    _showDomDropdown: !currentShow
                  }
                }
              })
            }}
            className="w-full px-2 py-1 text-xs bg-gray-700 border border-gray-600 rounded text-gray-100 text-left flex items-center justify-between hover:bg-gray-600"
          >
            <span>
              {filters.exclude_days_of_month && filters.exclude_days_of_month.length > 0
                ? `Selected: ${filters.exclude_days_of_month.sort((a, b) => a - b).join(', ')}`
                : 'Select days to exclude'}
            </span>
            <span className="ml-2">{streamFilters[streamId]?._showDomDropdown ? '▲' : '▼'}</span>
          </button>
          {streamFilters[streamId]?._showDomDropdown && (
            <div 
              className="absolute top-full left-0 right-0 w-full mt-1 bg-gray-800 border border-gray-600 rounded shadow-xl max-h-48 overflow-y-auto z-20"
              onClick={(e) => e.stopPropagation()}
              onMouseDown={(e) => e.stopPropagation()}
            >
              <div className="p-2 grid grid-cols-5 gap-1">
                {Array.from({length: 31}, (_, i) => i + 1).map(day => {
                  const isSelected = filters.exclude_days_of_month?.includes(day)
                  return (
                    <label
                      key={day}
                      onClick={(e) => {
                        e.preventDefault()
                        e.stopPropagation()
                        updateStreamFilter(streamId, 'exclude_days_of_month', day)
                      }}
                      className={`px-2 py-1 text-xs rounded cursor-pointer text-center transition-colors ${
                        isSelected
                          ? 'bg-red-600 text-white'
                          : 'bg-gray-700 text-gray-200 hover:bg-gray-600'
                      }`}
                    >
                      <input
                        type="checkbox"
                        checked={isSelected || false}
                        onChange={() => {}}
                        onClick={(e) => {
                          e.preventDefault()
                          e.stopPropagation()
                          updateStreamFilter(streamId, 'exclude_days_of_month', day)
                        }}
                        className="hidden"
                      />
                      {day}
                    </label>
                  )
                })}
              </div>
            </div>
          )}
        </div>
        
        {/* Times */}
        <div className="space-y-2">
          <label className="block text-xs font-medium mb-2 text-gray-400">Exclude Times</label>
          <div className="flex flex-wrap gap-1">
            {/* Only show relevant times for this stream */}
            {(() => {
              const relevantTimes = getRelevantTimeSlots(streamId) || AVAILABLE_TIMES
              const excludeTimes = filters.exclude_times || []
              return relevantTimes.map(time => {
                const isExcluded = excludeTimes.includes(time)
                return (
                  <button
                    key={time}
                    type="button"
                    onClick={(e) => {
                      e.preventDefault()
                      e.stopPropagation()
                      updateStreamFilter(streamId, 'exclude_times', time)
                    }}
                    className={`px-2 py-1 text-xs rounded font-mono cursor-pointer transition-colors ${
                      isExcluded
                        ? 'bg-red-600 text-white'
                        : 'bg-gray-700 hover:bg-gray-600'
                    }`}
                  >
                    {time}
                  </button>
                )
              })
            })()}
          </div>
        </div>
      </div>
    </div>
  )
}

