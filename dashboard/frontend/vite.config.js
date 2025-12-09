import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:8000',
        changeOrigin: true,
        onError: (err, req, res) => {
          // Silently ignore connection errors - backend might be down
          // Don't log ECONNREFUSED errors to reduce spam
          if (err.code !== 'ECONNREFUSED') {
            console.error('Proxy error:', err.message)
          }
        },
      },
      '/ws': {
        target: 'ws://localhost:8000',
        ws: true,
        onError: (err, req, res) => {
          // Silently ignore WebSocket connection errors
          if (err.code !== 'ECONNREFUSED') {
            console.error('WebSocket proxy error:', err.message)
          }
        },
      },
      '/health': {
        target: 'http://localhost:8000',
        changeOrigin: true,
        onError: (err, req, res) => {
          // Silently ignore health check connection errors
          // Health checks fail when backend is down - this is expected
          // Don't spam console with ECONNREFUSED errors
        },
      },
    },
  },
  // Suppress proxy connection errors in logs
  logLevel: 'warn',
  clearScreen: false, // Don't clear screen on errors
})















