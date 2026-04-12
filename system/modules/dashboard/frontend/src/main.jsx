import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App.jsx'
import './index.css'
import { WebSocketProvider } from './contexts/WebSocketContext'

// Debug: Log when React is mounting
console.log('[Dashboard] Starting React app...')
console.log('[Dashboard] Root element:', document.getElementById('root'))

ReactDOM.createRoot(document.getElementById('root')).render(
  <React.StrictMode>
    <WebSocketProvider>
      <App />
    </WebSocketProvider>
  </React.StrictMode>,
)

console.log('[Dashboard] React app mounted')



























