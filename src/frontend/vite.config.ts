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
        target: process.env.DATA_GENERATOR_HTTPS || process.env.DATA_GENERATOR_HTTP,
        changeOrigin: true,
        secure: false,
      },
      '/responses': {
        target: process.env.ADVISOR_AGENT_DOTNET_HTTPS || process.env.ADVISOR_AGENT_DOTNET_HTTP,
        changeOrigin: true,
        secure: false,
      },
      '/ws/voice': {
        target: process.env.VOICE_ADVISOR_AGENT_HTTPS || process.env.VOICE_ADVISOR_AGENT_HTTP,
        changeOrigin: true,
        secure: false,
        ws: true,
      },
    },
  },
})
