export default function ProfitTable({ data, periodType }) {
  const formatCurrency = (value) => {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 0,
      maximumFractionDigits: 0
    }).format(value)
  }
  
  const getOrdinalSuffix = (num) => {
    const j = num % 10
    const k = num % 100
    if (j === 1 && k !== 11) return 'st'
    if (j === 2 && k !== 12) return 'nd'
    if (j === 3 && k !== 13) return 'rd'
    return 'th'
  }
  
  // Get all unique streams from data
  const allStreams = new Set()
  Object.values(data).forEach(periodData => {
    if (periodData && typeof periodData === 'object' && !Array.isArray(periodData)) {
      Object.keys(periodData).forEach(stream => {
        // Skip 'profit' and 'trades' keys (legacy format)
        if (stream !== 'profit' && stream !== 'trades') {
          allStreams.add(stream)
        }
      })
    }
  })
  const sortedStreams = Array.from(allStreams).sort()
  
  // Debug logging for month breakdown
  if (periodType === 'month' && sortedStreams.length === 0 && Object.keys(data).length > 0) {
    console.warn('[ProfitTable] Month data has periods but no streams found:', {
      periods: Object.keys(data),
      samplePeriod: Object.keys(data)[0],
      sampleValue: data[Object.keys(data)[0]]
    })
  }
  
  // Get sorted periods
  // For DOM (Day of Month) and DOY (Day of Year), sort numerically; for month/year, sort descending (newest first); otherwise sort as strings
  const sortedPeriods = periodType === 'dom' || periodType === 'doy'
    ? Object.keys(data).sort((a, b) => parseInt(a) - parseInt(b))
    : periodType === 'month'
    ? Object.keys(data).sort((a, b) => {
        // Sort by year-month in descending order (newest first)
        // Format is "YYYY-MM" (e.g., "2024-01", "2025-12")
        return b.localeCompare(a) // Reverse string comparison for descending order
      })
    : periodType === 'year'
    ? Object.keys(data).sort((a, b) => {
        // Sort by year in descending order (newest first)
        return parseInt(b) - parseInt(a)
      })
    : Object.keys(data).sort()
  
  if (sortedPeriods.length === 0) {
    return (
      <div className="text-center py-8 text-gray-400">
        No data available
      </div>
    )
  }
  
  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse border border-gray-700">
        <thead>
          <tr className="bg-gray-800">
            <th className="p-3 border border-gray-700 text-left font-semibold bg-gray-800">
              {periodType === 'time' ? 'Time' : periodType === 'day' ? 'DOW' : periodType === 'dom' ? 'Day of Month' : periodType === 'doy' ? 'Day of Year' : periodType === 'month' ? 'Month' : 'Year'}
            </th>
            {sortedStreams.map(stream => (
              <th key={stream} className="p-3 border border-gray-700 text-right font-semibold bg-gray-800">
                {stream}
              </th>
            ))}
            <th className="p-3 border border-gray-700 text-right font-semibold bg-gray-700">
              Total
            </th>
          </tr>
        </thead>
        <tbody>
          {sortedPeriods.map(period => {
            const periodData = data[period]
            let total = 0
            
            // Validate periodData structure
            if (!periodData || typeof periodData !== 'object' || Array.isArray(periodData)) {
              console.warn(`[ProfitTable] Invalid periodData for ${periodType} period "${period}":`, periodData)
            }
            
            return (
              <tr key={period} className="hover:bg-gray-900">
                <td className="p-3 border border-gray-700 font-medium">
                  {periodType === 'time'
                    ? period // Already the time string (e.g., "08:00")
                    : periodType === 'day' 
                    ? period // Already the day of week name (Monday, Tuesday, etc.)
                    : periodType === 'dom'
                    ? `${period}${getOrdinalSuffix(period)}` // e.g., "1st", "2nd", "3rd"
                    : periodType === 'doy'
                    ? `Day ${period}`
                    : periodType === 'month'
                    ? (() => {
                        // Period should be in format "YYYY-MM" (e.g., "2024-01")
                        // Convert to full date string by appending '-01' and parse
                        try {
                          const dateStr = String(period) + '-01'
                          const date = new Date(dateStr)
                          if (isNaN(date.getTime())) {
                            console.warn(`[ProfitTable] Invalid month period format: ${period}`)
                            return String(period)
                          }
                          return date.toLocaleDateString('en-US', { year: 'numeric', month: 'long' })
                        } catch (e) {
                          console.error(`[ProfitTable] Error parsing month period: ${period}`, e)
                          return String(period)
                        }
                      })()
                    : period}
                </td>
                {sortedStreams.length > 0 ? sortedStreams.map(stream => {
                  const profit = (periodData && typeof periodData === 'object' && !Array.isArray(periodData)) 
                    ? (periodData[stream] || 0)
                    : 0
                  total += profit
                  return (
                    <td key={stream} className={`p-3 border border-gray-700 text-right ${
                      profit > 0 ? 'text-green-400' : profit < 0 ? 'text-red-400' : 'text-gray-400'
                    }`}>
                      {formatCurrency(profit)}
                    </td>
                  )
                }) : (
                  <td colSpan={sortedStreams.length || 1} className="p-3 border border-gray-700 text-center text-gray-400">
                    No stream data
                  </td>
                )}
                <td className={`p-3 border border-gray-700 text-right font-semibold bg-gray-800 ${
                  total > 0 ? 'text-green-400' : total < 0 ? 'text-red-400' : 'text-gray-400'
                }`}>
                  {formatCurrency(total)}
                </td>
              </tr>
            )
          })}
          {/* Totals row */}
          <tr className="bg-gray-800 font-semibold">
            <td className="p-3 border border-gray-700">Total</td>
            {sortedStreams.map(stream => {
              const streamTotal = sortedPeriods.reduce((sum, period) => {
                return sum + (data[period][stream] || 0)
              }, 0)
              return (
                <td key={stream} className={`p-3 border border-gray-700 text-right ${
                  streamTotal > 0 ? 'text-green-400' : streamTotal < 0 ? 'text-red-400' : 'text-gray-400'
                }`}>
                  {formatCurrency(streamTotal)}
                </td>
              )
            })}
            <td className={`p-3 border border-gray-700 text-right bg-gray-700 ${
              sortedPeriods.reduce((sum, period) => {
                return sum + sortedStreams.reduce((s, stream) => s + (data[period][stream] || 0), 0)
              }, 0) > 0 ? 'text-green-400' : 'text-red-400'
            }`}>
              {formatCurrency(
                sortedPeriods.reduce((sum, period) => {
                  return sum + sortedStreams.reduce((s, stream) => s + (data[period][stream] || 0), 0)
                }, 0)
              )}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  )
}

























