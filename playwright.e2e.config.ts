import { defineConfig, devices } from '@playwright/test';
import { workerCount } from './scripts/e2e-slots.mjs';

if (!process.env.STAFFORA_E2E_CONNECTION) {
  throw new Error('STAFFORA_E2E_CONNECTION is not set. Point it at a database whose name contains "e2e", such as Staffora.E2E on LocalDB.');
}

const workers = workerCount();

const reportFolder = '.e2e/playwright-report';
const outputFolder = '.e2e/test-results';

export default defineConfig({
  testDir: 'tests/e2e',

  fullyParallel: true,
  workers,
  retries: Number(process.env.STAFFORA_E2E_RETRIES ?? 0),
  maxFailures: Number(process.env.STAFFORA_E2E_MAX_FAILURES ?? 0),
  forbidOnly: !!process.env.CI,
  reportSlowTests: { max: 10, threshold: 15_000 },

  globalTeardown: './tests/e2e/global-teardown.ts',

  reporter: [
    ['list', { printSteps: workers === 1 }],
    ['html', { outputFolder: reportFolder, open: 'never' }],
    ['json', { outputFile: `${reportFolder}/results.json` }],
    ['./tests/e2e/lib/report-secret-guard.ts', { reportFolder, outputFolder }],
  ],
  outputDir: outputFolder,

  snapshotPathTemplate: '{testDir}/baselines/{arg}-{platform}{ext}',
  updateSnapshots: 'none',
  expect: {
    toHaveScreenshot: {
      maxDiffPixels: 200,
      animations: 'disabled',
      caret: 'hide',
      scale: 'css',
    },
  },

  use: {
    trace: { mode: (process.env.STAFFORA_E2E_TRACE ?? 'on-first-retry') as 'on-first-retry', snapshots: false, screenshots: true, sources: true },
    screenshot: 'only-on-failure',
    video: 'off',
  },

  webServer: {
    command: 'node scripts/e2e-server.mjs',
    wait: { stdout: /e2e-server: ready/ },
    env: { STAFFORA_E2E_WORKERS: String(workers) },
    timeout: 600_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },

  projects: [
    { name: 'setup', testMatch: /.*\.setup\.ts/ },
    {
      name: 'chromium',
      testMatch: /.*\.spec\.ts/,
      use: { ...devices['Desktop Chrome'] },
      dependencies: ['setup'],
    },
  ],
});
