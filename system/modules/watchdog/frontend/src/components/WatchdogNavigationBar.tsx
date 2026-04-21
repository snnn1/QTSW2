/**
 * WatchdogNavigationBar - Navigation for Watchdog app only
 * No cross-app links - Watchdog app is independent
 */
import { Link, useLocation } from 'react-router-dom'

export function WatchdogNavigationBar() {
  const location = useLocation()
  
  const navItems = [
    { path: '/watchdog', label: 'Watchdog' },
    { path: '/operator', label: 'Operator' },
    { path: '/daily', label: 'Daily' },
    { path: '/journal', label: 'Journal' },
    { path: '/summary', label: 'Summary' },
  ]
  
  return (
    <nav className="fixed top-0 left-0 right-0 z-50 border-b border-slate-700/70 bg-slate-950/90 backdrop-blur-md">
      <div className="watchdog-content h-11 px-2">
        <div className="flex h-full items-center gap-1">
          {navItems.map((item) => {
            const isActive = location.pathname === item.path
            return (
              <Link
                key={item.path}
                to={item.path}
                className={`inline-flex h-8 items-center rounded-full px-4 text-sm font-medium transition-colors ${
                  isActive
                    ? 'bg-slate-800 text-white shadow-[inset_0_0_0_1px_rgba(96,165,250,0.4)]'
                    : 'text-slate-400 hover:bg-slate-800/80 hover:text-white'
                }`}
              >
                {item.label}
              </Link>
            )
          })}
        </div>
      </div>
    </nav>
  )
}
