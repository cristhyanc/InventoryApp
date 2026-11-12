import { Locator, Page, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { expectNoHorizontalOverflow, expectPinchZoomAllowed, openSignedOutLanding } from './support/landing-page';

/**
 * The browser evidence issue #1192 asks for: the **signed-out** homepage with its refill and
 * supplier comparison examples at 1440px and 390px, the open mobile comparison dialog at 390px, and
 * the parts of the issue's last acceptance criterion that only a real engine can answer -
 *
 *  - the page has **no page-level horizontal scrolling** at 390px with the dialog closed *and* with
 *    it open (a modal in the top layer is laid out against the initial containing block, so it can
 *    widen the document even though the page behind it is inert);
 *  - **pinch zoom stays enabled**: nothing on this page touches the viewport meta tag;
 *  - the dialog can be **opened from the keyboard**, dismissed with **Escape** and with its
 *    **Close** button, and each dismissal returns focus to **View full comparison**.
 *
 * jsdom implements `<dialog>`'s open/close state but none of the platform's focus trap or any
 * layout (see `setup-jest.ts`), so `landing-supplier-comparison-example.component.spec.ts` pins the
 * component's own Tab cycle and this run pins what the browser itself does.
 *
 * This is deliberately its own spec rather than another block in
 * `landing-page-public-beta.visual.ts`. The `e2e:visual` npm script chain forwards a positional
 * path but swallows Playwright's own flags, so `--grep` cannot narrow a shared file: running one
 * issue's cases there re-renders every other issue's committed captures, leaves them modified in
 * the working tree, and the repair workflow then refuses to publish. A file of its own is the unit
 * the script can actually select, and it writes only to `docs/screenshots/issue-1192/`.
 *
 * The signed-out setup it shares with issue #1191's run lives in `support/landing-page.ts`, which
 * explains why the run needs no API, no database and no sign-in: the landing page injects no API
 * service and makes no request at all, and both examples are static data in `landing-content.ts`.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1192');
mkdirSync(screenshotDirectory, { recursive: true });

/** The two widths the issue names for its screenshots. */
const SCREENSHOT_WIDTHS = [1440, 390];

/** Below `md` the comparison is a preview plus this button; from `md` up the table is inline. */
const MOBILE_WIDTH = 390;

/** Tailwind's `md`, the breakpoint the component's own classes split on. */
const MD_BREAKPOINT = 768;

/** This spec sets every width itself, so it runs once rather than once per configured project. */
const PRIMARY_PROJECT = 'desktop-1440';

const VIEW_FULL_COMPARISON = 'View full comparison';
const EXAMPLE_NOTICE = 'Example comparison — prices and availability change.';
const CONFIRM_NOTICE = 'Confirm the product, pack size and final price with the supplier before buying.';
const LOWEST_UNIT_PRICE = 'Lowest unit price';

/** The issue's table: seven offers, of which the three 30-pack rows tie for the lowest unit price. */
const OFFER_COUNT = 7;
const TIED_FOR_LOWEST = 3;

function trigger(page: Page): Locator {
  return page.locator('[data-testid="landing-supplier-view-full-comparison"]');
}

function dialog(page: Page): Locator {
  return page.locator('[data-testid="landing-supplier-comparison-dialog"]');
}

/** Both examples are rendered and labelled as examples, whatever the width. */
async function expectBothExamples(page: Page): Promise<void> {
  const refill = page.locator('[data-testid="landing-refill-example"]');
  await expect(refill).toContainText('Banksia House · Banksia House VM');
  await expect(refill).toContainText('Example refill · Fictional data');
  await expect(refill.locator('[data-testid="landing-refill-row"]')).toHaveCount(4);
  await expect(page.locator('[data-testid="landing-refill-loaded-note"]')).toBeVisible();

  const comparison = page.locator('[data-testid="landing-supplier-comparison"]');
  await expect(comparison.getByRole('heading', { name: 'Compare prices across supported suppliers' })).toBeVisible();
  await expect(page.locator('[data-testid="landing-supplier-example-notice"]')).toHaveText(EXAMPLE_NOTICE);
  await expect(page.locator('[data-testid="landing-supplier-confirm-notice"]')).toContainText(CONFIRM_NOTICE);
}

function capture(page: Page, name: string): Promise<Buffer> {
  return page.screenshot({ path: join(screenshotDirectory, `${name}.png`) });
}

test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== PRIMARY_PROJECT, 'This spec sets each width itself, so it runs once.');
});

