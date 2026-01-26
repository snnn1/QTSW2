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
  return (
    <WebSocketProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/" element={<Navigate to="/watchdog" replace />} />
          <Route path="/watchdog" element={<WatchdogPage />} />
          <Route path="/journal" element={<JournalPage />} />
          <Route path="/summary" element={<SummaryPage />} />
        </Routes>
      </BrowserRouter>
    </WebSocketProvider>
  )
}

export default AppWatchdog
