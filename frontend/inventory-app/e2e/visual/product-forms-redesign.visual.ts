import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence for issue #1111: New Product and Edit Product on the approved Restockroo
 * layout, at desktop (1440px), tablet (834px) and narrow mobile (390px) widths.
 *
 * Like the other visual runs it starts no API and no database. Every `/api/` request is answered in
 * the browser with neutral synthetic data, so no business data, account name or credential can
 * appear in a capture. The only writes it makes are answered here too - one deliberately refused GST
 * rule write, to capture the partial-save message - so nothing is ever stored anywhere.
 *
 * Each capture also asserts what jsdom cannot: that the page does not scroll sideways, that the
 * settings column sits beside the main column on a wide screen and below it on a narrow one, and
 * that the sticky action bar never covers the last card once the page is scrolled to its end.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1111');
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

const LONG_NAME = 'Sample Brand Iced Double Espresso With Extra Long Descriptive Product Name 237 mL';

/** A product whose name and category the sales provider manages, with a matched catalogue link. */
const nayaxProduct = {
  id: 7,
  name: LONG_NAME,
  sku: 'SMP-ESP-237',
  description: 'Ready-to-drink canned coffee.',
  averageUnitCost: null,
  machinePrice: null,
  commissionValue: null,
  suggestedNetValue: null,
  suggestedPriceValue: null,
  quantityInStock: 10,
  maxStockInMachine: 0,
  machineReplenishmentNeed: 0,
  onOrderQuantity: 0,
  lowStockThreshold: 4,
  restockTo: 12,
  needToOrder: 0,
  mdbCode: null,
  unit: '237 mL',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  categoryId: 1,
  isActive: true,
  isLowStock: false,
  isReorderAlert: false,
  providerManagedFields: ['name', 'categoryId'],
  canDelete: false,
  deletionRefusal: 'This product cannot be deleted because its history refers to it.'
};

/** A product this business maintains itself, with tracking off. */
const manualProduct = {
  ...nayaxProduct,
  id: 8,
  name: 'Sample Corn Chips',
  sku: 'SMP-CHIP-45',
  description: '',
  unit: '45 g',
  categoryId: 2,
  providerManagedFields: [],
  canDelete: true,
  deletionRefusal: null
};

const categories = [
  { id: 1, name: 'Drinks' },
  { id: 2, name: 'Snacks' }
];

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function answerApi(page: Page): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
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
    if (path.endsWith('/categories')) return json(route, categories);
    if (path.endsWith('/products/7')) return json(route, nayaxProduct);
    if (path.endsWith('/products/8') && request.method() === 'GET') return json(route, manualProduct);
    if (path.endsWith('/products/8') && request.method() === 'PUT') return route.fulfill({ status: 204 });
    if (path.endsWith('/products/7/gst-rule')) return json(route, { productId: 7, gstRule: 1 });
    if (path.endsWith('/products/8/gst-rule') && request.method() === 'GET') return json(route, { productId: 8, gstRule: 0 });
    // The one refused write: it is what the partial-save capture shows.
    if (path.endsWith('/products/8/gst-rule')) return json(route, { title: 'Server error' }, 500);
    if (path.endsWith('/products/7/price-tracking')) {
      return json(route, {
        productId: 7,
        tracked: true,
        status: 3,
        searchName: LONG_NAME,
        unitLabel: '237 mL',
        masterProduct: { name: 'Sample Brand Iced Double Espresso Extra Long Catalogue Name', sizeValue: 237, sizeUnit: 'ml' },
        canTrack: true,
        trackRefusal: null
      });
    }
    if (path.endsWith('/products/8/price-tracking')) {
      return json(route, {
        productId: 8,
        tracked: false,
        status: null,
        searchName: null,
        unitLabel: null,
        masterProduct: null,
        canTrack: true,
        trackRefusal: null
      });
    }
    return json(route, []);
  });
}

async function open(page: Page, width: number, url: string, heading: string): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  await answerApi(page);
  await page.goto(url);
  await page.addStyleTag({
    content:
      '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }'
  });
  await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
}

