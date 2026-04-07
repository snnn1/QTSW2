/**
 * Debug-only page: raw session/flatten rollup table. Use Watchdog stream table for day-to-day decisions.
 */
import { WatchdogNavigationBar } from './components/WatchdogNavigationBar'
import { SessionFlattenTab } from './components/watchdog/SessionFlattenTab'

export function SessionFlattenPage() {
  return (
    <div className="min-h-screen bg-black text-white">
      <WatchdogNavigationBar />
      <div className="p-8 pt-14 max-w-[1400px] mx-auto">
        <h1 className="text-2xl font-bold mb-2">Session &amp; flatten (debug)</h1>
        <p className="text-gray-400 text-sm mb-6">
          Deterministic audit: session close source, flatten trigger, and broker outcome per trading day and session class.
          Prefer the main Watchdog stream feed for flatten status; this page is for deep inspection.
        </p>
        <SessionFlattenTab />
      </div>
    </div>
  )
}
