import { Locator, Page, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { openSignedOutLanding } from './support/landing-page';

/**
 * Visual evidence for issue #1272: the approved Restockroo logo in every place the app shows its
 * brand - the landing header and footer, a legal page, the sign-in page, the expanded and collapsed
 * sidebar and the narrow-layout More sheet - at 1440px, 768px, 390px and 360px, and at 125%, 150%, 200% and 400% browser zoom.
 *
 * Like the other visual runs it starts the Angular dev server alone, with no API and no database.
 * The public pages make no request, and the shell is opened on the development-only design-system
 * fixture route, which calls nothing, so no business data, account name or credential is captured.
 *
 * Each capture also checks the things a screenshot alone would hide: every logo image loaded from
 * the packaged `assets/brand/` files, none is stretched (its rendered aspect ratio matches the
 * file's), and the page does not scroll sideways.
 *
 * The widths are set per test rather than per project, so the run uses only the `desktop-1440`
 * project and skips itself on `mobile-390`, which would otherwise capture the same images twice.
 */
const WIDTHS = [1440, 768, 390, 360] as const;

/** Browser zoom levels checked on a 1440 x 900 window (issue #1272 review). */
const ZOOM_LEVELS = [1.25, 1.5, 2, 4] as const;

/** The `lg` breakpoint the shell reads: below it the sidebar is the More sheet, not a rail. */
const WIDE_LAYOUT_WIDTH = 1024;

const FIXTURE_PATH = '/__design-system/widgets';

const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1272');
mkdirSync(screenshotDirectory, { recursive: true });

/** One CSS pixel of slack for fractional layout. */
const tolerance = 1;

async function calmAnimations(page: Page): Promise<void> {
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
  });
}

/** Every logo image inside `scope` is a loaded, packaged asset drawn at its own proportions. */
async function expectLogosIntact(scope: Locator): Promise<void> {
  const logos = scope.locator('img[alt="Restockroo"]');
  expect(await logos.count()).toBeGreaterThan(0);
  await expect(logos.first()).toBeVisible();
  const all = await logos.evaluateAll((images) =>
    (images as HTMLImageElement[]).map((image) => {
      const box = image.getBoundingClientRect();
      return {
        src: image.getAttribute('src'),
        complete: image.complete,
        naturalWidth: image.naturalWidth,
        naturalHeight: image.naturalHeight,
        width: box.width,
        height: box.height
      };
    })
  );
  for (const facts of all) {
    expect(facts.height).toBeGreaterThan(0);
    expect(facts.src).toMatch(/^assets\/brand\/restockroo-(logo|symbol)\.png$/);
    expect(facts.complete).toBe(true);
    expect(facts.naturalWidth).toBeGreaterThan(0);
    const expectedWidth = (facts.height * facts.naturalWidth) / facts.naturalHeight;
    expect(Math.abs(facts.width - expectedWidth)).toBeLessThanOrEqual(tolerance);
  }
}

async function expectNoSidewaysScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(tolerance);
}

async function capture(locator: Locator, name: string, width: number): Promise<void> {
  await expect(locator).toBeVisible();
  await locator.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`) });
}

test.describe('brand logo visual evidence (issue #1272)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Widths are set per test; one project is enough.');
  });

  for (const width of WIDTHS) {
    test(`public pages at ${width}px`, async ({ page }) => {
      await openSignedOutLanding(page, width);
      const header = page.locator('app-landing-header header');
      await expectLogosIntact(header);
      await expectNoSidewaysScroll(page);
      await capture(header, 'landing-header', width);

      const footerBrand = page.locator('[data-testid="landing-footer-brand"]');
      await footerBrand.scrollIntoViewIfNeeded();
      await expectLogosIntact(footerBrand);
      await capture(footerBrand, 'landing-footer-brand', width);

      await page.goto('/privacy-policy');
      await calmAnimations(page);
      const legalHeader = page.locator('header').first();
      await expectLogosIntact(legalHeader);
      await expectNoSidewaysScroll(page);
      await capture(legalHeader, 'legal-header', width);

      await page.goto('/sign-in');
      await calmAnimations(page);
      const signInHeader = page.locator('header').first();
      await expectLogosIntact(signInHeader);
      await expectNoSidewaysScroll(page);
      await capture(signInHeader, 'sign-in-header', width);
    });

    test(`app navigation at ${width}px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(FIXTURE_PATH);
      await calmAnimations(page);
      const navigation = page.locator('nav[aria-label="Primary"]');

      if (width >= WIDE_LAYOUT_WIDTH) {
        await expectLogosIntact(navigation);
        await capture(navigation, 'sidebar-expanded', width);

        await navigation.getByRole('button', { name: 'Collapse navigation' }).click();
        await expect(navigation.getByRole('button', { name: 'Expand navigation' })).toBeVisible();
        await expectLogosIntact(navigation);
        await capture(navigation, 'sidebar-collapsed', width);
      } else {
        // The top bar keeps only its opener and the user control; it carries no logo.
        await capture(page.locator('main > header').first(), 'app-top-bar', width);
        await page.getByRole('button', { name: 'Open navigation menu' }).click();
        await expect(navigation).toBeVisible();
        const sheetHeader = navigation.locator('[data-testid="sidebar-brand"]').locator('..');
        await expectLogosIntact(sheetHeader);
        await capture(sheetHeader, 'more-sheet-header', width);
      }
      await expectNoSidewaysScroll(page);
    });
  }

  /**
   * Browser zoom on a 1440 x 900 window. Zooming to N% divides the CSS viewport by N/100 and
   * multiplies the device pixel ratio by the same factor, which is what a new context with that
   * viewport and `deviceScaleFactor` reproduces. From 150% the CSS width falls below the shell's
   * 1024px breakpoint, so the app shows the More sheet exactly as Chrome would.
   */
  for (const zoom of ZOOM_LEVELS) {
    test(`public header and app navigation at ${zoom * 100}% zoom`, async ({ browser }) => {
      const width = Math.round(1440 / zoom);
      const height = Math.round(900 / zoom);
      const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: zoom });
      const page = await context.newPage();
      const label = `zoom-${zoom * 100}pct`;
      try {
        await openSignedOutLanding(page, width, height);
        const header = page.locator('app-landing-header header');
        await expectLogosIntact(header);
        await expectNoSidewaysScroll(page);
        await header.screenshot({ path: join(screenshotDirectory, `landing-header-${label}.png`) });

        await page.goto(FIXTURE_PATH);
        await calmAnimations(page);
        const navigation = page.locator('nav[aria-label="Primary"]');
        if (width >= WIDE_LAYOUT_WIDTH) {
          await expectLogosIntact(navigation);
          await navigation.screenshot({ path: join(screenshotDirectory, `sidebar-expanded-${label}.png`) });
        } else {
          await page.getByRole('button', { name: 'Open navigation menu' }).click();
          const sheetHeader = navigation.locator('[data-testid="sidebar-brand"]').locator('..');
          await expectLogosIntact(sheetHeader);
          await sheetHeader.screenshot({ path: join(screenshotDirectory, `more-sheet-header-${label}.png`) });
        }
        await expectNoSidewaysScroll(page);
      } finally {
        await context.close();
      }
    });
  }
});
