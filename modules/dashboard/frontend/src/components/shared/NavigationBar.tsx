/**
 * NavigationBar - Shared navigation component for all pages
 */
import { Link, useLocation } from 'react-router-dom'

export function NavigationBar() {
  const location = useLocation()
  
  const navItems = [
    { path: '/watchdog', label: 'Watchdog' },
    { path: '/journal', label: 'Journal' },
    { path: '/summary', label: 'Summary' },
    { path: '/pipeline', label: 'Pipeline' },
  ]
  
  return (
    <nav className="fixed top-0 left-0 right-0 h-10 bg-gray-900 border-b border-gray-700 z-50">
      <div className="container mx-auto px-4 h-full">
        <div className="flex gap-1 h-full">
          {navItems.map((item) => {
            const isActive = location.pathname === item.path
            return (
              <Link
                key={item.path}
                to={item.path}
                className={`px-4 py-2 text-sm font-medium transition-colors h-full flex items-center ${
                  isActive
                    ? 'bg-gray-800 text-white border-b-2 border-blue-500'
                    : 'text-gray-400 hover:text-white hover:bg-gray-800'
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
