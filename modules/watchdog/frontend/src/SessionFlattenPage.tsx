/**
 * Session & flatten lifecycle audit (operational control surface).
 */
import { WatchdogNavigationBar } from './components/WatchdogNavigationBar'
import { SessionFlattenTab } from './components/watchdog/SessionFlattenTab'

export function SessionFlattenPage() {
  return (
    <div className="min-h-screen bg-black text-white">
      <WatchdogNavigationBar />
      <div className="p-8 pt-14 max-w-[1400px] mx-auto">
        <h1 className="text-2xl font-bold mb-2">Session &amp; flatten</h1>
        <p className="text-gray-400 text-sm mb-6">
          Deterministic audit: session close source, flatten trigger, and broker outcome per trading day and session class.
        </p>
        <SessionFlattenTab />
      </div>
    </div>
  )
}
