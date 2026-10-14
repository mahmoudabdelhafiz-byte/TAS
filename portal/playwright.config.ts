import { defineConfig, devices } from '@playwright/test';

// Runs against a real API that serves the built portal (CI starts it; see .github/workflows/ci.yml).
export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  timeout: 60_000,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.TAS_PORTAL_URL ?? 'http://localhost:5000',
    trace: 'retain-on-failure',
    actionTimeout: 15_000, // a stuck step fails on its own line instead of the whole test timing out
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1360, height: 860 } } },
    { name: 'phone', use: { ...devices['Pixel 7'] } },
  ],
});
