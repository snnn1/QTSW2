import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { readFileSync } from 'fs'
import { resolve } from 'path'

// Simple plugin to serve index-watchdog.html at root
const watchdogHtmlPlugin = () => ({
  name: 'watchdog-html',
  configureServer(server) {
    server.middlewares.use((req, res, next) => {
      // Serve index-watchdog.html at root instead of index.html
      if (req.url === '/' || req.url === '/index.html') {
        const htmlPath = resolve(__dirname, 'index-watchdog.html')
        const html = readFileSync(htmlPath, 'utf-8')
        res.setHeader('Content-Type', 'text/html')
        res.end(html)
        return
      }
      next()
    })
  }
})

export default defineConfig({
  plugins: [react(), watchdogHtmlPlugin()],
  build: {
    rollupOptions: {
      input: resolve(__dirname, 'index-watchdog.html')
    }
  },
  server: {
    port: 5175,
    proxy: {
      '/api': {
        target: 'http://localhost:8002',
        changeOrigin: true
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
