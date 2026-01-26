import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  resolve: {
    extensions: ['.tsx', '.ts', '.jsx', '.js'],
  },
  build: {
    outDir: 'dist-dashboard',
    assetsDir: 'assets',
    sourcemap: false,
    minify: 'esbuild',
    rollupOptions: {
      input: path.resolve(__dirname, 'index-dashboard.html'),
      output: {
        manualChunks: {
          'react-vendor': ['react', 'react-dom'],
        },
      },
    },
  },
  server: {
    port: 5173, // Dashboard on port 5173
    proxy: {
      '/api': {
        target: 'http://localhost:8001',
        changeOrigin: true,
        onError: (err, req, res) => {
          if (err.code !== 'ECONNREFUSED') {
            console.error('Proxy error:', err.message)
          }
        },
      },
      '/ws': {
        target: 'ws://localhost:8001',
        ws: true,
        timeout: 0,
        configure: (proxy, options) => {
          proxy.on('proxyReqWs', (proxyReq, req, socket) => {
            socket.setKeepAlive(true, 60000)
          })
        },
        onError: (err, req, res) => {
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
        },
      },
    },
  },
  logLevel: 'warn',
  clearScreen: false,
})
