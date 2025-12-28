/**
 * Reusable metric card component
 */
export function MetricCard({ label, value, button, className = '' }) {
  return (
    <div className={`bg-gray-900 rounded-lg p-4 border border-gray-700 ${className}`}>
      <div className="text-sm text-gray-400 mb-1">{label}</div>
      <div className="text-lg font-semibold mb-2 text-gray-300">{value}</div>
      {button && button}
    </div>
  )
}
















