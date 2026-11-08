import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence for issue #1128, the redesigned Sites overview: desktop at 1440px, mobile at
 * 390px, the Needs refill filter, the no-results state, the loading state and an Operator's view
 * without sales figures.
 *
 * Like the other visual runs this starts no API and no database. Every `/api/` request is answered in
 * the browser with neutral synthetic sites (none of the design mock's values), so no business data,
 * account name or credential can appear in a capture. The fleet deliberately includes a site whose
 * overall stock is high while several slots are low, a site with empty slots, a healthy site and a
 * site whose counts are missing, so every refill state is on screen. Every capture asserts the page
 * has no horizontal overflow. It runs once, in the `desktop-1440` project, and sets each width itself.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1128');
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

const OPERATOR_CAPABILITIES = ['Dashboard.View', 'PickList.View', 'Inventory.Operate'];

/** `stock` is [percentage, low, empty]; `sales` is [today, this week, previous comparable week]. */
function site(
  siteId: number,
  siteName: string,
  machineCount: number,
  stock: [number | null, number | null, number | null],
  sales: [number, number, number]
) {
  const [percentage, low, empty] = stock;
  const [today, week, previous] = sales;
  return {
    siteId,
    siteName,
    machineCount,
    totalStockPercentage: percentage,
    lowProductCount: low,
    emptyProductCount: empty,
    todayRevenue: today,
    currentWeekRevenue: week,
    previousComparableWeekRevenue: previous,
    lastWeekRevenue: previous + 20,
    monthToDateRevenue: week * 3,
    machineStocks: []
  };
}

const sites = [
  site(1, 'Sample Avenue', 1, [92, 4, 0], [36.7, 249.6, 221.87]),
  site(2, 'Sample Pavilion', 2, [94, 1, 2], [174.7, 823.2, 950.58]),
  site(3, 'Sample Central Hub', 1, [98, 0, 0], [48.2, 312, 299.42]),
  // Missing counts are not something today's API sends; they are here to show the defensive state.
  site(4, 'Sample Metro Terminal', 1, [null, null, null], [0, 0, 0]),
  site(5, 'Sample Harbour Kiosk With A Longer Name', 3, [71, 6, null], [12.4, 140.15, 0])
];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

type ApiOptions = { operator?: boolean; holdSites?: boolean; sites?: ReturnType<typeof site>[] };

async function answerApi(page: Page, options: ApiOptions = {}): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/me/access')) {
      return json(route, {
        role: options.operator ? 'Operator' : 'Owner',
        capabilities: options.operator ? OPERATOR_CAPABILITIES : OWNER_CAPABILITIES,
        businessName: 'Sample Vending',
        timeZoneId: 'Australia/Sydney'
      });
    }
    if (path.endsWith('/me/account-state')) return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/api/sites')) {
      if (options.holdSites) return new Promise<void>(() => undefined);
      const fleet = options.sites ?? sites;
      const visible = options.operator
        ? fleet.map((s) => ({ ...s, todayRevenue: null, currentWeekRevenue: null, previousComparableWeekRevenue: null, lastWeekRevenue: null, monthToDateRevenue: null }))
        : fleet;
      return json(route, visible);
    }
    if (route.request().method() !== 'GET') return route.fulfill({ status: 204, body: '' });
    return json(route, []);
  });
}

