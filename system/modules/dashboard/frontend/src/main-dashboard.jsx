import React from 'react'
import ReactDOM from 'react-dom/client'
import AppDashboard from './AppDashboard.tsx'
import './index.css'

console.log('[Dashboard] Starting Dashboard app...')

ReactDOM.createRoot(document.getElementById('root')).render(
  <React.StrictMode>
    <AppDashboard />
  </React.StrictMode>,
)

console.log('[Dashboard] Dashboard app mounted')
