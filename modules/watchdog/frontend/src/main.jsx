import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App.tsx'
import ErrorBoundary from './components/ErrorBoundary'
import './index.css'

const rootElement = document.getElementById('root')
if (!rootElement) {
  console.error('[Watchdog] Root element not found!')
} else {
  // Fix B.1: Remove StrictMode to prevent double-mount WebSocket connections
  // StrictMode causes effects to run twice in dev mode, which creates duplicate WS connections
  // The WebSocketContext already has guards, but removing StrictMode is cleaner for this app
  ReactDOM.createRoot(rootElement).render(
    <ErrorBoundary>
      <App />
    </ErrorBoundary>,
  )
  console.log('[Watchdog] Watchdog app mounted successfully')
}