async function openSites(page: Page, width: number, options: ApiOptions = {}): Promise<void> {
  await page.setViewportSize({ width, height: width >= 1024 ? 900 : 844 });
  await answerApi(page, options);
  await page.goto('/sites');
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
  await expect(page.getByRole('heading', { name: 'Sites', level: 1 })).toBeVisible();
  if (!options.holdSites && !options.sites) await expect(page.locator('[data-testid="sites-summary"]')).toHaveText('5 sites · 8 machines');
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

async function capture(page: Page, name: string, fullPage = true): Promise<void> {
  await expectNoHorizontalOverflow(page);
  await page.screenshot({ path: join(screenshotDirectory, `${name}.png`), fullPage });
}

/** The minimum rendered height of each visible element the locator matches. */
function minHeight(page: Page, selector: string): Promise<number> {
  return page.locator(selector).evaluateAll((elements) =>
    Math.min(...elements.filter((el) => (el as HTMLElement).offsetParent !== null).map((el) => el.getBoundingClientRect().height))
  );
}

test.describe('Sites overview redesign (issue #1128)', () => {
  test('1 desktop at 1440px', async ({ page }) => {
    await openSites(page, 1440);

    await expect(page.locator('thead th')).toHaveText([
      'Site',
      'Machines',
      'Stock level',
      'Refill status',
      'Sales today',
      'Sales this week',
      'vs last week'
    ]);
    // Refill priority: empty first, then by low count, then unavailable, then Stock OK.
    await expect(page.locator('[data-testid="site-row"] a')).toHaveText([
      'Sample Pavilion',
      'Sample Harbour Kiosk With A Longer Name',
      'Sample Avenue',
      'Sample Metro Terminal',
      'Sample Central Hub'
    ]);
    const first = page.locator('[data-testid="site-row"] a').first();
    await expect(first).toHaveAttribute('href', '/sites/2/products');
    await first.focus();
    await expect(first).toBeFocused();
    await capture(page, 'sites-desktop-1440');
  });

  test('2 desktop Needs refill filter at 1440px', async ({ page }) => {
    await openSites(page, 1440);
    await page.getByRole('button', { name: /^Needs refill/ }).click();
    await expect(page.getByRole('button', { name: /^Needs refill/ })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('[data-testid="site-row"]')).toHaveCount(3);
    await capture(page, 'sites-desktop-needs-refill-1440', false);
  });

  test('3 desktop no results at 1440px', async ({ page }) => {
    await openSites(page, 1440);
    await page.getByLabel('Search by site name').fill('central');
    await page.getByRole('button', { name: /^Needs refill/ }).click();
    await expect(page.locator('[data-testid="sites-no-results"]')).toContainText('No sites match "central" with a known low or empty slot.');
    await capture(page, 'sites-desktop-no-results-1440', false);
    await page.getByRole('button', { name: 'Clear search and filter' }).click();
    await expect(page.locator('[data-testid="site-row"]')).toHaveCount(5);
  });

  test('4 mobile at 390px', async ({ page }) => {
    await openSites(page, 390);

    await expect(page.locator('[data-testid="sites-table"]')).toBeHidden();
    await expect(page.locator('[data-testid="site-card"]')).toHaveCount(5);
    // The sort select itself is 40px inside its 44px labelled box.
    expect(await minHeight(page, '[data-testid="sites-sort"]')).toBeGreaterThanOrEqual(40);
    // The whole sort control (its label box) and each card link reach 44px.
    expect(await minHeight(page, '[data-testid="site-card"] a')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="sites-filter"] button')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="sites-search"]')).toBeGreaterThanOrEqual(44);
    // Zoom stays allowed.
    const viewport = await page.locator('meta[name="viewport"]').getAttribute('content');
    expect(viewport ?? '').not.toMatch(/user-scalable\s*=\s*no|maximum-scale\s*=\s*1(\.0)?\b/);
    await capture(page, 'sites-mobile-390');
  });

  test('5 mobile Needs refill at 390px', async ({ page }) => {
    await openSites(page, 390);
    await page.getByRole('button', { name: /^Needs refill/ }).click();
    await expect(page.locator('[data-testid="site-card"]')).toHaveCount(3);
    await capture(page, 'sites-mobile-needs-refill-390');
  });

  test('6 loading at 1440px', async ({ page }) => {
    await openSites(page, 1440, { holdSites: true });
    await expect(page.locator('[data-testid="sites-loading"]')).toBeVisible();
    await capture(page, 'sites-desktop-loading-1440', false);
  });

  test('8 a long unbroken site name wraps inside its mobile card at 320px', async ({ page }) => {
    const longName = 'SampleSupercalifragilisticVendingKioskTerminalNorthWing';
    await openSites(page, 320, { sites: [site(9, longName, 1, [88, 3, 1], [10, 100, 90]), ...sites] });

    const name = page.locator('[data-testid="site-card-name"]', { hasText: longName });
    await expect(name).toBeVisible();
    await expect(name).toContainText(longName);
    // Nothing of the name is cut off: it fits its own box and stays inside its card.
    const fits = await name.evaluate((el) => {
      const card = el.closest('[data-testid="site-card"]') as HTMLElement;
      const box = el.getBoundingClientRect();
      const cardBox = card.getBoundingClientRect();
      return {
        noClip: el.scrollWidth <= el.clientWidth + 1,
        inside: box.left >= cardBox.left && box.right <= cardBox.right + 0.5,
        wrapped: box.height > 30
      };
    });
    expect(fits).toEqual({ noClip: true, inside: true, wrapped: true });
    // The link keeps its 44px touch target.
    const link = name.locator('xpath=ancestor::a[1]');
    expect((await link.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await capture(page, 'sites-mobile-long-name-320');
  });

  for (const width of [1440, 390]) {
    test(`7 Operator without sales at ${width}px`, async ({ page }) => {
      await openSites(page, width, { operator: true });
      await expect(page.getByText('Sales today')).toHaveCount(0);
      await capture(page, `sites-operator-${width}`, width < 1024);
    });
  }
});
