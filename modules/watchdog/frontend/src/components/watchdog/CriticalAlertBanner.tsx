/**
 * CriticalAlertBanner component
 * Only renders if critical/degraded conditions exist
 */
interface Alert {
  type: 'critical' | 'degraded'
  message: string
  scrollTo?: string
}

interface CriticalAlertBannerProps {
  alerts: Alert[]
}

export function CriticalAlertBanner({ alerts }: CriticalAlertBannerProps) {
  if (alerts.length === 0) {
    return null
  }
  
  const handleClick = (scrollTo?: string) => {
    if (scrollTo) {
      const element = document.getElementById(scrollTo)
      if (element) {
        element.scrollIntoView({ behavior: 'smooth', block: 'start' })
      }
    }
  }
  
  return (
    <div className="mt-16">
      {alerts.map((alert, index) => (
        <div
          key={index}
          onClick={() => handleClick(alert.scrollTo)}
          className={`w-full py-3 px-4 cursor-pointer ${
            alert.type === 'critical'
              ? 'bg-red-600 text-white pulse-subtle'
              : 'bg-amber-500 text-black'
          }`}
        >
          <div className="container mx-auto flex items-center justify-between">
            <span className="font-semibold">{alert.message}</span>
            {alert.scrollTo && <span className="text-sm">→</span>}
          </div>
        </div>
      ))}
    </div>
  )
}
