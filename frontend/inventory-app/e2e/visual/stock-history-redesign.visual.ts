import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence for issue #1156, the redesigned Stock History: the desktop ledger at 1440px
 * with a movement's details open, the Adjust stock drawer over it, the mobile movement cards at
 * 390px and the full-screen adjustment form, plus the More filters disclosure.
 *
 * Like the other visual runs this starts no API and no database. Every `/api/` request is answered
 * in the browser with neutral synthetic products, machines and movements (none of the design mock's
 * values), so no business data, account name or credential can appear in a capture. The movements
 * include a correction that ended at a recorded zero, Nayax-sourced machine refills and a manual
 * restock. Every capture asserts the page has no horizontal overflow. It runs once, in the
 * `desktop-1440` project, and sets each width itself.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1156');
mkdirSync(screenshotDirectory, { recursive: true });

const OWNER_CAPABILITIES = [
  'Dashboard.View',
  'Dashboard.ViewFinancials',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate',
  'Expenses.Manage',
  'Reports.View',
  'Imports.Run',
  'SiteCommissions.Manage',
  'Integration.Manage',
  'Members.Manage',
  'Roles.View',
  'Business.Manage'
];

const products = [
  { id: 1, name: 'Sample Jerky 25 g', unit: '25 g', quantityInStock: 9, lowStockThreshold: 3, isActive: true, isLowStock: false },
  { id: 2, name: 'Sample Cola 375 mL', unit: '375 mL', quantityInStock: 0, lowStockThreshold: 6, isActive: true, isLowStock: true }
];

const machines = [
  { id: 41, name: 'Sample Pavilion Right', isActive: true, isProviderNamed: false },
  { id: 42, name: 'Sample Lobby', isActive: true, isProviderNamed: false }
];

// reason: Restock 0, Sale 1, Damaged 2, Expired 3, Correction 4, MachineRefill 5; source: Manual 0, Nayax 1.
const movements = [
  { id: 105, productId: 1, productName: 'Sample Jerky 25 g', quantityChange: -3, quantityAfter: 9, unitCost: null, reason: 5, source: 1, machineId: 41, notes: null, eatBefore: null, createdAt: '2026-10-29T09:54:00Z' },
  { id: 104, productId: 1, productName: 'Sample Jerky 25 g', quantityChange: 12, quantityAfter: 12, unitCost: 3.49, reason: 0, source: 0, machineId: null, notes: 'Supplier delivery', eatBefore: '2027-03-01', createdAt: '2026-10-27T12:05:00Z' },
  { id: 103, productId: 1, productName: 'Sample Jerky 25 g', quantityChange: -4, quantityAfter: 0, unitCost: 3.49, reason: 4, source: 0, machineId: null, notes: 'Stock count', eatBefore: null, createdAt: '2026-10-25T08:39:00Z' },
  { id: 102, productId: 2, productName: 'Sample Cola 375 mL', quantityChange: -6, quantityAfter: 0, unitCost: null, reason: 5, source: 0, machineId: 42, notes: null, eatBefore: null, createdAt: '2026-10-22T10:08:00Z' },
  { id: 101, productId: 1, productName: 'Sample Jerky 25 g', quantityChange: -1, quantityAfter: 4, unitCost: null, reason: 5, source: 1, machineId: 41, notes: null, eatBefore: null, createdAt: '2026-10-15T22:29:00Z' }
];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function answerApi(page: Page): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path.endsWith('/me/access')) {
      return json(route, { role: 'Owner', capabilities: OWNER_CAPABILITIES, businessName: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    }
    if (path.endsWith('/me/account-state')) return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/api/products')) return json(route, products);
    if (path.endsWith('/api/local-machines')) return json(route, machines);
    if (path.endsWith('/restock-cost-suggestion')) return json(route, { unitCost: 3.49, source: 'LastPurchase', purchaseDate: '2026-10-27' });
    if (path.endsWith('/api/stock-history')) {
      const productId = url.searchParams.get('productId');
      const reason = url.searchParams.get('reason');
      const items = movements.filter(
        (m) => (productId === null || String(m.productId) === productId) && (reason === null || String(m.reason) === reason)
      );
      return json(route, { items, page: 1, pageSize: Number(url.searchParams.get('pageSize') ?? 50), totalCount: items.length, hasMore: false });
    }
    if (route.request().method() !== 'GET') return route.fulfill({ status: 204, body: '' });
    return json(route, []);
  });
}

