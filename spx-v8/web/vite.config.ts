import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Dev server proxies the API so the browser never needs CORS. Both stay on loopback.
export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    proxy: { '/api': 'http://127.0.0.1:5080', '/health': 'http://127.0.0.1:5080' },
  },
  test: { environment: 'node' },
});
