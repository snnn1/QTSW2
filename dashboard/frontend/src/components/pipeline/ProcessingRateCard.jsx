/**
 * Processing Rate Card component
 */
export function ProcessingRateCard({ rowsPerMin }) {
  if (rowsPerMin <= 0) return null

  return (
    <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
      <div className="text-sm text-gray-400 mb-1">Processing Rate</div>
      <div className="text-lg font-semibold text-gray-300">{rowsPerMin.toLocaleString()} rows/min</div>
    </div>
  )
}