async function openStockHistory(page: Page, width: number, query = '?productId=1'): Promise<void> {
  await page.setViewportSize({ width, height: width >= 1024 ? 900 : 844 });
  await answerApi(page);
  await page.goto(`/stock-history${query}`);
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
  await expect(page.getByRole('heading', { name: 'Stock history', level: 1 })).toBeVisible();
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

async function capture(page: Page, name: string, fullPage = true): Promise<void> {
  await expectNoHorizontalOverflow(page);
  await page.screenshot({ path: join(screenshotDirectory, `${name}.png`), fullPage });
}

function minHeight(page: Page, selector: string): Promise<number> {
  return page.locator(selector).evaluateAll((elements) =>
    Math.min(...elements.filter((el) => (el as HTMLElement).offsetParent !== null).map((el) => el.getBoundingClientRect().height))
  );
}

test.describe('Stock History redesign (issue #1156)', () => {
  test.beforeEach(({}, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop-1440', 'Each test sets its own width.');
  });

  test('1 desktop ledger for one product with a movement expanded at 1440px', async ({ page }) => {
    await openStockHistory(page, 1440);

    await expect(page.locator('[data-testid="history-table"] thead th')).toHaveText([
      'Date and time',
      'Change',
      'Stock after',
      'Reason',
      'Source',
      'Machine',
      'Details'
    ]);
    await expect(page.locator('[data-testid="summary-current-stock"]')).toHaveText('9');
    await expect(page.locator('[data-testid="summary-unit-label"]')).toHaveText('25 g');
    const correction = page.locator('[data-testid="history-rows"] tr', { hasText: 'Correction' });
    await expect(correction.locator('td').nth(1)).toHaveText('−4');
    await expect(correction.locator('td').nth(2)).toHaveText('0');
    await correction.getByRole('button', { name: /Show details/ }).click();
    await expect(page.locator('[data-testid="movement-details"] [data-testid="movement-unit-cost"]')).toHaveText('$3.49');
    await capture(page, 'stock-history-desktop-1440');
  });

  test('2 desktop all products with More filters at 1440px', async ({ page }) => {
    await openStockHistory(page, 1440, '');

    await expect(page.locator('[data-testid="history-table"] thead th').nth(1)).toHaveText('Product');
    await page.getByRole('button', { name: 'More filters' }).click();
    await page.getByLabel('Reason').selectOption({ label: 'Machine refill' });
    await expect(page.locator('[data-testid="applied-filters"]')).toContainText('Reason: Machine refill');
    await expect(page.locator('[data-testid="history-rows"] tr')).toHaveCount(3);
    await capture(page, 'stock-history-desktop-filters-1440');
  });

  test('3 desktop Adjust stock drawer at 1440px', async ({ page }) => {
    await openStockHistory(page, 1440);
    const open = page.getByRole('button', { name: 'Adjust stock' });
    await open.click();

    const panel = page.getByRole('dialog', { name: 'Adjust stock' });
    await expect(panel).toBeVisible();
    await expect(page.locator('#adjustment-quantity-change')).toBeFocused();
    // The drawer sits on the right, over the ledger, which stays in place behind it.
    const box = (await panel.boundingBox())!;
    expect(box.x + box.width).toBeGreaterThanOrEqual(1439);
    expect(box.width).toBeLessThan(600);
    await expect(page.locator('[data-testid="history-rows"]')).toBeAttached();
    await expect(page.locator('#adjustment-machine')).toBeDisabled();
    await page.locator('#adjustment-reason').selectOption({ label: 'Machine refill' });
    await expect(page.locator('#adjustment-machine')).toBeEnabled();
    await page.locator('#adjustment-quantity-change').fill('-3');
    await capture(page, 'stock-history-desktop-drawer-1440', false);

    // Keyboard: Escape closes and focus returns to Adjust stock.
    await page.keyboard.press('Escape');
    await expect(panel).toBeHidden();
    await expect(open).toBeFocused();
  });

  test('4 mobile ledger cards at 390px', async ({ page }) => {
    await openStockHistory(page, 390, '');

    await expect(page.locator('[data-testid="history-table"]')).toBeHidden();
    await expect(page.locator('[data-testid="history-card"]')).toHaveCount(5);
    await page.locator('[data-testid="history-card"]').nth(1).getByRole('button', { name: 'Show details' }).click();
    expect(await minHeight(page, '[data-testid="movement-card-details-toggle"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="more-filters"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="list-pagination-next"]')).toBeGreaterThanOrEqual(40);
    const viewport = await page.locator('meta[name="viewport"]').getAttribute('content');
    expect(viewport ?? '').not.toMatch(/user-scalable\s*=\s*no|maximum-scale\s*=\s*1(\.0)?\b/);
    await capture(page, 'stock-history-mobile-390');
  });

  test('5 mobile full-screen adjustment form at 390px', async ({ page }) => {
    await openStockHistory(page, 390);
    await page.getByRole('button', { name: 'Adjust stock' }).click();

    const panel = page.getByRole('dialog', { name: 'Adjust stock' });
    const box = (await panel.boundingBox())!;
    expect(box.width).toBeGreaterThanOrEqual(389);
    expect(box.height).toBeGreaterThanOrEqual(843);
    expect(await minHeight(page, '[data-testid="apply-adjustment"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="cancel-adjustment"]')).toBeGreaterThanOrEqual(44);
    await page.locator('#adjustment-quantity-change').fill('12');
    await expect(page.locator('#adjustment-unit-cost')).toHaveValue('3.49');
    await capture(page, 'stock-history-mobile-form-390', false);

    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(panel).toBeHidden();
  });

  test('6 narrow 320px without horizontal scrolling', async ({ page }) => {
    await openStockHistory(page, 320, '');
    await expect(page.locator('[data-testid="history-card"]').first()).toBeVisible();
    await capture(page, 'stock-history-mobile-320');
  });
});