/** The checks jsdom cannot make: overflow, column placement, and the action bar's clearance. */
async function assertLayout(page: Page, width: number): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `no horizontal overflow at ${width}px`).toBeLessThanOrEqual(1);

  const main = await page.locator('[data-testid="product-form-main"]').boundingBox();
  const settings = await page.locator('[data-testid="product-form-settings"]').boundingBox();
  expect(main && settings).toBeTruthy();
  if (width >= 1024) {
    expect(settings!.x, 'settings column beside the main column').toBeGreaterThanOrEqual(main!.x + main!.width);
    expect(settings!.width, 'settings column is the narrower one').toBeLessThan(main!.width);
  } else {
    expect(settings!.y, 'settings column below the main column').toBeGreaterThanOrEqual(main!.y + main!.height);
  }

  // Scrolled to the end, the last card ends above the sticky action bar.
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  const lastCard = await page.locator('[data-testid="product-form-settings"] section').last().boundingBox();
  const bar = await page.locator('[data-testid="product-form-actions"]').boundingBox();
  expect(lastCard!.y + lastCard!.height, 'the action bar does not cover the last card').toBeLessThanOrEqual(bar!.y + 1);
  await page.evaluate(() => window.scrollTo(0, 0));
}

/**
 * One whole-page capture. The viewport is grown to the page rather than passing `fullPage`, which is
 * the idiom the other visual runs use: a `fullPage` capture paints the sticky bars over the middle.
 */
async function capture(page: Page, width: number, name: string): Promise<void> {
  await assertLayout(page, width);
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  await page.setViewportSize({ width, height });
  await page.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`) });
}

test.use({ timezoneId: 'Australia/Sydney', locale: 'en-AU' });

test.describe('New and Edit Product visual evidence (issue #1111)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of [1440, 834, 390]) {
    test(`New Product at ${width}px`, async ({ page }) => {
      await open(page, width, '/products/new', 'New product');
      await expect(page.locator('[data-testid="product-section-opening-inventory"]')).toBeVisible();
      await capture(page, width, 'new-product');
    });

    test(`Edit Product, Nayax-managed, matched, with a long name at ${width}px`, async ({ page }) => {
      await open(page, width, '/products/7/edit', LONG_NAME);
      await expect(page.locator('[data-testid="product-form-provider-badge"]')).toHaveCount(1);
      await expect(page.locator('input[name="name"]')).toHaveAttribute('readonly', '');
      await expect(page.locator('[data-testid="price-tracking-status-label"]')).toHaveText('Matched');
      await expect(page.locator('[data-testid="product-section-opening-inventory"]')).toHaveCount(0);
      await capture(page, width, 'edit-product-nayax');
    });
  }

  for (const width of [1440, 390]) {
    test(`Edit Product partial save failure at ${width}px`, async ({ page }) => {
      await open(page, width, '/products/8/edit', 'Sample Corn Chips');
      await page.locator('input[name="sku"]').fill('SMP-CHIP-46');
      await page.locator('#product-gst-rule').selectOption({ label: 'Taxable' });
      await expect(page.locator('[data-testid="product-form-save-state"]')).toContainText('Unsaved changes');
      await page.locator('[data-testid="product-form-submit"]').click();
      await expect(page.locator('[data-testid="product-form-save-problem"]')).toContainText('Saved: Product details.');
      await expect(page.locator('[data-testid="product-form-save-problem"]')).toContainText('Not saved: GST rule.');
      await capture(page, width, 'edit-product-partial-save');
    });
  }

  test('keyboard focus reaches every control and the primary action is reachable by Tab', async ({ page }) => {
    await open(page, 1440, '/products/8/edit', 'Sample Corn Chips');
    await page.locator('#product-gst-rule').focus();
    // Product status' switch is before the GST rule; tracking's switch follows it.
    await page.keyboard.press('Tab');
    await expect(page.locator('[data-testid="price-tracking-switch"]')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.locator('[data-testid="product-form-cancel"]')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.locator('[data-testid="product-form-submit"]')).toBeFocused();
  });

  test('touch targets of the action bar and switches are at least 44px tall at 390px', async ({ page }) => {
    await open(page, 390, '/products/8/edit', 'Sample Corn Chips');
    for (const selector of [
      '[data-testid="product-form-submit"]',
      '[data-testid="product-form-cancel"]',
      '[data-testid="product-section-status"] label',
      '[data-testid="product-price-tracking"] label'
    ]) {
      const box = await page.locator(selector).first().boundingBox();
      expect(box!.height, `${selector} height`).toBeGreaterThanOrEqual(44);
    }
  });
});