test.describe('Landing refill and supplier comparison examples (issue #1192)', () => {
  for (const width of SCREENSHOT_WIDTHS) {
    test(`1 the signed-out homepage shows both examples at ${width}px and never scrolls sideways`, async ({ page }) => {
      await openSignedOutLanding(page, width, width >= MD_BREAKPOINT ? 900 : 844);
      await expectBothExamples(page);

      // The dialog element is always in the DOM, so what the browser must do with it before it is
      // opened is the thing to pin: a closed `<dialog>` is `display: none` and is painted nowhere.
      // It regressed once - a bare Tailwind `flex` on the element is an author declaration and beat
      // the user-agent's `dialog:not([open]) { display: none }`, leaving the comparison painted over
      // the page and its Close controls in the accessibility tree at every width, which jsdom
      // cannot see because it applies no user-agent stylesheet and computes no layout.
      const closedDialog = dialog(page);
      await expect(closedDialog).toBeHidden();
      await expect(closedDialog).not.toHaveAttribute('open', /.*/);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Close supplier comparison' })).toHaveCount(0);

      if (width >= MD_BREAKPOINT) {
        // From `md` up the full table is inline and the dialog is not offered at all.
        const inline = page.locator('[data-testid="landing-supplier-comparison-inline"]');
        await expect(inline).toBeVisible();
        await expect(page.locator('[data-testid="landing-supplier-preview"]')).toBeHidden();
        await expect(inline.locator('[data-testid="landing-supplier-row"]')).toHaveCount(OFFER_COUNT);
        await expect(inline.getByText(LOWEST_UNIT_PRICE, { exact: true })).toHaveCount(TIED_FOR_LOWEST);
      } else {
        // Below `md` a readable preview stands in for the table: the lowest unit price and how many
        // suppliers tie for it, plus the button that opens the full comparison.
        const preview = page.locator('[data-testid="landing-supplier-preview"]');
        await expect(preview).toContainText('From $1.07 inc GST / unit');
        await expect(preview).toContainText(`${TIED_FOR_LOWEST} of ${OFFER_COUNT} suppliers tie for the lowest unit price`);
        await expect(trigger(page)).toHaveText(VIEW_FULL_COMPARISON);
        await expect(page.locator('[data-testid="landing-supplier-comparison-inline"]')).toBeHidden();
      }

      await expectNoHorizontalOverflow(page, `at ${width}px with the dialog closed`);

      // The whole page rather than one viewport: growing the viewport is the idiom the other visual
      // runs use instead of `fullPage`, which paints the sticky header over the middle of a capture.
      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await expectNoHorizontalOverflow(page, `at ${width}px with the whole page in view`);

      await capture(page, `homepage-examples-signed-out-${width}px`);
    });
  }

  test(`2 the comparison dialog opens from the keyboard at ${MOBILE_WIDTH}px without scrolling the page sideways`, async ({ page }) => {
    await openSignedOutLanding(page, MOBILE_WIDTH, 844);

    // Pinch zoom, measured in the same browser as the overflow: the page sets no maximum-scale and
    // no user-scalable, with or without the dialog.
    await expectPinchZoomAllowed(page);
    await expectNoHorizontalOverflow(page, `at ${MOBILE_WIDTH}px before the dialog is opened`);

    // Opened from the keyboard alone: focus the button and press Enter, no pointer involved.
    const button = trigger(page);
    await button.scrollIntoViewIfNeeded();
    await button.focus();
    await expect(button).toBeFocused();
    await page.keyboard.press('Enter');

    const panel = dialog(page);
    await expect(panel).toBeVisible();
    // `showModal()` put it in the top layer, so the browser maps it to the dialog role itself and
    // the component focuses the panel: the keyboard is inside the dialog, not on the page behind it.
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(panel).toBeFocused();
    await expect(panel.getByRole('heading', { name: 'Compare prices across supported suppliers' })).toBeVisible();
    await expect(panel.locator('[data-testid="landing-supplier-row"]')).toHaveCount(OFFER_COUNT);
    await expect(panel.getByText(LOWEST_UNIT_PRICE, { exact: true })).toHaveCount(TIED_FOR_LOWEST);
    await expect(panel).toContainText(EXAMPLE_NOTICE);
    await expect(panel).toContainText(CONFIRM_NOTICE);

    // The measurement the criterion is about, in the state the issue names: the open modal is laid
    // out against the initial containing block, and the document still does not scroll sideways.
    await expectNoHorizontalOverflow(page, `at ${MOBILE_WIDTH}px with the dialog open`);
    // The dialog's own panel stays inside the viewport; only its table scrolls, and only inside it.
    const panelBox = await panel.evaluate((element) => {
      const box = element.getBoundingClientRect();
      return { left: box.left, right: box.right, viewport: window.innerWidth };
    });
    expect(panelBox.left).toBeGreaterThanOrEqual(0);
    expect(panelBox.right).toBeLessThanOrEqual(panelBox.viewport);
    await expectPinchZoomAllowed(page);

    await capture(page, `supplier-comparison-dialog-${MOBILE_WIDTH}px`);
  });

  test(`3 Escape closes the dialog and returns focus to ${VIEW_FULL_COMPARISON} at ${MOBILE_WIDTH}px`, async ({ page }) => {
    await openSignedOutLanding(page, MOBILE_WIDTH, 844);
    const button = trigger(page);
    await button.scrollIntoViewIfNeeded();
    await button.focus();
    await page.keyboard.press('Enter');
    await expect(dialog(page)).toBeVisible();

    await page.keyboard.press('Escape');

    await expect(dialog(page)).toBeHidden();
    await expect(button).toBeFocused();
    await expectNoHorizontalOverflow(page, `at ${MOBILE_WIDTH}px after Escape`);
  });

  test(`4 the Close button closes the dialog from the keyboard and returns focus to ${VIEW_FULL_COMPARISON}`, async ({ page }) => {
    await openSignedOutLanding(page, MOBILE_WIDTH, 844);
    const button = trigger(page);
    await button.scrollIntoViewIfNeeded();
    await button.focus();
    await page.keyboard.press('Enter');

    const panel = dialog(page);
    await expect(panel).toBeVisible();

    // Tab stays inside the dialog - its two Close controls, in order - and never reaches the page
    // behind it, which `showModal()` made inert.
    const dismiss = panel.getByRole('button', { name: 'Close', exact: true });
    await page.keyboard.press('Tab');
    await expect(panel.getByRole('button', { name: 'Close supplier comparison' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(dismiss).toBeFocused();

    await page.keyboard.press('Enter');

    await expect(panel).toBeHidden();
    await expect(button).toBeFocused();
    await expectNoHorizontalOverflow(page, `at ${MOBILE_WIDTH}px after Close`);
  });
});
