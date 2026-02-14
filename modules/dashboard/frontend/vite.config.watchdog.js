import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { readFileSync } from 'fs'
import { resolve } from 'path'

// Simple plugin to serve index-watchdog.html for all watchdog routes
// CRITICAL: Must use transformIndexHtml so React refresh preamble gets injected (fixes "can't detect preamble" error)
// CRITICAL: Must catch /watchdog, /journal, /summary etc. so refresh on those routes serves watchdog app (not dashboard)
const WATCHDOG_ROUTES = ['/', '/index.html', '/index-watchdog.html', '/watchdog', '/journal', '/summary']

const watchdogHtmlPlugin = () => ({
  name: 'watchdog-html',
  enforce: 'pre',
  configureServer(server) {
    server.middlewares.use(async (req, res, next) => {
      const pathname = (req.url || '').split('?')[0]
      if (WATCHDOG_ROUTES.includes(pathname)) {
        try {
          const htmlPath = resolve(__dirname, 'index-watchdog.html')
          let html = readFileSync(htmlPath, 'utf-8')
          html = await server.transformIndexHtml(req.url, html)
          res.setHeader('Content-Type', 'text/html')
          res.end(html)
        } catch (err) {
          console.error('[watchdog-html] transformIndexHtml failed:', err)
          next(err)
        }
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
