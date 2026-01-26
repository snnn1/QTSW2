import React from 'react'
import ReactDOM from 'react-dom/client'
import AppWatchdog from './AppWatchdog.tsx'
import './index.css'

console.log('[Watchdog] Starting Watchdog app...')

ReactDOM.createRoot(document.getElementById('root')).render(
  <React.StrictMode>
    <AppWatchdog />
  </React.StrictMode>,
)

console.log('[Watchdog] Watchdog app mounted')
