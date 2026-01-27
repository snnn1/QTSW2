/**
 * App.tsx - Watchdog App
 * Standalone watchdog application - completely independent from dashboard
 */
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { WebSocketProvider } from './contexts/WebSocketContext'
import { WatchdogPage } from './WatchdogPage'
import { JournalPage } from './JournalPage'
import { SummaryPage } from './SummaryPage'
import './App.css'

function App() {
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

export default App
