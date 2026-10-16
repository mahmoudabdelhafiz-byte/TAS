import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev` serves the portal on :5173 and forwards /api to the API (dotnet run, :5000 by default).
// `npm run build` writes into the API's wwwroot so one process serves both.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': process.env.TAS_API_URL ?? 'http://localhost:5000' },
  },
  build: {
    outDir: '../src/Yardsyncs.Api/wwwroot',
    emptyOutDir: true,
  },
});
