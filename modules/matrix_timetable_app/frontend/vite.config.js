import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'
import { fileURLToPath } from 'url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const modulesConfigDir = path.resolve(__dirname, '../../config')

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@qtsw2-config': modulesConfigDir,
    },
  },
  server: {
    port: 5174,
    host: true,
    proxy: {
      '/api/watchdog': {
        target: 'http://127.0.0.1:8002',
        changeOrigin: true,
      },
    },
    fs: {
      allow: [__dirname, path.resolve(__dirname, '../..')],
    },
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: './src/test/setup.js'
  }
})



