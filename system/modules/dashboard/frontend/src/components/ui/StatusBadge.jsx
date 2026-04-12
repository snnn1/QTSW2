/**
 * Reusable status badge component
 */
export function StatusBadge({ status, className = '' }) {
  const statusConfig = {
    'not_started': { label: 'Not Started', bg: 'bg-gray-700', text: 'text-gray-400' },
    'active': { label: 'Active', bg: 'bg-gray-700', text: 'text-gray-200' },
    'complete': { label: 'Complete', bg: 'bg-green-600', text: 'text-white' },
    'failed': { label: 'Failed / Stalled', bg: 'bg-red-600', text: 'text-white' },
    'idle': { label: 'Idle', bg: 'bg-gray-700', text: 'text-gray-400' },
    'starting': { label: 'Starting', bg: 'bg-gray-700', text: 'text-gray-200' },
    'running': { label: 'Running', bg: 'bg-blue-600', text: 'text-white' },
  }

  const config = statusConfig[status] || statusConfig['idle']

  return (
    <div className={`px-3 py-1 rounded text-sm font-medium ${config.bg} ${config.text} ${className}`}>
      {config.label}
    </div>
  )
}
















