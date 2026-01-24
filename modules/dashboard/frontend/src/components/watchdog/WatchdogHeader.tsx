/**
 * WatchdogHeader component - Global sticky header
 */
import { useMemo } from 'react'
import { CopyableText } from '../ui/CopyableText'
import { formatChicagoTime } from '../../utils/timeUtils'

interface WatchdogHeaderProps {
  runId: string | null
  engineStatus: 'ALIVE' | 'STALLED' | 'FAIL_CLOSED' | 'RECOVERY_IN_PROGRESS'
  chicagoTime: string
  lastEngineTick: string | null
  lastSuccessfulPollTimestamp: number | null
}

export function WatchdogHeader({
  runId,
  engineStatus,
  chicagoTime,
  lastEngineTick,
  lastSuccessfulPollTimestamp
}: WatchdogHeaderProps) {
  // Compute data freshness
  const dataFreshness = useMemo(() => {
    if (!lastSuccessfulPollTimestamp) return 'STALE'
    const elapsed = Date.now() - lastSuccessfulPollTimestamp
    return elapsed > 10000 ? 'STALE' : 'OK' // 10 second threshold
  }, [lastSuccessfulPollTimestamp])
  
  // Get engine status badge
  const getEngineStatusBadge = () => {
    switch (engineStatus) {
      case 'ALIVE':
        return <span className="px-4 py-2 bg-green-600 text-white rounded-full font-semibold">ENGINE ALIVE</span>
      case 'STALLED':
        return <span className="px-4 py-2 bg-red-600 text-white rounded-full font-semibold">ENGINE STALLED</span>
      case 'FAIL_CLOSED':
        return <span className="px-4 py-2 bg-red-700 text-white rounded-full font-semibold">FAIL-CLOSED</span>
      case 'RECOVERY_IN_PROGRESS':
        return <span className="px-4 py-2 bg-amber-500 text-black rounded-full font-semibold">RECOVERY IN PROGRESS</span>
      default:
        return <span className="px-4 py-2 bg-gray-600 text-white rounded-full font-semibold">UNKNOWN</span>
    }
  }
  
  return (
    <header className="fixed top-0 left-0 right-0 h-16 bg-gray-900 border-b border-gray-700 z-50 flex items-center px-6">
      {/* Left */}
      <div className="flex items-center gap-4 flex-1">
        <h1 className="text-xl font-bold">QTSW2 Execution Watchdog</h1>
        {runId && <CopyableText text={runId} />}
      </div>
      
      {/* Center */}
      <div className="flex-1 flex justify-center">
        {getEngineStatusBadge()}
      </div>
      
      {/* Right */}
      <div className="flex items-center gap-4 flex-1 justify-end">
        <div className={`text-sm ${dataFreshness === 'OK' ? 'text-green-500' : 'text-red-500'}`}>
          Data Freshness: {dataFreshness}
        </div>
        <div className="text-sm font-mono">{chicagoTime}</div>
        {lastEngineTick && (
          <div className="text-xs text-gray-400">
            Last Tick: {formatChicagoTime(lastEngineTick)}
          </div>
        )}
      </div>
    </header>
  )
}
