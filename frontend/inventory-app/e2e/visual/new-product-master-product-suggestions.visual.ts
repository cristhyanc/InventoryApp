import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence for issue #1052: the New Product Name field suggesting the shared master
 * product catalogue, at 1440px and 390px, with two size variants of one product in the list and
 * with one of them taken.
 *
 * This is the half of the issue's "browser check at desktop and 390 px with two size variants of
 * one product" that a person has to look at. The assertions themselves live in
 * `e2e/tests/new-product-master-product-suggestions.e2e.ts`, which drives the same field against
 * the real API in the same browser; these two widths are what the suggestion panel, the labelled
 * size rows and the tracking note actually look like.
 *
 * Like the other visual runs it starts no API and no database. Every `/api/` request is answered in
 * the browser with neutral synthetic data, so no business data, account name or credential can
 * appear in a capture, and nothing is ever saved: the page is never submitted, so no product is
 * created and no master product is linked. Each capture also asserts the page has no horizontal
 * overflow at its width. It runs once, in the `desktop-1440` project, and sets each width itself.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1052');
mkdirSync(screenshotDirectory, { recursive: true });

const OWNER_CAPABILITIES = [
  'Dashboard.View',
  'Dashboard.ViewFinancials',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate',
  'Suppliers.ManageGstDefaults',
  'Expenses.Manage',
  'Reports.View',
  'Imports.Run',
  'SiteCommissions.Manage',
  'Integration.Manage',
  'Data.Repair',
  'Members.Manage',
  'Roles.View',
  'Business.Manage'
];

/**
 * Two size variants of one catalogue product, which is the case the issue names: brand and name are
 * identical on both rows and only the size tells them apart, so the capture shows whether the list
 * is actually usable for it.
 */
const variants = [
  {
    id: 9101,
    brand: 'Sample Brand',
    name: 'Tasty Cheese Corn Chips',
    sizeValue: 45,
    sizeUnit: 'g',
    productName: 'Sample Brand Tasty Cheese Corn Chips',
    unitLabel: '45g'
  },
  {
    id: 9102,
    brand: 'Sample Brand',
    name: 'Tasty Cheese Corn Chips',
    sizeValue: 175,
    sizeUnit: 'g',
    productName: 'Sample Brand Tasty Cheese Corn Chips',
    unitLabel: '175g'
  }
];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function answerApi(page: Page): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/me/access')) {
      return json(route, {
        role: 'Owner',
        capabilities: OWNER_CAPABILITIES,
        businessName: 'Sample Vending',
        timeZoneId: 'Australia/Sydney'
      });
    }
    if (path.endsWith('/me/account-state')) {
      return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    }
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/master-product-suggestions')) {
      return json(route, { products: variants, page: 1, pageSize: 10, hasMore: false });
    }
    return json(route, []);
  });
}

function nameField(page: Page) {
  return page.locator('app-product-master-product-picker input[role="combobox"]');
}

async function open(page: Page, width: number): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  await answerApi(page);
  await page.goto('/products/new');
  await page.addStyleTag({
    content:
      '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }'
  });
  await expect(page.getByRole('heading', { name: 'New product', level: 1 })).toBeVisible();
}

/**
 * One capture at one width, after asserting the page does not scroll sideways there.
 *
 * `wholePage` grows the viewport to the page rather than passing `fullPage`, which is the idiom the
 * other visual runs use: a `fullPage` capture paints the sticky mobile navigation bar over the
 * middle of the page, and the tracking note it would cover is the subject of one of these captures.
 */
async function capture(page: Page, width: number, name: string, wholePage = false): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `no horizontal overflow at ${width}px`).toBeLessThanOrEqual(1);

  if (wholePage) {
    const height = await page.evaluate(() => document.documentElement.scrollHeight);
    await page.setViewportSize({ width, height });
  }

  await page.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`) });
}

test.use({ timezoneId: 'Australia/Sydney', locale: 'en-AU' });

test.describe('New Product master-product suggestions visual evidence (issue #1052)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of [1440, 390]) {
    test(`the suggestion list, with two size variants of one product, at ${width}px`, async ({ page }) => {
      await open(page, width);
      await nameField(page).fill('corn chips');

      const rows = page.locator('app-product-master-product-picker [role="option"]');
      await expect(rows).toHaveCount(2);
      await expect(rows).toHaveText([
        'Sample Brand Tasty Cheese Corn Chips · 45 g',
        'Sample Brand Tasty Cheese Corn Chips · 175 g'
      ]);

      await capture(page, width, 'suggestions-open');
    });

    test(`a taken suggestion, with the tracking note and its opt-out, at ${width}px`, async ({ page }) => {
      await open(page, width);
      await nameField(page).fill('corn chips');

      const rows = page.locator('app-product-master-product-picker [role="option"]');
      await expect(rows).toHaveCount(2);
      await rows.last().click();

      await expect(nameField(page)).toHaveValue('Sample Brand Tasty Cheese Corn Chips');
      await expect(page.locator('input[name="unit"]')).toHaveValue('175g');
      await expect(page.locator('[data-testid="product-master-product-selection"]')).toBeVisible();
      await expect(page.locator('input[name="trackSupplierPrices"]')).toBeChecked();

      // The whole page: the note and its opt-out checkbox are the point of this capture and they
      // sit below the fold at 390px, so photographing one viewport would cut them off.
      await capture(page, width, 'selection-tracking', /* wholePage */ true);
    });
  }
});
