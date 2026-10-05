import { defineConfig } from '@playwright/test';
import { mkdirSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { API_BASE_URL, API_PORT, APP_BASE_URL } from './fixtures/harness';

/**
 * The end-to-end harness (issue #46): one dedicated API process, one Angular dev server, and one
 * disposable SQLite database, all started by Playwright and all torn down with it.
 *
 * Isolation and determinism come from four decisions:
 *
 *  1. **A fresh database per run.** The artefacts directory is deleted before the API starts, so
 *     every run begins from an empty schema that startup migrates and the E2E fixture seeds. No
 *     run can see a previous run's rows, and nothing here can touch a developer's own
 *     `inventory.db`.
 *  2. **The dedicated E2E environment.** `ASPNETCORE_ENVIRONMENT=E2ETest` is what makes the
 *     synthetic authentication scheme and the fixture exist at all
 *     (`backend/InventoryApi/Auth/E2ETesting`). No other environment can be used to run this
 *     suite, and this environment is never deployed.
 *  3. **No live external service.** The Nayax base URL is pointed at a closed local port, so a
 *     test that ever reached the Nayax integration would fail immediately instead of calling the
 *     real operator account. Nothing in the suite triggers a sync or an import.
 *  4. **One worker.** The whole suite shares one API process and one SQLite file, and SQLite is a
 *     single-writer store, so the tests run serially. Each test also works on its own seeded
 *     product, so a failure in one does not change what another asserts.
 */
const e2eDirectory = __dirname;
const frontendDirectory = resolve(e2eDirectory, '..');
const repositoryRoot = resolve(frontendDirectory, '..', '..');
const artifactsDirectory = join(e2eDirectory, '.artifacts');
const databaseFile = join(artifactsDirectory, 'inventory-e2e.db');

// Only the process that starts the run may clear the artefacts. Playwright re-imports this config
// in every worker process, and a worker clearing it would delete the database file out from under
// the running API - which SQLite answers by silently creating a new, empty one.
if (process.env['TEST_WORKER_INDEX'] === undefined) {
  rmSync(artifactsDirectory, { recursive: true, force: true });
}

mkdirSync(artifactsDirectory, { recursive: true });

export default defineConfig({
  testDir: './tests',
  testMatch: /.*\.e2e\.ts/,
  outputDir: join(artifactsDirectory, 'test-results'),
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env['CI'],
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: process.env['CI'] ? [['list'], ['github']] : [['list']],
  use: {
    baseURL: APP_BASE_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off'
  },
  webServer: [
    {
      // --no-launch-profile: launchSettings.json would otherwise force Development and port 5000.
      command: `dotnet run --no-launch-profile --project ${join(repositoryRoot, 'backend', 'InventoryApi', 'InventoryApi.csproj')}`,
      cwd: repositoryRoot,
      env: {
        ASPNETCORE_ENVIRONMENT: 'E2ETest',
        ASPNETCORE_URLS: `http://127.0.0.1:${API_PORT}`,
        ConnectionStrings__DefaultConnection: `Data Source=${databaseFile}`,
        // The documented opt-in for a disposable database outside Development/Testing/Production;
        // see backend DatabaseSchemaStartup and AGENTS.md § Database and migrations.
        Database__AllowAutomaticMigrationUnsafeOutsideDevelopment: 'true',
        // A closed local port: no E2E run may ever reach the live Nayax operator account.
        NayaxLynx__BaseUrl: 'https://127.0.0.1:9',
        NayaxLynx__AccessToken: '',
        DOTNET_ENVIRONMENT: 'E2ETest'
      },
      // /health/ready runs the database connectivity check, and the web host only starts
      // listening after migrations and the fixture seed have completed.
      url: `${API_BASE_URL}/health/ready`,
      reuseExistingServer: false,
      timeout: 300_000,
      stdout: 'pipe',
      stderr: 'pipe'
    },
    {
      command: 'npm run start:e2e',
      cwd: frontendDirectory,
      url: APP_BASE_URL,
      reuseExistingServer: false,
      timeout: 300_000,
      stdout: 'pipe',
      stderr: 'pipe'
    }
  ]
});
