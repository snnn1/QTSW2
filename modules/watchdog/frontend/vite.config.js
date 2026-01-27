import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5175,
    proxy: {
      '/api': {
        target: 'http://localhost:8002',
        changeOrigin: true,
        timeout: 60000, // 60 second timeout - backend may be slow on first load
        proxyTimeout: 60000
      },
      '/ws': {
        target: 'ws://localhost:8002',
        ws: true,
        changeOrigin: true
      },
      '/health': {
        target: 'http://localhost:8002',
        changeOrigin: true
      }
    }
  }
})
