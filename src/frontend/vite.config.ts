import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: parseInt(process.env.PORT ?? '5173'),
    host: true,
    proxy: {
      '/api': {
        target: process.env.DATAGENERATOR_HTTPS || process.env.DATAGENERATOR_HTTP,
        changeOrigin: true,
        secure: false,
      },
      '/responses': {
        target: process.env.ADVISORAGENT_HTTPS || process.env.ADVISORAGENT_HTTP,
        changeOrigin: true,
        secure: false,
      },
      '/ws/voice': {
        target: process.env.VOICEADVISORAGENT_HTTPS || process.env.VOICEADVISORAGENT_HTTP,
        changeOrigin: true,
        secure: false,
        ws: true,
      },
    },
  },
})
