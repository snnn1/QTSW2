import React from 'react'
import ReactDOM from 'react-dom/client'
import AppWatchdog from './AppWatchdog.tsx'
import ErrorBoundary from './components/ErrorBoundary'
import './index.css'

// Debug: Verify we're loading the watchdog app
console.log('[Watchdog] ==========================================')
console.log('[Watchdog] WATCHDOG APP LOADING')
console.log('[Watchdog] URL:', window.location.href)
console.log('[Watchdog] Port:', window.location.port)
console.log('[Watchdog] ==========================================')

const rootElement = document.getElementById('root')
if (!rootElement) {
  console.error('[Watchdog] Root element not found!')
} else {
  ReactDOM.createRoot(rootElement).render(
    <React.StrictMode>
      <ErrorBoundary>
        <AppWatchdog />
      </ErrorBoundary>
    </React.StrictMode>,
  )
  console.log('[Watchdog] Watchdog app mounted successfully')
}
