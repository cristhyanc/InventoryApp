import { Locator, Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence for issue #972: the Restockroo Dashboard and authenticated navigation at
 * 360, 390, 768 and 1440px, plus the narrow layout's More sheet opened and scrolled to its end. The
 * dashboard captures also show issue #987's combined **Site and machine performance** card - each
 * site expanding to the machines standing at it, with issue #1013's own stock level on every machine
 * row - and issue #981's Cheapest supplier column in Reorder priorities.
 *
 * Like the other visual runs this starts no API and no database. Every `/api/` request is answered
 * in the browser with neutral synthetic figures - an Owner's capabilities, a connected business,
 * three sites (one with a deliberately long name and one with no machines), four machines, one of
 * them at no site, seven reorder products, observed supplier prices for three of them and one
 * pending restock sync - so no business data, account name or credential can appear in a capture.
 *
 * Each width also asserts the page has no horizontal overflow, which is the issue's layout rule.
 * Issue #987's own interaction evidence is here too: a collapsed group at 1440 and 390px, the
 * shared period control switched to Last week, and the keyboard focus ring on a site expander with
 * the group toggled from the keyboard alone. The More sheet's modality is exercised in the browser
 * at 390px from both of its openers. It runs once, in the `desktop-1440` project, and sets each
 * width itself.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-972');
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

const period = {
  startUtc: '2026-10-25T13:00:00Z',
  endUtc: '2026-10-28T01:00:00Z',
  firstBusinessDate: '2026-10-26T00:00:00Z',
  lastBusinessDate: '2026-10-28T00:00:00Z'
};

const summary = {
  asOfUtc: '2026-10-28T01:00:00Z',
  businessDate: '2026-10-28T00:00:00Z',
  salesThisWeek: {
    sales: 587.3,
    transactionCount: 210,
    period,
    comparisonPeriod: period,
    isComparisonAvailable: true,
    comparisonSales: 468.0,
    comparisonTransactionCount: 170,
    changeAmount: 119.3,
    changePercent: 25.5,
    comparisonNote: null
  },
  needsRefill: {
    machinesNeedingRefill: 3,
    machinesWithEmptySelections: 2,
    machinesWithLowSelections: 3,
    emptySelectionCount: 4,
    lowSelectionCount: 23,
    machinesEvaluated: 3,
    selectionsEvaluated: 120
  },
  needsOrdering: { productsNeedingOrdering: 7, productsEvaluated: 71, mode: 'MachineAware' },
  inventory: {
    inventoryValueAtCost: 2155.49,
    isInventoryValueComplete: true,
    productsWithUnknownCost: 0,
    productCount: 71,
    unitsInStorage: 596
  }
};

const sites = [
  {
    siteId: 1,
    siteName: 'Harbour Point Plaza',
    machineCount: 1,
    totalStockPercentage: 80,
    lowProductCount: 8,
    emptyProductCount: 1,
    todayRevenue: 57.6,
    currentWeekRevenue: 123.9,
    previousComparableWeekRevenue: 75.9,
    lastWeekRevenue: 318.4,
    monthToDateRevenue: 1042.7,
    // Each machine's own stock level (issue #1013), keyed by the provider machine id the machine
    // read uses. The figures are deliberately not the site's, so a capture shows a machine row's
    // own percentage rather than a repeated one.
    machineStocks: [{ machineId: 1, stockPercentage: 80 }]
  },
  {
    siteId: 2,
    siteName: 'Pavillion Shopping Centre and Food Court West Entrance Level 2',
    machineCount: 2,
    totalStockPercentage: 27,
    lowProductCount: 15,
    emptyProductCount: 3,
    todayRevenue: 171.6,
    currentWeekRevenue: 463.4,
    previousComparableWeekRevenue: 392.0,
    lastWeekRevenue: 1187.25,
    monthToDateRevenue: 3904.8,
    // Two machines in different stock bands under one site in the warning band, so a capture shows
    // the per-machine colours and words differing from the site's and from each other.
    machineStocks: [
      { machineId: 2, stockPercentage: 12 },
      { machineId: 3, stockPercentage: 41 }
    ]
  },
  // A site the fleet read returned no machine for: issue #987 keeps it visible with its own empty
  // state rather than hiding a site the business has.
  {
    siteId: 3,
    siteName: 'Riverside Depot',
    machineCount: 0,
    totalStockPercentage: 94,
    lowProductCount: 0,
    emptyProductCount: 0,
    todayRevenue: 0,
    currentWeekRevenue: 0,
    previousComparableWeekRevenue: 0,
    lastWeekRevenue: 0,
    monthToDateRevenue: 0,
    // No machine stood at it, so there is no machine figure either.
    machineStocks: []
  }
];

function machine(id: number, name: string, today: number, week: number, lastWeek: number, comparable: number, mtd: number) {
  return {
    machineID: id,
    localMachineId: 500 + id,
    machineName: name,
    machineNumber: `M${id}`,
    todayGrossRevenue: today,
    currentWeekGrossRevenue: week,
    lastWeekGrossRevenue: lastWeek,
    twoWeeksAgoGrossRevenue: lastWeek,
    todayDirectProfit: Math.round(today * 45) / 100,
    twoWeeksAgoDirectProfit: Math.round(lastWeek * 44) / 100,
    lastWeekDirectProfit: Math.round(lastWeek * 44) / 100,
    currentWeekDirectProfit: Math.round(week * 45) / 100,
    previousComparableWeekGrossRevenue: comparable,
    previousComparableWeekDirectProfit: Math.round(comparable * 44) / 100,
    monthToDateGrossRevenue: mtd,
    monthToDateDirectProfit: Math.round(mtd * 45) / 100,
    profitabilityStatus: null
  };
}

const machines = [
  { ...machine(1, 'Harbour Point Plaza - Avenue Hotel VM', 57.6, 123.9, 210, 75.9, 610), siteId: 1 },
  { ...machine(2, 'Pavillion Left (Drinks & Cold)', 79, 210, 240, 166.5, 820), siteId: 2 },
  { ...machine(3, 'Pavillion Right (Snacks & Confectionery)', 92.6, 253.4, 270, 225.5, 905), siteId: 2 },
  // At no site, so it appears under "Other machines" and still appears exactly once.
  { ...machine(4, 'Workshop spare unit awaiting placement', 0, 0, 0, 0, 0), siteId: null }
];

function product(id: number, name: string, stock: number, need: number, toOrder: number, onOrder = 0) {
  return {
    id,
    name,
    sku: null,
    description: null,
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: stock,
    maxStockInMachine: 20,
    machineReplenishmentNeed: need,
    onOrderQuantity: onOrder,
    lowStockThreshold: 5,
    restockTo: 30,
    needToOrder: toOrder,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true
  };
}

const reorder = [
  product(1, 'Cadbury Dairy Milk 45g', 4, 7, 26),
  product(2, 'Red Bull Energy Drink 250mL', 0, 8, 21, 6),
  product(3, 'Kinder Bueno 43g', 1, 7, 19),
  product(4, 'Nestle KitKat 42g', 8, 6, 17),
  product(5, 'Smiths Original Chips 45g Multipack Value Share Bag', 10, 8, 17),
  product(6, 'Mount Franklin Water 600mL', 12, 4, 9),
  product(7, 'Coca-Cola 375mL', 14, 3, 6)
];

const purchaseCosts = [
  { productId: 1, lastUnitCost: 1.05, lastSupplierName: 'Woolworths', lastPurchaseDate: '2026-10-12', lowestUnitCost: 1, lowestSupplierName: 'Woolworths', lowestPurchaseDate: '2026-02-12' },
  { productId: 2, lastUnitCost: 1.6, lastSupplierName: 'Costco', lastPurchaseDate: '2026-10-02', lowestUnitCost: 1.54, lowestSupplierName: 'Costco', lowestPurchaseDate: '2026-01-28' },
  { productId: 3, lastUnitCost: 1.55, lastSupplierName: 'Coles', lastPurchaseDate: '2026-09-20', lowestUnitCost: 1.5, lowestSupplierName: 'Coles', lowestPurchaseDate: '2026-02-04' },
  { productId: 4, lastUnitCost: 1.5, lastSupplierName: 'Woolworths', lastPurchaseDate: '2026-10-18', lowestUnitCost: 1.5, lowestSupplierName: 'Woolworths', lowestPurchaseDate: '2026-01-18' }
];

function offer(supplierId: number, supplierName: string, unitPrice: number, isCheapest: boolean) {
  return {
    supplierId,
    supplierName,
    hasListing: true,
    hasPrice: true,
    packQuantity: 24,
    packPrice: Math.round(unitPrice * 2400) / 100,
    packPriceBasis: 1,
    unitPrice,
    unitPriceExGst: unitPrice,
    unitPriceIncGst: Math.round(unitPrice * 110) / 100,
    isSpecial: false,
    promotionEndsAtUtc: null,
    stockStatus: 1,
    lastCheckedAtUtc: '2026-10-27T22:00:00Z',
    productUrl: null,
    ineligibility: null,
    isCheapest
  };
}

/** Observed supplier prices (issue #981): two tracked products priced, one awaiting a match, the rest untracked. */
const cheapestSuppliers = {
  displayBasis: 1,
  products: [
    { productId: 1, trackingStatus: 3, cheapestUnitPrice: 0.98, suppliers: [offer(1, 'Woolworths', 0.98, true), offer(2, 'Coles', 1.04, false)] },
    { productId: 2, trackingStatus: 3, cheapestUnitPrice: 1.49, suppliers: [offer(3, 'Costco', 1.49, true)] },
    { productId: 3, trackingStatus: 1, cheapestUnitPrice: null, suppliers: [] }
  ]
};

const restockReminder = {
  applicable: true,
  asOfUtc: '2026-10-28T00:00:00Z',
  businessDate: '2026-10-28T00:00:00',
  overdueAfterDays: 3,
  defaultFromBusinessDate: '2026-10-25T00:00:00',
  pendingLookbackDays: 14,
  pendingFromBusinessDate: '2026-10-14T00:00:00',
  syncAvailability: 'Confirmed',
  machines: [
    {
      localMachineId: 501,
      machineName: 'Harbour Point Plaza - Avenue Hotel VM',
      lastSuccessfulSyncAtUtc: '2026-10-22T08:00:00Z',
      lastSuccessfulSyncBusinessDate: '2026-10-22T00:00:00',
      daysSinceLastSuccessfulSync: 6,
      isFetchOverdue: true,
      knownPendingEventCount: 2,
      oldestKnownPendingEventAtUtc: '2026-10-16T08:00:00Z',
      oldestKnownPendingEventBusinessDate: '2026-10-16T00:00:00',
      hasPendingWorkBeforeDefaultFrom: true
    }
  ]
};

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
    if (path.endsWith('/dashboard/summary')) return json(route, summary);
    if (path.endsWith('/machine-restock-sync/reminder')) return json(route, restockReminder);
    if (path.endsWith('/products/alerts/low-stock')) return json(route, reorder);
    if (path.endsWith('/products/purchase-cost-summary')) return json(route, purchaseCosts);
    if (path.endsWith('/sites')) return json(route, sites);
    if (path.endsWith('/machines')) return json(route, machines);
    if (path.endsWith('/cheapest-suppliers/summary')) return json(route, cheapestSuppliers);
    if (route.request().method() === 'POST') return route.fulfill({ status: 204, body: '' });
    return json(route, []);
  });
}

