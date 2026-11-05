import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence for issue #1093, with the always-visible history fields and direct
 * cheapest-supplier View links of its owner-corrected follow-up #1098: the redesigned Products
 * page's desktop default (now carrying Last/Cheapest purchase and a View link beside the cheapest
 * supplier by default, with no Columns control left), a tied cheapest offer's two distinct View
 * links, mobile, More actions open, a row's overflow menu open and the supplier comparison open -
 * at 1440px, plus mobile and the supplier comparison at 390px.
 *
 * Like the other visual runs this starts no API and no database. Every `/api/` request is answered
 * in the browser with neutral synthetic products, purchase costs and observed supplier prices (none
 * of the design mock's values), so no business data, account name or credential can appear in a
 * capture, and nothing is ever saved. Every capture also asserts the page has no horizontal
 * overflow - the wide history table scrolls inside its own card - and the dialog captures assert
 * the comparison fits inside the viewport. It runs once, in the `desktop-1440` project, and sets
 * each width itself.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1093');
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

const categories = [
  { id: 1, name: 'Snacks' },
  { id: 2, name: 'Drinks' },
  { id: 3, name: 'Sweets' }
];

function product(id: number, name: string, sku: string, categoryId: number, stock: number, averageCost: number | null) {
  const category = categories.find((c) => c.id === categoryId) ?? null;
  return {
    id,
    name,
    sku,
    description: null,
    averageUnitCost: averageCost ?? 0,
    averageUnitCostForDisplay: averageCost,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: stock,
    maxStockInMachine: 10,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 20,
    needToOrder: 0,
    mdbCode: null,
    unit: 'ea',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId,
    category,
    isActive: true,
    isLowStock: stock <= 5,
    isReorderAlert: stock <= 5,
    canDelete: id % 3 === 0,
    deletionRefusal: null
  };
}

const products = [
  product(1, 'Sample Beef Jerky Original 25g', 'SMP-JRK-25', 1, 9, 3.48),
  product(2, 'Sample Mint Bubbles Chocolate Bar With A Deliberately Long Name 40g', 'SMP-MNT-40', 3, 17, 1.5),
  product(3, 'Sample Cheese & Crackers 38g', 'SMP-CHC-38', 1, 2, 2.5),
  product(4, 'Sample Iced Coffee Double Shot 237mL', 'SMP-ICD-237', 2, 4, 2.61),
  product(5, 'Sample Iced Latte 237mL', 'SMP-ICL-237', 2, 6, 2.65),
  product(6, 'Sample Ginger Beer 250mL', 'SMP-GNB-250', 2, 10, 1.31),
  product(7, 'Sample Cherry Coconut Bar 44g', 'SMP-CHR-44', 3, 0, 1.5),
  product(8, 'Sample Hazelnut Wafer 43g', 'SMP-HZW-43', 3, 12, 1.42),
  product(9, 'Sample Energy Drink 500mL', 'SMP-ENR-500', 2, 8, 2.15),
  product(10, 'Sample Salt & Vinegar Chips 45g', 'SMP-SVC-45', 1, 15, null),
  ...Array.from({ length: 13 }, (_, index) =>
    product(11 + index, `Sample Product ${String(11 + index).padStart(2, '0')}`, `SMP-${11 + index}`, (index % 3) + 1, 6 + index, 1 + index / 10)
  )
];

function cost(productId: number, last: number, lastSupplier: string, lastDate: string, low: number, lowSupplier: string, lowDate: string) {
  return {
    productId,
    lastUnitCost: last,
    lastSupplierName: lastSupplier,
    lastPurchaseDate: `${lastDate}T00:00:00`,
    lowestUnitCost: low,
    lowestSupplierName: lowSupplier,
    lowestPurchaseDate: `${lowDate}T00:00:00`
  };
}

/** Purchase history for most products; product 8 has none, so its history cells show the em dash. */
const purchaseCosts = [
  cost(1, 3.48, 'Sample Wholesale', '2026-10-18', 3.47, 'Sample Wholesale', '2026-08-04'),
  cost(2, 1.5, 'Sample Grocer', '2026-10-20', 1.5, 'Sample Market', '2026-09-12'),
  cost(3, 2.5, 'Sample Grocer', '2026-10-15', 2.5, 'Sample Grocer', '2026-10-15'),
  cost(4, 2.94, 'Sample Market', '2026-10-24', 2.2, 'Sample Market', '2026-09-12'),
  cost(5, 2.2, 'Sample Market', '2026-10-19', 2.2, 'Sample Market', '2026-10-19'),
  cost(6, 1.2, 'Sample Wholesale', '2026-10-14', 1.2, 'Sample Wholesale', '2026-10-14'),
  cost(7, 1.5, 'Sample Grocer', '2026-10-02', 1.5, 'Sample Grocer', '2026-10-02'),
  cost(9, 2.15, 'Sample Grocer', '2026-10-05', 1.95, 'Sample Market', '2026-07-01'),
  cost(10, 1.61, 'Sample Wholesale', '2026-10-10', 1.61, 'Sample Wholesale', '2026-10-10')
];

function offer(supplierId: number, supplierName: string, packQuantity: number | null, packPrice: number | null, unitPrice: number | null, extra: Record<string, unknown> = {}) {
  return {
    supplierId,
    supplierName,
    hasListing: true,
    hasPrice: unitPrice !== null || packPrice !== null,
    packQuantity,
    packPrice,
    packPriceBasis: packPrice === null ? null : 2,
    unitPrice,
    unitPriceExGst: unitPrice === null ? null : Math.round((unitPrice / 1.1) * 10000) / 10000,
    unitPriceIncGst: unitPrice,
    isSpecial: false,
    promotionEndsAtUtc: null,
    stockStatus: 1,
    lastCheckedAtUtc: '2026-11-04T02:00:00Z',
    productUrl: `https://example.test/${supplierId}/${packQuantity ?? 0}`,
    ineligibility: null,
    isCheapest: false,
    ...extra
  };
}

const MATCHED = 3;
const cheapestSuppliers = {
  displayBasis: 2,
  products: [
    { productId: 1, trackingStatus: MATCHED, cheapestUnitPrice: 3.82, suppliers: [offer(1, 'Sample Wholesale South Coast', 12, 45.84, 3.82, { isCheapest: true })] },
    { productId: 2, trackingStatus: MATCHED, cheapestUnitPrice: 1.5, suppliers: [offer(2, 'Sample Grocer', 1, 1.5, 1.5, { isCheapest: true, isSpecial: true })] },
    {
      productId: 3,
      trackingStatus: MATCHED,
      cheapestUnitPrice: 3,
      suppliers: [offer(2, 'Sample Grocer', 1, 3, 3, { isCheapest: true }), offer(3, 'Sample Market', 1, 3, 3, { isCheapest: true, isSpecial: true })]
    },
    {
      productId: 4,
      trackingStatus: MATCHED,
      cheapestUnitPrice: 1.84,
      suppliers: [
        offer(2, 'Sample Grocer', 12, 22.08, 1.84, { isCheapest: true, isSpecial: true }),
        offer(3, 'Sample Market', 1, 2.2, 2.2, { lastCheckedAtUtc: '2026-11-03T22:00:00Z' }),
        offer(1, 'Sample Wholesale South Coast', 24, 57.6, 2.4, { lastCheckedAtUtc: '2026-11-03T05:00:00Z' }),
        offer(4, 'Sample Bulk Club', null, null, null, { hasPrice: false, lastCheckedAtUtc: null, stockStatus: null, productUrl: null })
      ]
    },
    { productId: 5, trackingStatus: MATCHED, cheapestUnitPrice: 2.2, suppliers: [offer(3, 'Sample Market', 1, 2.2, 2.2, { isCheapest: true, isSpecial: true })] },
    { productId: 6, trackingStatus: MATCHED, cheapestUnitPrice: 1.2, suppliers: [offer(1, 'Sample Wholesale South Coast', 24, 28.8, 1.2, { isCheapest: true, isSpecial: true })] },
    { productId: 7, trackingStatus: MATCHED, cheapestUnitPrice: 1.5, suppliers: [offer(3, 'Sample Market', 1, 1.5, 1.5, { isCheapest: true })] },
    { productId: 8, trackingStatus: MATCHED, cheapestUnitPrice: null, suppliers: [offer(2, 'Sample Grocer', 1, 1.35, 1.35, { ineligibility: 5 })] },
    {
      productId: 9,
      trackingStatus: MATCHED,
      cheapestUnitPrice: null,
      suppliers: [offer(3, 'Sample Market', 1, 2.1, 2.1, { ineligibility: 6, lastCheckedAtUtc: '2026-11-02T20:00:00Z' })]
    },
    { productId: 11, trackingStatus: 1, cheapestUnitPrice: null, suppliers: [] }
  ]
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function answerApi(page: Page): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/me/access')) {
      return json(route, { role: 'Owner', capabilities: OWNER_CAPABILITIES, businessName: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    }
    if (path.endsWith('/me/account-state')) return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/api/products')) return json(route, products);
    if (path.endsWith('/categories')) return json(route, categories);
    if (path.endsWith('/products/purchase-cost-summary')) return json(route, purchaseCosts);
    if (path.endsWith('/cheapest-suppliers/summary')) return json(route, cheapestSuppliers);
    if (route.request().method() !== 'GET') return route.fulfill({ status: 204, body: '' });
    return json(route, []);
  });
}

