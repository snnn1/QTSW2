/**
 * DashboardNavigationBar - Navigation for Pipeline Dashboard app only
 * No cross-app links - Dashboard app is independent
 */
import { Link, useLocation } from 'react-router-dom'

export function DashboardNavigationBar() {
  const location = useLocation()
  
  // Dashboard app only has Pipeline route
  // No navigation needed for single-page app, but keeping component for consistency
  // If you want to remove navigation entirely, just remove this component usage
  
  return (
    <nav className="fixed top-0 left-0 right-0 h-10 bg-gray-900 border-b border-gray-700 z-50">
      <div className="container mx-auto px-4 h-full">
        <div className="flex gap-1 h-full">
          <Link
            to="/pipeline"
            className={`px-4 py-2 text-sm font-medium transition-colors h-full flex items-center ${
              location.pathname === '/pipeline'
                ? 'bg-gray-800 text-white border-b-2 border-blue-500'
                : 'text-gray-400 hover:text-white hover:bg-gray-800'
            }`}
          >
            Pipeline
          </Link>
        </div>
      </div>
    </nav>
  )
}
