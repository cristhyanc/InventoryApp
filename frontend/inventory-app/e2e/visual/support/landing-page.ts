import { Page, expect } from '@playwright/test';

/**
 * The shared **signed-out** setup and page-level overflow measurement the landing-page visual runs
 * use (issue #1191's public beta refresh and issue #1192's refill and supplier comparison
 * examples). It is not a spec itself - the visual config only runs files ending in `.visual.ts` - so
 * importing it registers no tests.
 *
 * jsdom computes no layout, so the component specs can prove every sentence of the copy and none of
 * this: whether the page scrolls sideways at a phone width is a question only a real engine answers.
 *
 * Like the other visual runs it starts the Angular dev server alone - no API process, no database,
 * no Nayax call and no sign-in - and that is enough here because the landing page injects no API
 * service and makes no request at all. Nothing on these captures comes from a business: the page is
 * static marketing copy with clearly labelled examples of invented figures, and no account name,
 * credential or imported document can appear on it.
 *
 * **Signed out is the whole point**, so the run asks the E2E build's auth stub for it: the stub
 * reports the synthetic signed-in account by default (which is what the workflow suite needs), and
 * `e2e-signed-out` in `sessionStorage`, set before the bundle loads, makes it report no account -
 * exactly the state `signedOutVisitorGuard` matches `/` on, and the state in which the page offers
 * Sign in and Start free instead of Open app.
 */

/**
 * Keep in step with `E2E_SIGNED_OUT_KEY` in
 * `frontend/inventory-app/src/app/auth/browser-auth-providers.e2e.ts`, which
 * `browser-auth-providers.spec.ts` pins to this exact string in the Jest suite.
 */
const SIGNED_OUT_KEY = 'e2e-signed-out';

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

/** Opens the homepage as a visitor nobody has signed in as, at one width. */
export async function openSignedOutLanding(page: Page, width: number, height = 900): Promise<void> {
  await page.addInitScript((key: string) => sessionStorage.setItem(key, 'true'), SIGNED_OUT_KEY);
  await page.setViewportSize({ width, height });
  await page.goto('/');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
  });

  // The signed-out landing page, and not the authenticated shell: `/` only matches it while nobody
  // is signed in, and only then does it offer Start free rather than Open app.
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Know what to buy. Pack what you need.');
  await expect(page.locator('[data-testid="landing-primary-get-started"]').first()).toHaveText(/Start free/);
  await expect(page.locator('[data-testid="landing-open-app"]')).toHaveCount(0);
}

/**
 * The page-level measurement the acceptance criterion is about: can the document scroll sideways?
 * `where` names the state being measured, so a failure says which one broke.
 */
export async function expectNoHorizontalOverflow(page: Page, where: string): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `no page-level horizontal overflow ${where}`).toBeLessThanOrEqual(tolerance);
}

/** The other half of the same criterion: the viewport meta tag still allows pinch zoom. */
export async function expectPinchZoomAllowed(page: Page): Promise<void> {
  const viewport = await page.locator('meta[name="viewport"]').getAttribute('content');

  expect(viewport).toBe('width=device-width, initial-scale=1');
  expect(viewport).not.toMatch(/maximum-scale|user-scalable/);
}
