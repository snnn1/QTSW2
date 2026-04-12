/**
 * AppDashboard.tsx - Pipeline Dashboard App
 * Independent app for Pipeline dashboard only
 */
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { WebSocketProvider } from './contexts/WebSocketContext'
import { PipelinePage } from './pages/PipelinePage'
import './App.css'

function AppDashboard() {
  return (
    <WebSocketProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/" element={<Navigate to="/pipeline" replace />} />
          <Route path="/pipeline" element={<PipelinePage />} />
        </Routes>
      </BrowserRouter>
    </WebSocketProvider>
  )
}

export default AppDashboard