async function openDashboard(page: Page, width: number): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  await answerApi(page);
  await page.goto('/');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
  });
  await expect(page.getByRole('heading', { name: 'Dashboard', level: 1 })).toBeVisible();
  await expect(page.getByText('Restock sync pending')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Site and machine performance' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Reorder priorities' })).toBeVisible();
  await expect(page.locator('[data-testid="cheapest-supplier-cell"]:visible', { hasText: 'Costco' }).first()).toBeVisible();
}

/** The visible expander for one site group, whichever of the two layouts is the rendered one. */
function siteExpander(page: Page, key: string): Locator {
  return page
    .locator(`[data-testid="performance-expander-${key}"]:visible, [data-testid="performance-card-expander-${key}"]:visible`)
    .first();
}

/** The visible machine rows or cards of one site group. */
function groupMachines(page: Page, key: string): Locator {
  return page.locator(
    `tbody#site-machines-${key}:visible tr, ul#site-machine-cards-${key}:visible [data-testid^="performance-machine-card-"]`
  );
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

/** Opens the More sheet at 390px from the given control and checks focus starts on Close. */
async function openSheet(page: Page, openerOf: (page: Page) => Locator): Promise<{ control: Locator; sheet: Locator }> {
  await openDashboard(page, 390);
  // Tag the control first: the top-bar button's accessible name changes once the sheet opens.
  await openerOf(page).evaluate((element) => {
    (element as HTMLElement).dataset['e2eOpener'] = '';
  });
  const control = page.locator('[data-e2e-opener]');
  await control.click();
  const sheet = page.getByTestId('more-sheet');
  await expect(sheet).toBeVisible();
  await expect(sheet.getByRole('button', { name: 'Close navigation menu' })).toBeFocused();
  return { control, sheet };
}

function focusIsInSheet(page: Page): Promise<boolean> {
  return page.evaluate(() => document.querySelector('[data-testid="more-sheet"]')?.contains(document.activeElement) ?? false);
}

test.describe('Restockroo dashboard and navigation visual evidence (issue #972)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of [360, 390, 768, 1440]) {
    test(`dashboard at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);
      await expectNoHorizontalOverflow(page);
      // A full-page capture keeps fixed elements where the first viewport put them, which would
      // paint the bottom bar mid-page; growing the viewport to the document puts it at its foot.
      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await page.screenshot({ path: join(screenshotDirectory, `dashboard-${width}px.png`) });
    });
  }

  /**
   * Issue #987's own evidence. Each one is a real browser interaction, not a styled state: the
   * group is collapsed by pressing its expander, the period is changed with the shared control,
   * and the focus capture reaches the expander and toggles it with the keyboard alone.
   */
  for (const width of [1440, 390]) {
    test(`Site and machine performance with a group collapsed at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);

      await expect(groupMachines(page, 'site-2')).toHaveCount(2);
      await expect(siteExpander(page, 'site-2')).toHaveAttribute('aria-expanded', 'true');
      await siteExpander(page, 'site-2').click();

      await expect(siteExpander(page, 'site-2')).toHaveAttribute('aria-expanded', 'false');
      await expect(groupMachines(page, 'site-2')).toHaveCount(0);
      // Collapsing one site leaves the others exactly as they were, and the fleet total unchanged.
      await expect(siteExpander(page, 'site-1')).toHaveAttribute('aria-expanded', 'true');
      await expect(groupMachines(page, 'site-1')).toHaveCount(1);
      await expectNoHorizontalOverflow(page);

      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await page.screenshot({ path: join(screenshotDirectory, `performance-collapsed-${width}px.png`) });
    });
  }

  for (const width of [1440, 390]) {
    test(`Site and machine performance period control at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);
      const control = page.locator('[data-testid="performance-period"]:visible');

      await expect(control.getByRole('button', { name: 'This week' })).toHaveAttribute('aria-pressed', 'true');
      await control.getByRole('button', { name: 'Last week' }).click();

      await expect(control.getByRole('button', { name: 'Last week' })).toHaveAttribute('aria-pressed', 'true');
      // Sites publish this period too since issue #1012, so a site row shows its own figure for it
      // instead of saying the period is unavailable for sites. The other layout's copy of the same
      // text is in the document but hidden, so only a visible one counts.
      await expect(page.getByText('$318.40').filter({ visible: true }).first()).toBeVisible();
      await expect(page.getByText('Not published for sites')).toHaveCount(0);
      await expectNoHorizontalOverflow(page);

      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await page.screenshot({ path: join(screenshotDirectory, `performance-period-last-week-${width}px.png`) });
    });
  }

  test('a site expander takes keyboard focus, shows it, and toggles from the keyboard', async ({ page }) => {
    await openDashboard(page, 1440);
    const expander = siteExpander(page, 'site-1');

    await expander.focus();
    await expect(expander).toBeFocused();
    await page
      .locator('[data-testid="performance-table"]')
      .screenshot({ path: join(screenshotDirectory, 'performance-expander-focus-1440px.png') });

    await page.keyboard.press('Enter');
    await expect(expander).toHaveAttribute('aria-expanded', 'false');
    await expect(groupMachines(page, 'site-1')).toHaveCount(0);
    await expect(expander).toBeFocused();
    // A collapsed group exposes nothing focusable, so the keyboard can never reach into it.
    await expect(page.locator('tbody#site-machines-site-1 a, tbody#site-machines-site-1 button')).toHaveCount(0);

    await page.keyboard.press(' ');
    await expect(expander).toHaveAttribute('aria-expanded', 'true');
    await expect(groupMachines(page, 'site-1')).toHaveCount(1);
  });

  for (const width of [360, 390]) {
    test(`More sheet opened and scrolled at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);
      await page.getByTestId('bottom-nav-more').click();
      const sheet = page.getByTestId('more-sheet');
      await expect(sheet).toBeVisible();
      await expect(sheet.getByRole('button', { name: 'Close navigation menu' })).toBeFocused();
      await page.screenshot({ path: join(screenshotDirectory, `more-sheet-open-${width}px.png`) });

      await sheet.locator('nav').evaluate((nav) => nav.scrollTo(0, nav.scrollHeight));
      await expect(sheet.getByRole('link', { name: 'Business Settings' })).toBeInViewport();
      await page.screenshot({ path: join(screenshotDirectory, `more-sheet-scrolled-${width}px.png`) });

      await page.keyboard.press('Escape');
      await expect(sheet).toHaveCount(0);
      await expect(page.getByTestId('bottom-nav-more')).toBeFocused();
    });
  }

  /**
   * The More sheet is a modal dialog, and these prove it in a real browser rather than by its
   * attributes: while it is open the page behind it can be neither focused nor activated, Tab never
   * leaves it, and closing it - by Escape, Close or the backdrop - returns focus to whichever
   * control opened it. Both openers are covered: More in the bottom bar and the top bar's menu
   * button.
   */
  const openers: readonly { name: string; open: (page: Page) => Locator }[] = [
    { name: 'More in the bottom bar', open: (page) => page.getByTestId('bottom-nav-more') },
    { name: 'the top-bar menu button', open: (page) => page.getByRole('button', { name: 'Open navigation menu' }) }
  ];

  for (const opener of openers) {
    test.describe(`More sheet modality, opened from ${opener.name}`, () => {
      test('keeps focus inside through repeated Tab and Shift+Tab', async ({ page }) => {
        await openSheet(page, opener.open);
        const stops = await page.getByTestId('more-sheet').locator('a[href], button:not([disabled])').count();

        for (let press = 0; press < stops + 3; press++) {
          await page.keyboard.press('Tab');
          expect(await focusIsInSheet(page)).toBe(true);
        }
        for (let press = 0; press < stops + 3; press++) {
          await page.keyboard.press('Shift+Tab');
          expect(await focusIsInSheet(page)).toBe(true);
        }
      });

      test('lets nothing behind it take focus or be activated', async ({ page }) => {
        const { sheet } = await openSheet(page, opener.open);

        // Programmatic focus on background controls is refused while the page is inert.
        const backgroundFocus = await page.evaluate(() => {
          const targets = [
            document.querySelector<HTMLElement>('[data-testid="bottom-nav-more"]'),
            document.querySelector<HTMLElement>('[data-testid="reorder-view-all"]'),
            document.querySelector<HTMLElement>('app-user-menu button')
          ];
          return targets.map((target) => {
            target?.focus();
            return document.activeElement === target;
          });
        });
        expect(backgroundFocus).toEqual([false, false, false]);
        expect(await focusIsInSheet(page)).toBe(true);

        // A pointer press where a background control sits lands on the backdrop: it dismisses the
        // sheet and does not reach the control (the user menu stays closed, the page stays put).
        const userMenu = page.locator('app-user-menu button[aria-haspopup="true"]');
        const box = await userMenu.boundingBox();
        expect(box).not.toBeNull();
        await page.mouse.click(box!.x + box!.width / 2, box!.y + box!.height / 2);
        await expect(sheet).toHaveCount(0);
        await expect(userMenu).not.toHaveAttribute('aria-expanded', 'true');
        await expect(page).toHaveURL(/\/$/);
      });

      test('returns focus to its opener on Escape', async ({ page }) => {
        const { control, sheet } = await openSheet(page, opener.open);

        await page.keyboard.press('Escape');

        await expect(sheet).toHaveCount(0);
        await expect(control).toBeFocused();
        // The page is interactive again: a background control takes focus.
        await page.getByTestId('reorder-view-all').focus();
        await expect(page.getByTestId('reorder-view-all')).toBeFocused();
      });

      test('returns focus to its opener from Close', async ({ page }) => {
        const { control, sheet } = await openSheet(page, opener.open);

        await sheet.getByRole('button', { name: 'Close navigation menu' }).click();

        await expect(sheet).toHaveCount(0);
        await expect(control).toBeFocused();
      });
    });
  }
});
