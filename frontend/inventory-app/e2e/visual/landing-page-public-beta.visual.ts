import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { expectNoHorizontalOverflow, expectPinchZoomAllowed, openSignedOutLanding } from './support/landing-page';

/**
 * The browser evidence issue #1191 asks for: the **signed-out** public homepage at 1440px and
 * 390px, and the measurement behind its last acceptance criterion - that the page has no
 * page-level horizontal scrolling anywhere from 320px to 390px wide.
 *
 * The signed-out setup, the overflow measurement and the pinch-zoom check live in
 * `support/landing-page.ts`, shared with issue #1192's own run
 * (`landing-refill-supplier-examples.visual.ts`), and that module explains why this run needs no
 * API, no database and no sign-in.
 *
 * These captures are evidence of **this** issue's head: they show the page as the public beta
 * refresh left it, with the example dashboard issue #1192 later replaced. The current examples are
 * captured by issue #1192's run instead; see `docs/screenshots/issue-1191/README.md`.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1191');
mkdirSync(screenshotDirectory, { recursive: true });

/** The two widths the issue names for its screenshots. */
const SCREENSHOT_WIDTHS = [1440, 390];

/** The range the issue's no-horizontal-scrolling criterion covers, every width of it. */
const NARROW_WIDTHS = Array.from({ length: 390 - 320 + 1 }, (_, index) => 320 + index);

test.describe('public beta homepage visual evidence (issue #1191)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of SCREENSHOT_WIDTHS) {
    test(`the signed-out homepage at ${width}px`, async ({ page }) => {
      await openSignedOutLanding(page, width);

      // The sections the issue rewrote, so one capture per width is the evidence for all of them.
      await expect(page.locator('[data-testid="landing-beta-notice"]')).toHaveText(
        'Pro access is free during our public beta. No credit card required.'
      );
      await expect(page.locator('[data-testid="landing-export-notice"]')).toBeVisible();
      await expect(page.locator('[data-testid="landing-footer-contact"]')).toBeVisible();
      await expect(page.locator('footer a[href="#pricing"]')).toHaveCount(0);

      await expectNoHorizontalOverflow(page, `at ${width}px`);

      // The whole page rather than one viewport: the hero notices, the reworded Nayax section, the
      // three steps and the new footer line are the subject, and they are spread down the page.
      // Growing the viewport is the idiom the other visual runs use instead of `fullPage`, which
      // paints a sticky header over the middle of the capture.
      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await expectNoHorizontalOverflow(page, `at ${width}px with the whole page in view`);

      await page.screenshot({ path: join(screenshotDirectory, `homepage-signed-out-${width}px.png`) });
    });
  }

  /**
   * The criterion itself, measured at every width in the range rather than at its two ends: a
   * layout breaks at the width where one element stops fitting, which is not usually a round
   * number.
   */
  test('never scrolls sideways at any width from 320px to 390px', async ({ page }) => {
    await openSignedOutLanding(page, NARROW_WIDTHS[0], 844);

    for (const width of NARROW_WIDTHS) {
      await page.setViewportSize({ width, height: 844 });
      await expectNoHorizontalOverflow(page, `at ${width}px`);
    }
  });

  /** The other half of the same criterion: the viewport meta tag still allows pinch zoom. */
  test('keeps a viewport meta tag that allows pinch zoom', async ({ page }) => {
    await openSignedOutLanding(page, 390, 844);

    await expectPinchZoomAllowed(page);
  });
});
