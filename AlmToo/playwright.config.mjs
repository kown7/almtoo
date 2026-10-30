import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.ALMTOO_BASE_URL ?? 'http://127.0.0.1:5208';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  workers: 1,
  reporter: 'list',
  metadata: {
    gitBugRemoteRepository: process.env.ALMTOO_GITBUG_REMOTE_URL ?? 'https://github.com/kown7/gitbugtest.git'
  },
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
    // A dev server caches the generated framework asset manifest in memory. Reusing an
    // orphaned process after a build can serve stale HTML that references missing
    // dotnet.*.js assets and leave every browser test on the Loading screen. Fresh
    // startup is the safe default; opt in only when the caller owns a compatible
    // server process.
    reuseExistingServer: process.env.PW_REUSE_SERVER === '1',
    timeout: 120_000,
    stdout: 'pipe',
    stderr: 'pipe'
  }
});
