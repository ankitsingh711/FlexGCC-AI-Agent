import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// In dev, proxy /api to the ASP.NET Core backend so the browser talks to one origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
  build: {
    outDir: '../backend/src/CodeReviewAgent.Api/wwwroot',
    emptyOutDir: true,
  },
})
