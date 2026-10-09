import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.ALMTOO_BASE_URL ?? 'http://127.0.0.1:5208';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  workers: 1,
  reporter: 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off'
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ],
  webServer: {
    command: 'dotnet run --project AlmToo.csproj --urls http://127.0.0.1:5208',
    url: baseURL,
    // Reuse a manually started local server during development; CI always starts a fresh one.
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    stdout: 'pipe',
    stderr: 'pipe'
  }
});
