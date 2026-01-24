import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App.tsx'
import './index.css'

// Debug: Log when React is mounting
console.log('[Dashboard] Starting React app...')
console.log('[Dashboard] Root element:', document.getElementById('root'))

ReactDOM.createRoot(document.getElementById('root')).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
)

console.log('[Dashboard] React app mounted')



