async function openProducts(page: Page, width: number): Promise<void> {
  await page.setViewportSize({ width, height: width >= 1024 ? 1000 : 844 });
  await answerApi(page);
  await page.goto('/products');
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
  await expect(page.getByRole('heading', { name: 'Products', level: 1 })).toBeVisible();
  await expect(page.locator('[data-testid="cheapest-supplier-compare"]:visible').first()).toBeVisible();
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

async function capture(page: Page, name: string, fullPage = true): Promise<void> {
  await expectNoHorizontalOverflow(page);
  await page.screenshot({ path: join(screenshotDirectory, `${name}.png`), fullPage });
}

test.describe('Products redesign (issue #1093, issue #1098)', () => {
  test('1 desktop default at 1440px', async ({ page }) => {
    await openProducts(page, 1440);

    await expect(page.locator('thead th')).toHaveText([
      'Product',
      'Category',
      'Storage stock',
      'Average cost',
      'Last purchase',
      'Cheapest purchase',
      'Cheapest supplier',
      'Actions'
    ]);
    await expect(page.locator('[data-testid="list-pagination-range"]')).toHaveText(/Showing 1–10 of 23 products/);
    await expect(page.getByRole('menu')).toHaveCount(0);
    await expect(page.locator('dialog[open]')).toHaveCount(0);
    // No Columns control is left to turn the history fields on - both are already on screen.
    await expect(page.getByRole('button', { name: 'Columns' })).toHaveCount(0);
    // A direct View link sits beside the single cheapest supplier, opening its own page.
    const link = page.getByRole('link', { name: 'Open Sample Wholesale South Coast product page for Sample Beef Jerky Original 25g' });
    await expect(link).toHaveText('View ↗');
    await expect(link).toHaveAttribute('href', 'https://example.test/1/12');
    await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    // The wide table scrolls inside its card; the page itself never does.
    const table = page.locator('[data-testid="products-table"]');
    const scroll = await table.evaluate((el) => ({ scroll: el.scrollWidth, client: el.clientWidth }));
    expect(scroll.scroll).toBeGreaterThanOrEqual(scroll.client);
    await capture(page, 'products-desktop-default-1440');
  });

  test('2 a tied cheapest offer gets two distinct View links at 1440px', async ({ page }) => {
    await openProducts(page, 1440);

    // Sample Cheese & Crackers 38g (product 3) is tied between Sample Grocer and Sample Market;
    // each keeps its own link to its own page, named so neither can be mistaken for the other's.
    const grocer = page.getByRole('link', { name: 'Open Sample Grocer product page for Sample Cheese & Crackers 38g' });
    const market = page.getByRole('link', { name: 'Open Sample Market product page for Sample Cheese & Crackers 38g' });
    await expect(grocer).toHaveText('View Sample Grocer ↗');
    await expect(grocer).toHaveAttribute('href', 'https://example.test/2/1');
    await expect(market).toHaveText('View Sample Market ↗');
    await expect(market).toHaveAttribute('href', 'https://example.test/3/1');
    await capture(page, 'products-desktop-tied-view-links-1440', false);
  });

  test('3 mobile at 390px', async ({ page }) => {
    await openProducts(page, 390);

    await expect(page.locator('[data-testid="product-card"]')).toHaveCount(10);
    await expect(page.locator('[data-testid="products-table"]')).toBeHidden();
    await expect(page.getByRole('button', { name: 'Columns' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'More actions', exact: true })).toBeVisible();
    await expect(page.locator('[data-testid="product-card"]').first()).toContainText('Last purchase:');
    await expect(page.getByRole('link', { name: 'Open Sample Wholesale South Coast product page for Sample Beef Jerky Original 25g' })).toBeVisible();
    await capture(page, 'products-mobile-390');
  });

  test('4 More actions open at 1440px', async ({ page }) => {
    await openProducts(page, 1440);
    const trigger = page.getByRole('button', { name: 'More actions', exact: true });
    await trigger.focus();
    await page.keyboard.press('Enter');

    const menu = page.getByRole('menu');
    await expect(menu.getByRole('menuitem')).toHaveText(['Import products', 'Set GST rule in bulk']);
    await expect(menu.getByRole('menuitem', { name: 'Import products' })).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(menu.getByRole('menuitem', { name: 'Set GST rule in bulk' })).toBeFocused();
    await capture(page, 'products-more-actions-open-1440', false);

    await page.keyboard.press('Escape');
    await expect(page.getByRole('menu')).toHaveCount(0);
    await expect(trigger).toBeFocused();
  });

  test('5 row overflow open at 1440px', async ({ page }) => {
    await openProducts(page, 1440);
    await page.getByRole('button', { name: 'More actions for Sample Cheese & Crackers 38g' }).click();

    const menu = page.getByRole('menu');
    await expect(menu.getByRole('menuitem')).toHaveText(['Deactivate', 'Delete']);
    await expect(page.getByRole('menu')).toHaveCount(1);
    await capture(page, 'products-row-overflow-open-1440', false);
  });

  for (const width of [1440, 390]) {
    test(`6 supplier comparison open at ${width}px`, async ({ page }) => {
      await openProducts(page, width);
      const compare = page.getByRole('button', { name: 'Compare 4 supplier prices for Sample Iced Coffee Double Shot 237mL' });
      await compare.click();

      const dialog = page.locator('dialog[open]');
      await expect(dialog).toBeVisible();
      await expect(dialog.locator('thead th')).toHaveText(['Supplier', 'Pack size', 'Pack price', 'Unit price', 'Status & freshness', 'Link']);
      const rect = await dialog.boundingBox();
      expect(rect).not.toBeNull();
      expect(rect!.x).toBeGreaterThanOrEqual(0);
      expect(rect!.x + rect!.width).toBeLessThanOrEqual(width);
      await expect(dialog.getByRole('button', { name: 'Close', exact: true })).toBeVisible();
      await capture(page, `products-supplier-comparison-open-${width}`, false);

      await page.keyboard.press('Escape');
      await expect(page.locator('dialog[open]')).toHaveCount(0);
      await expect(compare).toBeFocused();
    });
  }
});
