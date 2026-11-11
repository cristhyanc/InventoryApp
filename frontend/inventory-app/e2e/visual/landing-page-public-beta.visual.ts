import { Page, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The browser evidence issue #1191 asks for: the **signed-out** public homepage at 1440px and
 * 390px, and the measurement behind its last acceptance criterion - that the page has no
 * page-level horizontal scrolling anywhere from 320px to 390px wide.
 *
 * jsdom computes no layout, so the component specs can prove every sentence of the copy and none
 * of this: whether the page scrolls sideways at a phone width is a question only a real engine
 * answers.
 *
 * Like the other visual runs it starts the Angular dev server alone - no API process, no database,
 * no Nayax call and no sign-in - and that is enough here because the landing page injects no API
 * service and makes no request at all. Nothing on these captures comes from a business: the page is
 * static marketing copy with an example dashboard of invented figures, and no account name,
 * credential or imported document can appear on it.
 *
 * **Signed out is the whole point**, so the run asks the E2E build's auth stub for it: the stub
 * reports the synthetic signed-in account by default (which is what the workflow suite needs), and
 * `e2e-signed-out` in `sessionStorage`, set before the bundle loads, makes it report no account -
 * exactly the state `signedOutVisitorGuard` matches `/` on, and the state in which the page offers
 * Sign in and Start free instead of Open app.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1191');
mkdirSync(screenshotDirectory, { recursive: true });

/**
 * Keep in step with `E2E_SIGNED_OUT_KEY` in
 * `frontend/inventory-app/src/app/auth/browser-auth-providers.e2e.ts`, which
 * `browser-auth-providers.spec.ts` pins to this exact string in the Jest suite.
 */
const SIGNED_OUT_KEY = 'e2e-signed-out';

/** The two widths the issue names for its screenshots. */
const SCREENSHOT_WIDTHS = [1440, 390];

/** The range the issue's no-horizontal-scrolling criterion covers, every width of it. */
const NARROW_WIDTHS = Array.from({ length: 390 - 320 + 1 }, (_, index) => 320 + index);

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

/** Opens the homepage as a visitor nobody has signed in as, at one width. */
async function openSignedOut(page: Page, width: number): Promise<void> {
  await page.addInitScript((key: string) => sessionStorage.setItem(key, 'true'), SIGNED_OUT_KEY);
  await page.setViewportSize({ width, height: 900 });
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

/** The page-level measurement the acceptance criterion is about: can the document scroll sideways? */
async function expectNoHorizontalOverflow(page: Page, width: number): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `no page-level horizontal overflow at ${width}px`).toBeLessThanOrEqual(tolerance);
}

test.describe('public beta homepage visual evidence (issue #1191)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of SCREENSHOT_WIDTHS) {
    test(`the signed-out homepage at ${width}px`, async ({ page }) => {
      await openSignedOut(page, width);

      // The sections the issue rewrote, so one capture per width is the evidence for all of them.
      await expect(page.locator('[data-testid="landing-beta-notice"]')).toHaveText(
        'Pro access is free during our public beta. No credit card required.'
      );
      await expect(page.locator('[data-testid="landing-export-notice"]')).toBeVisible();
      await expect(page.locator('[data-testid="landing-footer-contact"]')).toBeVisible();
      await expect(page.locator('footer a[href="#pricing"]')).toHaveCount(0);

      await expectNoHorizontalOverflow(page, width);

      // The whole page rather than one viewport: the hero notices, the reworded Nayax section, the
      // three steps and the new footer line are the subject, and they are spread down the page.
      // Growing the viewport is the idiom the other visual runs use instead of `fullPage`, which
      // paints a sticky header over the middle of the capture.
      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await expectNoHorizontalOverflow(page, width);

      await page.screenshot({ path: join(screenshotDirectory, `homepage-signed-out-${width}px.png`) });
    });
  }

  /**
   * The criterion itself, measured at every width in the range rather than at its two ends: a
   * layout breaks at the width where one element stops fitting, which is not usually a round
   * number.
   */
  test('never scrolls sideways at any width from 320px to 390px', async ({ page }) => {
    await openSignedOut(page, NARROW_WIDTHS[0]);

    for (const width of NARROW_WIDTHS) {
      await page.setViewportSize({ width, height: 844 });
      await expectNoHorizontalOverflow(page, width);
    }
  });

  /** The other half of the same criterion: the viewport meta tag still allows pinch zoom. */
  test('keeps a viewport meta tag that allows pinch zoom', async ({ page }) => {
    await openSignedOut(page, 390);

    const viewport = await page.locator('meta[name="viewport"]').getAttribute('content');

    expect(viewport).toBe('width=device-width, initial-scale=1');
    expect(viewport).not.toMatch(/maximum-scale|user-scalable/);
  });
});
