import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App.jsx'
import './index.css'

try {
  ReactDOM.createRoot(document.getElementById('root')).render(
    <React.StrictMode>
      <App />
    </React.StrictMode>,
  )
} catch (error) {
  console.error('Failed to render app:', error)
  document.getElementById('root').innerHTML = `
    <div style="padding: 20px; background: #1a1a1a; color: white; font-family: monospace;">
      <h1 style="color: #ef4444;">App Failed to Load</h1>
      <p>Error: ${error.message}</p>
      <pre style="background: #000; padding: 10px; overflow: auto;">${error.stack}</pre>
    </div>
  `
}



