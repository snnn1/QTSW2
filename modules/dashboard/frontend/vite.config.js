import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  // Production build configuration
  build: {
    outDir: 'dist',
    assetsDir: 'assets',
    sourcemap: false, // Disable source maps in production
    minify: 'esbuild', // Use esbuild instead of terser (faster, no extra dependency)
    rollupOptions: {
      output: {
        manualChunks: {
          'react-vendor': ['react', 'react-dom'],
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:8001',
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
        target: 'ws://localhost:8001',
        ws: true,
        // Increase timeout for long-lived WebSocket connections
        timeout: 0, // No timeout (infinite)
        // Configure WebSocket proxy to keep connections alive
        configure: (proxy, options) => {
          proxy.on('proxyReqWs', (proxyReq, req, socket) => {
            // Keep WebSocket connection alive
            socket.setKeepAlive(true, 60000) // 60 second keepalive
          })
        },
        onError: (err, req, res) => {
          // Silently ignore WebSocket connection errors
          if (err.code !== 'ECONNREFUSED') {
            console.error('WebSocket proxy error:', err.message)
          }
        },
      },
      '/health': {
        target: 'http://localhost:8001',
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















