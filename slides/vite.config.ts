import { defineConfig } from 'vite';

export default defineConfig({
  server: {
    host: true,
    port: parseInt(process.env.PORT ?? '5173'),
  },
});
