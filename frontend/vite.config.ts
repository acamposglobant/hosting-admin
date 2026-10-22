import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Dev server proxies /api to the local ASP.NET Core API (launchSettings http
// profile: http://localhost:5163), so the SPA is same-origin in development
// and cookies flow without CORS. Production serves the SPA behind the same
// origin as the API (plan §3.2: loopback binding + reverse proxy).
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5163',
        changeOrigin: false,
      },
    },
  },
});
