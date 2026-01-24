/**
 * WatchdogHeader component - Global sticky header
 */
import { useMemo } from 'react'
import { CopyableText } from '../ui/CopyableText'
import { formatChicagoTime } from '../../utils/timeUtils'

interface WatchdogHeaderProps {
  runId: string | null
  engineStatus: 'ALIVE' | 'STALLED' | 'FAIL_CLOSED' | 'RECOVERY_IN_PROGRESS' | 'IDLE_MARKET_CLOSED'
  marketOpen: boolean | null
  connectionStatus: string | null
  dataFlowStatus: 'FLOWING' | 'STALLED' | 'ACCEPTABLE_SILENCE' | 'UNKNOWN'
  chicagoTime: string
  lastEngineTick: string | null
  lastSuccessfulPollTimestamp: number | null
  // PHASE 3.1: Identity invariants status
  identityInvariantsPass: boolean | null
  identityViolations: string[]
}

export function WatchdogHeader({
  runId,
  engineStatus,
  marketOpen,
  connectionStatus,
  dataFlowStatus,
  chicagoTime,
  lastEngineTick,
  lastSuccessfulPollTimestamp,
  identityInvariantsPass,
  identityViolations
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
        return <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">ENGINE ALIVE</span>
      case 'IDLE_MARKET_CLOSED':
        return (
          <span className="px-3 py-1.5 bg-gray-500 text-white rounded-full font-semibold text-sm whitespace-nowrap">
            ENGINE IDLE (MARKET CLOSED)
          </span>
        )
      case 'STALLED':
        return <span className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">ENGINE STALLED</span>
      case 'FAIL_CLOSED':
        return <span className="px-3 py-1.5 bg-red-700 text-white rounded-full font-semibold text-sm whitespace-nowrap">FAIL-CLOSED</span>
      case 'RECOVERY_IN_PROGRESS':
        return <span className="px-3 py-1.5 bg-amber-500 text-black rounded-full font-semibold text-sm whitespace-nowrap">RECOVERY IN PROGRESS</span>
      default:
        return <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">UNKNOWN</span>
    }
  }
  
  // Get broker connection status badge
  const getBrokerStatusBadge = () => {
    if (connectionStatus === null) {
      return <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">BROKER UNKNOWN</span>
    }
    if (connectionStatus === 'Connected') {
      return <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">BROKER CONNECTED</span>
    }
    return <span className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">BROKER DISCONNECTED</span>
  }
  
  // Get data flow status badge
  const getDataFlowBadge = () => {
    switch (dataFlowStatus) {
      case 'FLOWING':
        return <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">DATA FLOWING</span>
      case 'STALLED':
        return <span className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">DATA STALLED</span>
      case 'ACCEPTABLE_SILENCE':
        return <span className="px-3 py-1.5 bg-gray-500 text-white rounded-full font-semibold text-sm whitespace-nowrap">DATA SILENT (OK)</span>
      default:
        return <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">DATA UNKNOWN</span>
    }
  }
  
  // Get market status badge
  const getMarketStatusBadge = () => {
    if (marketOpen === null) {
      return <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">MARKET UNKNOWN</span>
    }
    if (marketOpen) {
      return <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap">MARKET OPEN</span>
    }
    return <span className="px-3 py-1.5 bg-gray-500 text-white rounded-full font-semibold text-sm whitespace-nowrap">MARKET CLOSED</span>
  }
  
  // PHASE 3.1: Get identity invariants badge
  const getIdentityBadge = () => {
    if (identityInvariantsPass === null) {
      return <span className="px-3 py-1.5 bg-gray-600 text-white rounded-full font-semibold text-sm whitespace-nowrap" title="Identity status not yet checked">IDENTITY UNKNOWN</span>
    }
    if (identityInvariantsPass) {
      return <span className="px-3 py-1.5 bg-green-600 text-white rounded-full font-semibold text-sm whitespace-nowrap" title="All identity invariants passed">IDENTITY OK</span>
    }
    const violationsText = identityViolations.length > 0 
      ? identityViolations.join('; ')
      : 'Unknown violation'
    return (
      <span 
        className="px-3 py-1.5 bg-red-600 text-white rounded-full font-semibold text-sm whitespace-nowrap cursor-help" 
        title={`Identity violations: ${violationsText}`}
      >
        IDENTITY VIOLATION
      </span>
    )
  }
  
  return (
    <header className="fixed top-10 left-0 right-0 h-16 bg-gray-900 border-b border-gray-700 z-40 flex items-center px-6">
      {/* Left */}
      <div className="flex items-center gap-4 flex-1">
        <h1 className="text-xl font-bold">QTSW2 Execution Watchdog</h1>
        {runId && <CopyableText text={runId} />}
      </div>
      
      {/* Center - Status Badges */}
      <div className="flex-1 flex justify-center items-center gap-2">
        {getEngineStatusBadge()}
        {getBrokerStatusBadge()}
        {getDataFlowBadge()}
        {getMarketStatusBadge()}
        {getIdentityBadge()}
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
