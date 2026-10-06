import { defineConfig } from '@playwright/test';
import { mkdirSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { APP_BASE_URL } from './fixtures/harness';

/**
 * The visual-evidence run (issue #411, series rule of #409): it screenshots the shared widgets and
 * the bundled icon set at 1440px and 390px so a pull request can show what a restyle actually
 * looks like.
 *
 * It is deliberately a second, smaller configuration rather than extra projects on
 * `playwright.config.ts`:
 *
 *  1. **No API and no database.** The design-system fixture route renders components only and
 *     calls nothing, so this run starts the Angular dev server alone. There is no SQLite file to
 *     dispose of and nothing that could reach a real service.
 *  2. **Its output is committed evidence, not a test artefact.** The screenshots are written
 *     straight to `docs/screenshots/issue-411/`, which is version-controlled, while Playwright's
 *     own traces and failure output stay in the git-ignored artefacts directory.
 *
 * Run it with `npm run e2e:visual` from `frontend/inventory-app` (after `npm run e2e:install`).
 */
const e2eDirectory = __dirname;
const frontendDirectory = resolve(e2eDirectory, '..');
const repositoryRoot = resolve(frontendDirectory, '..', '..');
const artifactsDirectory = join(e2eDirectory, '.artifacts', 'visual');

/** Version-controlled: these files are the pull request's visual evidence. */
export const screenshotDirectory = join(repositoryRoot, 'docs', 'screenshots', 'issue-411');

if (process.env['TEST_WORKER_INDEX'] === undefined) {
  rmSync(artifactsDirectory, { recursive: true, force: true });
}

mkdirSync(artifactsDirectory, { recursive: true });
mkdirSync(screenshotDirectory, { recursive: true });

export default defineConfig({
  testDir: './visual',
  testMatch: /\.visual\.ts$/,
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
    screenshot: 'off',
    video: 'off',
    // One device pixel per CSS pixel: the evidence has to stay small enough to live in the
    // repository, and the geometry of an icon is just as checkable at 1x.
    deviceScaleFactor: 1
  },
  projects: [
    { name: 'desktop-1440', use: { viewport: { width: 1440, height: 900 } } },
    { name: 'mobile-390', use: { viewport: { width: 390, height: 844 } } }
  ],
  webServer: [
    {
      // The same unoptimized `e2e` configuration the workflow suite serves: it stubs interactive
      // sign-in, and it is the build in which the design-system fixture route exists at all.
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
