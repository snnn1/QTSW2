/**
 * AppWatchdog.tsx - Watchdog App
 * Independent app for Watchdog, Journal, and Summary pages
 */
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { WebSocketProvider } from './contexts/WebSocketContext'
import { WatchdogPage } from './pages/WatchdogPage'
import { JournalPage } from './pages/JournalPage'
import { SummaryPage } from './pages/SummaryPage'
import './App.css'

function AppWatchdog() {
  try {
    return (
      <WebSocketProvider>
        <BrowserRouter basename="/">
          <Routes>
            <Route path="/" element={<Navigate to="/watchdog" replace />} />
            <Route path="/index-watchdog.html" element={<Navigate to="/watchdog" replace />} />
            <Route path="/watchdog" element={<WatchdogPage />} />
            <Route path="/journal" element={<JournalPage />} />
            <Route path="/summary" element={<SummaryPage />} />
          </Routes>
        </BrowserRouter>
      </WebSocketProvider>
    )
  } catch (error) {
    console.error('[AppWatchdog] Error rendering app:', error)
    return (
      <div style={{ padding: '20px', color: 'red' }}>
        <h1>Error Loading Watchdog App</h1>
        <pre>{String(error)}</pre>
      </div>
    )
  }
}

export default AppWatchdog
