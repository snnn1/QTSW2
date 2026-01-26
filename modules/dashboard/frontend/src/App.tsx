/**
 * @deprecated This combined app is deprecated. Use AppDashboard.tsx or AppWatchdog.tsx instead.
 * Kept for dev/testing purposes only.
 * 
 * To run separate apps:
 * - Dashboard: npm run dev:dashboard (port 5173)
 * - Watchdog: npm run dev:watchdog (port 5174)
 * 
 * App.tsx - Main app with routing (DEPRECATED)
 */
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { WebSocketProvider } from './contexts/WebSocketContext'
import { WatchdogPage } from './pages/WatchdogPage'
import { JournalPage } from './pages/JournalPage'
import { SummaryPage } from './pages/SummaryPage'
import { PipelinePage } from './pages/PipelinePage'
import './App.css'

function App() {
  return (
    <WebSocketProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/" element={<Navigate to="/pipeline" replace />} />
          <Route path="/watchdog" element={<WatchdogPage />} />
          <Route path="/journal" element={<JournalPage />} />
          <Route path="/summary" element={<SummaryPage />} />
          <Route path="/pipeline" element={<PipelinePage />} />
        </Routes>
      </BrowserRouter>
    </WebSocketProvider>
  )
}

export default App
