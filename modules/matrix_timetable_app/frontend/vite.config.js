import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5174,
    host: true
  }
  // Vite automatically exposes VITE_* environment variables to the client
  // Set VITE_API_PORT environment variable before running npm run dev
})



