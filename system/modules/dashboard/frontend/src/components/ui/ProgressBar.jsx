/**
 * Reusable progress bar component
 */
export function ProgressBar({ percent, label, showPercent = true, className = '' }) {
  return (
    <div className={className}>
      {label && (
        <div className="flex justify-between text-sm text-gray-400 mb-1">
          <span>{label}</span>
          {showPercent && <span>{percent.toFixed(1)}%</span>}
        </div>
      )}
      <div className="w-full bg-gray-800 rounded-full h-2.5">
        <div
          className="bg-gray-600 h-2.5 rounded-full transition-all duration-300"
          style={{ width: `${Math.min(100, Math.max(0, percent))}%` }}
        ></div>
      </div>
    </div>
  )
}
















