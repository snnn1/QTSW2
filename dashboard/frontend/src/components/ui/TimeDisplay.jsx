/**
 * Time display component for Chicago time and countdown
 */
export function TimeDisplay({ label, time, className = '' }) {
  return (
    <div className={`flex items-center gap-2 ${className}`}>
      <span className="text-xs text-gray-400">{label}:</span>
      <span className="text-sm font-mono font-semibold text-gray-200 bg-gray-700 px-2 py-1 rounded">
        {time}
      </span>
    </div>
  )
}




