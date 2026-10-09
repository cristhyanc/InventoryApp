import { Locator, Page, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence issue #409's series rules require for #457: the shared breadcrumb above the
 * page title, captured at 1440px and 390px from the real shell in a real browser.
 *
 * Every route opened below is reached by `MsalGuard` alone — the stubbed e2e sign-in this
 * configuration's dev server build already uses (see `playwright.visual.config.ts`) — and the
 * breadcrumb itself needs no API response: `/admin/historical-gst-classification` and
 * `/products/5/edit` are fully static entries, and `/machines/7` deliberately leaves its own data
 * request unanswered so the screenshot shows the generic "Machine details" fallback label issue
 * #457 asks for before any machine name has loaded. No business data, account name or credential
 * can appear in any of these captures.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-457');
mkdirSync(screenshotDirectory, { recursive: true });

/** `desktop-1440` / `mobile-390` -> `1440px` / `390px`, so a file name states its own width. */
function widthSuffix(projectName: string): string {
  const width = projectName.split('-').pop();
  return `${width}px`;
}

function breadcrumb(page: Page): Locator {
  return page.locator('nav[aria-label="Breadcrumb"]');
}

async function open(page: Page, path: string): Promise<void> {
  await page.goto(path);
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
  });
  await expect(breadcrumb(page)).toBeVisible();
}

async function capture(locator: Locator, name: string): Promise<void> {
  await expect(locator).toBeVisible();
  await locator.screenshot({ path: join(screenshotDirectory, `${name}-${widthSuffix(test.info().project.name)}.png`) });
}

test.describe('breadcrumb visual evidence', () => {
  test('a routed edit page: Products > Edit product', async ({ page }) => {
    await open(page, '/products/5/edit');

    const parentLink = breadcrumb(page).locator('a');
    await expect(parentLink).toHaveText('Products');
    await expect(breadcrumb(page).locator('[aria-current="page"]')).toHaveText('Edit product');

    await capture(breadcrumb(page), 'breadcrumb-edit-product');
  });

  test('a long current-page label: Admin > Historical GST Classification', async ({ page }) => {
    await open(page, '/admin/historical-gst-classification');

    await expect(breadcrumb(page).locator('[aria-current="page"]')).toHaveText('Historical GST Classification');

    await capture(breadcrumb(page), 'breadcrumb-long-label');
  });

  test('the dynamic Machines entry before its live label has loaded', async ({ page }) => {
    await open(page, '/machines/7');

    await expect(breadcrumb(page).locator('[aria-current="page"]')).toHaveText('Machine details');

    await capture(breadcrumb(page), 'breadcrumb-dynamic-fallback');
  });
});
