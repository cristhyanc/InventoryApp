import { Locator, Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence for issue #972: the Restockroo Dashboard and authenticated navigation at
 * 360, 390, 768 and 1440px, plus the narrow layout's More sheet opened and scrolled to its end. The
 * dashboard captures also show issue #987's combined **Site and machine performance** card - each
 * site expanding to the machines standing at it, with issue #1013's own stock level and issue
 * #1019's own low and empty counts on every machine row - and issue #981's Cheapest supplier column
 * in Reorder priorities.
 *
 * Like the other visual runs this starts no API and no database. Every `/api/` request is answered
 * in the browser with neutral synthetic figures - an Owner's capabilities, a connected business,
 * three sites (one with a deliberately long name and one with no machines), four machines, one of
 * them at no site, seven reorder products, observed supplier prices for three of them and one
 * pending restock sync - so no business data, account name or credential can appear in a capture.
 *
 * Each width also asserts the page has no horizontal overflow, which is the issue's layout rule.
 * Issue #1015's evidence is here - the performance card's explanation opened from the one help
 * button beside its title at all four widths, measured beside the button, in the top layer, inside
 * the viewport and leaving the table where it was, plus the keyboard focus ring on that button -
 * and issue #1016's - the Cheapest supplier help panel opened from the desktop
 * heading at 1440px and from a narrow-screen card at 360px - and issue #1017's: Reorder priorities
 * is paged five rows to a page, so the seven reorder products give two pages, and the second page
 * is captured at 1440 and 390px after turning it with the pager. Issue #1020's **Storage after
 * refill** column is measured here too - its position beside Machine need, its positive, zero and
 * negative values, its column alignment on the desktop table, the matching narrow-screen card entry
 * and its withholding in storage-only mode, which the synthetic reorder read declares with the same
 * `X-Reorder-Mode` header the API publishes. Issue #987's own interaction evidence
 * is here as well: a collapsed group at 1440 and 390px, the
 * shared period control switched to Last week, and the keyboard focus ring on a site expander with
 * the group toggled from the keyboard alone. Issue #1080's **Items per page** selector is measured
 * here too, at both widths the issue names: every offered size applied to a deliberately long
 * synthetic list, the pager's range and navigation moving with it, a size change returning to page
 * 1, a one-page list keeping the selector while Previous and Next go away, the selector reached and
 * operated from the keyboard alone with the ring the browser actually paints on it, and the page
 * never scrolling sideways at any of it. The More sheet's modality is exercised in the browser
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
    // Each machine's own stock level (issue #1013) and its own low/empty counts (issue #1019),
    // keyed by the provider machine id the machine read uses. The figures are deliberately not the
    // site's, so a capture shows a machine row's own percentage and counts rather than repeated
    // ones. This site stands one machine, and a one-machine site groups each product exactly as
    // that machine does, so its counts legitimately coincide with it here; the two-machine site
    // below is where the figures differ.
    machineStocks: [{ machineId: 1, stockPercentage: 80, lowProductCount: 8, emptyProductCount: 1 }]
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
    // the per-machine colours and words differing from the site's and from each other. Their
    // low/empty counts differ from the site's 15 and 3 as well (issue #1019): a product low on both
    // machines is two machine alerts and one site alert, so the machine counts sum to more than the
    // site's rather than adding up to it.
    machineStocks: [
      { machineId: 2, stockPercentage: 12, lowProductCount: 11, emptyProductCount: 2 },
      { machineId: 3, stockPercentage: 41, lowProductCount: 7, emptyProductCount: 1 }
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

/**
 * Seven reorder products, so issue #1017's pager has two pages. Their storage stock and machine need
 * also give the first page all three shapes of issue #1020's **Storage after refill**: a shortfall
 * (4 - 7 = -3, 0 - 8 = -8, 1 - 7 = -6), exactly nothing left (8 - 8 = 0, the KitKat, whose machine
 * need is deliberately equal to its storage stock) and a remainder (10 - 8 = 2). Page 2 keeps two
 * more remainders (12 - 4 = 8 and 14 - 3 = 11).
 */
const reorder = [
  product(1, 'Cadbury Dairy Milk 45g', 4, 7, 26),
  product(2, 'Red Bull Energy Drink 250mL', 0, 8, 21, 6),
  product(3, 'Kinder Bueno 43g', 1, 7, 19),
  product(4, 'Nestle KitKat 42g', 8, 8, 17),
  product(5, 'Smiths Original Chips 45g Multipack Value Share Bag', 10, 8, 17),
  product(6, 'Mount Franklin Water 600mL', 12, 4, 9),
  product(7, 'Coca-Cola 375mL', 14, 3, 6)
];

/**
 * How long the synthetic reorder list is when a test asks for the long one (issue #1080). It is
 * deliberately more than the largest offered page size, so **every** one of 5, 10, 20, 50 and 100
 * leaves more than one page - the pager always has somewhere to go - and so the last page at 50 and
 * at 100 is a short one whose range has to be reported correctly.
 */
const LONG_REORDER_COUNT = 120;

/**
 * The same seven products the rest of this file measures, followed by generated ones up to
 * {@link LONG_REORDER_COUNT}. Keeping the first seven identical is what lets the long list reuse
 * every shared assertion in `openDashboard` - the Costco supplier cell among them - while giving the
 * page sizes a list worth paging.
 */
const longReorder = [
  ...reorder,
  ...Array.from({ length: LONG_REORDER_COUNT - reorder.length }, (_, index) => {
    const id = reorder.length + index + 1;
    return product(id, `Synthetic reorder product ${id}`, 2, 6, 24);
  })
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

function json(route: Route, body: unknown, headers: Record<string, string> = {}): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body), headers });
}

/**
 * The additive response headers a storage-only reorder read carries (issues #726 and #727): the mode
 * and why the machines were not read. They are the only thing that puts the Dashboard in that mode,
 * so the fixture declares it exactly as the API does rather than by changing the body.
 */
const STORAGE_ONLY_REORDER_HEADERS = {
  'X-Reorder-Mode': 'StorageOnly',
  'X-Reorder-Machine-Need-Unavailable-Reason': 'NayaxNotConnected'
};

async function answerApi(
  page: Page,
  options: { storageOnlyReorder?: boolean; longReorderList?: boolean } = {}
): Promise<void> {
  const reorderList = options.longReorderList ? longReorder : reorder;
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
    if (path.endsWith('/products/alerts/low-stock')) {
      return json(route, reorderList, options.storageOnlyReorder ? STORAGE_ONLY_REORDER_HEADERS : {});
    }
    if (path.endsWith('/products/purchase-cost-summary')) return json(route, purchaseCosts);
    if (path.endsWith('/sites')) return json(route, sites);
    if (path.endsWith('/machines')) return json(route, machines);
    if (path.endsWith('/cheapest-suppliers/summary')) return json(route, cheapestSuppliers);
    if (route.request().method() === 'POST') return route.fulfill({ status: 204, body: '' });
    return json(route, []);
  });
}

async function openDashboard(
  page: Page,
  width: number,
  options: { failSitesRead?: boolean; storageOnlyReorder?: boolean; longReorderList?: boolean } = {}
): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  await answerApi(page, options);
  if (options.failSitesRead) {
    // Registered after the catch-all so it is the handler that wins: Playwright offers a request to
    // the most recently added matching route first. The machine read still succeeds, which is the
    // state issue #1014 cares about - every machine falls back to the one unheaded group.
    await page.route('**/api/sites', (route: Route) =>
      route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'Sites are unavailable.' }) })
    );
  }
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

/** The site's own name inside whichever layout's expander is the rendered one. */
function siteName(page: Page, key: string): Locator {
  return siteExpander(page, key).locator('span.font-semibold');
}

/** Where an element's box actually starts on the page, in CSS pixels. */
async function leftEdge(locator: Locator): Promise<number> {
  return (await box(locator)).x;
}

/** An element's rendered box, which has to exist before anything about it can be measured. */
async function box(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const measured = await locator.boundingBox();
  expect(measured, 'an element has to be laid out before it can be measured').not.toBeNull();
  return measured!;
}

/** The Reorder priorities card, which holds both the desktop table and the narrow-screen cards. */
function reorderSection(page: Page): Locator {
  return page.locator('section[aria-labelledby="reorder-priorities-title"]');
}

/**
 * The desktop table's column headings, in rendered order, lower-cased. `innerText` is deliberately
 * the source: it is what the browser lays out, so a heading read here is one a reader can actually
 * see - which also means it comes back as `.table-head` paints it, in capitals, hence the folding.
 * It is only meaningful at a width where the table layout is the rendered one.
 */
async function reorderHeadings(page: Page): Promise<string[]> {
  return (await reorderSection(page).locator('thead th').allInnerTexts()).map((heading) =>
    heading.trim().toLowerCase()
  );
}

/** The narrow-screen reorder cards, which render the same page of rows as the table. */
function reorderCards(page: Page): Locator {
  return reorderSection(page).getByTestId('reorder-cards').locator('> li');
}

/**
 * The rows of the current reorder page in whichever of the two layouts this width actually paints -
 * the desktop table's rows above `md`, the phone cards below it. Both are in the document at every
 * width, so only the rendered one may be counted; that is also what makes one count an answer about
 * the shared page state rather than about one layout's copy of it.
 */
function visibleReorderRows(page: Page): Locator {
  return reorderSection(page).locator('table:visible tbody tr, [data-testid="reorder-cards"]:visible > li');
}

/** The "Items per page" selector of the one shared pager (issue #1080). */
function pageSizeSelect(page: Page): Locator {
  return reorderSection(page).getByTestId('reorder-page-size');
}

/** Its visible label, found the way an assistive technology finds it: by the `for` association. */
function pageSizeLabel(page: Page): Locator {
  return reorderSection(page).locator('label[for="reorder-page-size"]');
}

/** The one shared pager row, which holds the range output, the selector and Previous/Next. */
function reorderPagination(page: Page): Locator {
  return reorderSection(page).getByTestId('reorder-pagination');
}

/**
 * The focus ring the browser is actually painting on an element right now. Taken from the computed
 * style of the live element rather than from the stylesheet, so a `:focus-visible` rule that never
 * matches - or one some later rule removes - reads as the absence this returns for an unfocused
 * control.
 */
function focusRing(element: Locator): Promise<{ style: string; width: string; offset: string; colour: string }> {
  return element.evaluate((node) => {
    const painted = getComputedStyle(node);
    return {
      style: painted.outlineStyle,
      width: painted.outlineWidth,
      offset: painted.outlineOffset,
      colour: painted.outlineColor
    };
  });
}

/** A card's value for one labelled entry, taken from the `dd` beside that `dt`. */
function cardValue(card: Locator, label: string): Locator {
  return card.locator(`dt:text-is("${label}") + dd`);
}

/** Whether an element carries one class, asked of the rendered element rather than of the template. */
function hasClass(element: Locator, className: string): Promise<boolean> {
  return element.evaluate((node, name) => node.classList.contains(name), className);
}

/** The colour the browser actually paints an element's text in. */
function paintedColour(element: Locator): Promise<string> {
  return element.evaluate((node) => getComputedStyle(node).color);
}

/** The card title's one help button (issue #1015), in whichever layout is the rendered one. */
function performanceHelpButton(page: Page): Locator {
  return page
    .locator('section[aria-labelledby="site-machine-performance-title"] button[aria-label="About these figures"]:visible')
    .first();
}

/**
 * Where the help button actually sits (issue #1015): immediately after the words "Site and machine
 * performance", on every width. The heading's *last line* is measured with a `Range` rather than
 * the heading's box, because at 360px the title wraps - and a button beside the box rather than
 * beside the words would land at the far end of the header, which is exactly what the criterion
 * rules out and what no class assertion could tell apart.
 */
function measureHelpButtonPlacement(page: Page): Promise<{ onTitleLastLine: boolean; gapAfterLastWord: number }> {
  return page.evaluate(() => {
    const heading = document.querySelector('#site-machine-performance-title') as HTMLElement;
    const button = document.querySelector('button[aria-label="About these figures"]') as HTMLElement;
    const text = document.createRange();
    text.selectNodeContents(heading);
    const lines = Array.from(text.getClientRects());
    const lastLine = lines.at(-1)!;
    const box = button.getBoundingClientRect();

    return {
      onTitleLastLine: box.top + box.height / 2 >= lastLine.top && box.top + box.height / 2 <= lastLine.bottom,
      gapAfterLastWord: Math.round(box.left - lastLine.right)
    };
  });
}

/**
 * What issue #1015 asks of the open help panel, measured inside the page so every figure is
 * viewport-relative and therefore comparable: it stands beside its trigger rather than over it, it
 * is wholly inside the viewport, and over its whole extent the element the browser hit-tests to is
 * the panel - which is what "escapes clipping" means for a card with `overflow-hidden`, and what
 * nothing but a real browser's top layer can answer.
 *
 * The probe points are the panel's centre and the middle of each of its four edges rather than its
 * corners, because the panel is rounded: a point inside the bounding box at a corner is outside
 * the painted shape, and would say "clipped" about a radius.
 */
function measureHelpPanel(page: Page): Promise<{
  coversTrigger: boolean;
  insideViewport: boolean;
  onTopThroughout: boolean;
}> {
  return page.evaluate(() => {
    const panel = document.querySelector('[data-testid="help-toggletip-panel"]') as HTMLElement;
    const trigger = document.querySelector(
      'button[aria-label="About these figures"][aria-expanded="true"]'
    ) as HTMLElement;
    const box = panel.getBoundingClientRect();
    const button = trigger.getBoundingClientRect();
    const inset = 2;
    const points: [number, number][] = [
      [box.left + box.width / 2, box.top + box.height / 2],
      [box.left + box.width / 2, box.top + inset],
      [box.left + box.width / 2, box.bottom - inset],
      [box.left + inset, box.top + box.height / 2],
      [box.right - inset, box.top + box.height / 2]
    ];

    return {
      coversTrigger:
        box.left < button.right && box.right > button.left && box.top < button.bottom && box.bottom > button.top,
      insideViewport:
        box.left >= 0 && box.top >= 0 && box.right <= window.innerWidth && box.bottom <= window.innerHeight,
      onTopThroughout: points.every(
        ([x, y]) => document.elementFromPoint(x, y)?.closest('[data-testid="help-toggletip-panel"]') != null
      )
    };
  });
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
   * Issue #1016's own evidence: the Cheapest supplier explanation opened from its help button -
   * the desktop heading's at 1440px and a narrow-screen card's at 360px. It is a real interaction
   * too, and it is what shows the panel standing beside the button in the browser's top layer,
   * clipped by neither the card nor the scrolling table and inside the narrow viewport.
   */
  for (const width of [1440, 360]) {
    test(`Cheapest supplier help panel open at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);
      await page.locator('section[aria-labelledby="reorder-priorities-title"]').scrollIntoViewIfNeeded();
      await page.locator('button[aria-label="About Cheapest supplier"]:visible').first().click();

      const panel = page.locator('[data-testid="help-toggletip-panel"]');
      await expect(panel).toBeVisible();
      await expect(panel).toHaveText(/not costs you have paid\.$/);
      await expectNoHorizontalOverflow(page);
      // The viewport, not the document: the panel is in the top layer, which a full-page capture
      // stitched from a taller viewport would move away from the button it is anchored to.
      await page.screenshot({ path: join(screenshotDirectory, `reorder-supplier-help-${width}px.png`) });
    });
  }

  /**
   * Issue #1017's own evidence: Reorder priorities paged, five rows to a page. The seven synthetic
   * reorder products give exactly two pages, so page 2 is the partial one - "Showing 6-7 of 7
   * products", with Next at the end of the list and Previous now available. It is a real interaction
   * at both widths, which is also what shows the desktop table and the phone cards turning the one
   * shared page.
   */
  for (const width of [1440, 390]) {
    test(`Reorder priorities second page at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);
      await page.locator('section[aria-labelledby="reorder-priorities-title"]').scrollIntoViewIfNeeded();

      await expect(page.getByTestId('reorder-range')).toHaveText(/Showing 1.5 of 7 products/);
      await expect(page.getByTestId('reorder-previous-page')).toBeDisabled();
      await page.getByTestId('reorder-next-page').click();

      await expect(page.getByTestId('reorder-range')).toHaveText(/Showing 6.7 of 7 products/);
      await expect(page.getByTestId('reorder-page-indicator')).toHaveText(/Page 2 of 2/);
      await expect(page.getByTestId('reorder-next-page')).toBeDisabled();
      await expect(page.getByTestId('reorder-previous-page')).toBeEnabled();
      // The last two products of the list, and none of the first page's, in both layouts.
      await expect(page.getByText('Mount Franklin Water 600mL').filter({ visible: true })).toBeVisible();
      await expect(page.getByText('Coca-Cola 375mL').filter({ visible: true })).toBeVisible();
      await expect(page.getByText('Cadbury Dairy Milk 45g').filter({ visible: true })).toHaveCount(0);
      await expectNoHorizontalOverflow(page);

      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await page.screenshot({ path: join(screenshotDirectory, `reorder-page-2-${width}px.png`) });
    });
  }

  /**
   * Issue #1080's own evidence, at the two widths its layout criterion names, and the part no
   * jsdom test and no reading of the CSS can answer: the **Items per page** selector added to the
   * one shared pager row, every size it offers actually applied to a list long enough to page, and
   * the page measurably not scrolling sideways with the control in place at any of them.
   */
  const REORDER_PAGE_SIZES = [5, 10, 20, 50, 100] as const;

  for (const width of [1440, 390]) {
    test(`Reorder priorities page-size selector applies every offered size at ${width}px`, async ({ page }) => {
      await openDashboard(page, width, { longReorderList: true });
      await reorderSection(page).scrollIntoViewIfNeeded();

      const select = pageSizeSelect(page);
      const label = pageSizeLabel(page);
      // A label a reader can see, associated with the control rather than merely standing near it.
      await expect(label).toBeVisible();
      await expect(label).toHaveText('Items per page');
      expect(await label.getAttribute('for')).toBe(await select.getAttribute('id'));
      await expect(select).toBeVisible();
      await expect(select.locator('option')).toHaveText(['5', '10', '20', '50', '100']);
      // Five on arrival, which is the default the issue asks for on every visit.
      await expect(select).toHaveValue('5');

      const card = await box(reorderSection(page));
      for (const size of REORDER_PAGE_SIZES) {
        await select.selectOption(String(size));

        // The shared page, the range output and the navigation all move with the chosen size.
        await expect(select).toHaveValue(String(size));
        await expect(visibleReorderRows(page)).toHaveCount(size);
        await expect(page.getByTestId('reorder-range')).toHaveText(
          `Showing 1–${size} of ${LONG_REORDER_COUNT} products`
        );
        await expect(page.getByTestId('reorder-page-indicator')).toHaveText(
          `Page 1 of ${Math.ceil(LONG_REORDER_COUNT / size)}`
        );
        await expect(page.getByTestId('reorder-previous-page')).toBeDisabled();
        await expect(page.getByTestId('reorder-next-page')).toBeEnabled();

        // The layout rule, measured rather than argued: the page has nothing to scroll sideways,
        // the pager row itself has nothing to scroll sideways either - so the control reaches
        // everybody without dragging the row - and the control stays inside the card it is in.
        await expectNoHorizontalOverflow(page);
        expect(await reorderPagination(page).evaluate((node) => node.scrollWidth - node.clientWidth)).toBeLessThanOrEqual(0);
        const control = await box(select);
        expect(control.x).toBeGreaterThanOrEqual(card.x - 1);
        expect(control.x + control.width).toBeLessThanOrEqual(card.x + card.width + 1);
        expect(control.x + control.width).toBeLessThanOrEqual(width);
      }

      // The capture is taken at one of the chosen sizes rather than at the default, because what
      // the evidence has to show is a size the operator picked actually paging a long list. 20 of
      // 120 is the one that fits in a reviewable image at both widths.
      await select.selectOption('20');
      await expect(page.getByTestId('reorder-range')).toHaveText('Showing 1–20 of 120 products');
      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await reorderSection(page).screenshot({ path: join(screenshotDirectory, `reorder-page-size-${width}px.png`) });
    });
  }

  /**
   * Turning pages at a chosen size, and what a size change does from a later page: it is a fresh
   * view of the same loaded list, so it returns to page 1. The short last page at 50 is here too,
   * because its range is the longest line the pager row ever has to fit.
   */
  for (const width of [1440, 390]) {
    test(`Reorder priorities pages at a chosen size and returns to page 1 at ${width}px`, async ({ page }) => {
      await openDashboard(page, width, { longReorderList: true });
      await reorderSection(page).scrollIntoViewIfNeeded();

      await pageSizeSelect(page).selectOption('10');
      await expect(page.getByTestId('reorder-page-indicator')).toHaveText('Page 1 of 12');
      await page.getByTestId('reorder-next-page').click();
      await page.getByTestId('reorder-next-page').click();
      await expect(page.getByTestId('reorder-page-indicator')).toHaveText('Page 3 of 12');
      await expect(page.getByTestId('reorder-range')).toHaveText('Showing 21–30 of 120 products');
      await expect(page.getByTestId('reorder-previous-page')).toBeEnabled();

      await pageSizeSelect(page).selectOption('50');

      await expect(page.getByTestId('reorder-page-indicator')).toHaveText('Page 1 of 3');
      await expect(page.getByTestId('reorder-range')).toHaveText('Showing 1–50 of 120 products');
      await expect(page.getByTestId('reorder-previous-page')).toBeDisabled();
      await expect(visibleReorderRows(page)).toHaveCount(50);
      // The head of the list is back on screen, which is what "page 1" means for these rows.
      await expect(reorderSection(page).getByText('Cadbury Dairy Milk 45g').filter({ visible: true })).toBeVisible();
      await expectNoHorizontalOverflow(page);

      // The last page at that size is the short one, and it reports its own range.
      await page.getByTestId('reorder-next-page').click();
      await page.getByTestId('reorder-next-page').click();
      await expect(page.getByTestId('reorder-page-indicator')).toHaveText('Page 3 of 3');
      await expect(page.getByTestId('reorder-range')).toHaveText('Showing 101–120 of 120 products');
      await expect(visibleReorderRows(page)).toHaveCount(20);
      await expect(page.getByTestId('reorder-next-page')).toBeDisabled();
      await expect(pageSizeSelect(page)).toHaveValue('50');
      await expectNoHorizontalOverflow(page);
      expect(await reorderPagination(page).evaluate((node) => node.scrollWidth - node.clientWidth)).toBeLessThanOrEqual(0);

      await reorderPagination(page).screenshot({
        path: join(screenshotDirectory, `reorder-page-size-last-page-${width}px.png`)
      });
    });
  }

  /**
   * The one-page case the issue is explicit about: the selector has to stay, so a size that
   * currently fits the whole list on one page can be made smaller again, while Previous, Next and
   * the page indicator go away because there is nowhere to go. The default seven-product fixture is
   * the list, so this is also the capture that shows the card as an operator usually meets it.
   */
  for (const width of [1440, 390]) {
    test(`Reorder priorities keeps the page-size selector on a single page at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);
      await reorderSection(page).scrollIntoViewIfNeeded();
      await expect(page.getByTestId('reorder-page-indicator')).toHaveText('Page 1 of 2');

      await pageSizeSelect(page).selectOption('20');

      await expect(visibleReorderRows(page)).toHaveCount(7);
      await expect(page.getByTestId('reorder-range')).toHaveText('Showing 1–7 of 7 products');
      await expect(page.getByTestId('reorder-page-indicator')).toHaveCount(0);
      await expect(page.getByTestId('reorder-previous-page')).toHaveCount(0);
      await expect(page.getByTestId('reorder-next-page')).toHaveCount(0);
      // Still there, still labelled, and still the whole of the pager row's content.
      await expect(pageSizeSelect(page)).toBeVisible();
      await expect(pageSizeLabel(page)).toBeVisible();
      await expectNoHorizontalOverflow(page);

      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await reorderSection(page).screenshot({
        path: join(screenshotDirectory, `reorder-page-size-one-page-${width}px.png`)
      });
      await page.setViewportSize({ width, height: 900 });

      // And a smaller size can indeed be chosen again, which brings the navigation back.
      await pageSizeSelect(page).selectOption('5');
      await expect(visibleReorderRows(page)).toHaveCount(5);
      await expect(page.getByTestId('reorder-page-indicator')).toHaveText('Page 1 of 2');
      await expect(page.getByTestId('reorder-next-page')).toBeEnabled();
      await expectNoHorizontalOverflow(page);
    });
  }

  /**
   * Keyboard access and *visible* focus, which is the half of the criterion a markup assertion
   * cannot reach: the selector is arrived at by pressing keys rather than by calling `focus()`, the
   * ring the repository's one `:focus-visible` rule promises is the ring the browser is measurably
   * painting on it, that ring is not clipped away by the card it sits in, a key press alone changes
   * the size, and focus carries on into the pager instead of stopping on the control.
   */
  for (const width of [1440, 390]) {
    test(`the page-size selector is reached, shown and operated from the keyboard at ${width}px`, async ({ page }) => {
      await openDashboard(page, width, { longReorderList: true });
      await reorderSection(page).scrollIntoViewIfNeeded();
      const select = pageSizeSelect(page);

      // Nothing is painted around it while it is not focused, so the ring below is the focus ring.
      expect((await focusRing(select)).style).toBe('none');

      // Shift+Tab from Next walks back over the disabled Previous onto the selector: it is in the
      // page's tab sequence, not merely programmatically focusable.
      await page.getByTestId('reorder-next-page').focus();
      await page.keyboard.press('Shift+Tab');
      await expect(select).toBeFocused();

      expect(await focusRing(select)).toEqual({
        style: 'solid',
        width: '2px',
        offset: '2px',
        colour: 'rgb(26, 115, 232)'
      });
      // A ring at 2px offset needs 4px of room on each side, and the card is `overflow-hidden`:
      // measured here so a ring the card clips can never pass as a visible one.
      const card = await box(reorderSection(page));
      const control = await box(select);
      expect(control.x - 4).toBeGreaterThanOrEqual(card.x);
      expect(control.x + control.width + 4).toBeLessThanOrEqual(card.x + card.width);
      expect(control.y - 4).toBeGreaterThanOrEqual(card.y);
      await reorderPagination(page).screenshot({
        path: join(screenshotDirectory, `reorder-page-size-focus-${width}px.png`)
      });

      // One key press moves to the next offered size and applies it, with no pointer involved.
      await page.keyboard.press('ArrowDown');
      await expect(select).toHaveValue('10');
      await expect(visibleReorderRows(page)).toHaveCount(10);
      await expect(page.getByTestId('reorder-page-indicator')).toHaveText('Page 1 of 12');
      await expect(page.getByTestId('reorder-range')).toHaveText('Showing 1–10 of 120 products');

      // Focus is not trapped on it: Tab carries on into the pager it stands beside.
      await page.keyboard.press('Tab');
      await expect(page.getByTestId('reorder-next-page')).toBeFocused();
      await expectNoHorizontalOverflow(page);
    });
  }

  /**
   * Issue #1020's own evidence on the desktop table, and the part of it jsdom cannot answer: where
   * the **Storage after refill** column actually lands, that each of its cells is laid out in that
   * column rather than merely written after the Machine need cell in the markup, and that a
   * shortfall is painted in a different colour from a remainder. The first page carries all three
   * shapes of the figure - three shortfalls, exactly nothing left, and a remainder - and the pager
   * still reports the same seven products over two pages.
   */
  test('Storage after refill is laid out beside Machine need and shows its three shapes at 1440px', async ({ page }) => {
    await openDashboard(page, 1440);
    await reorderSection(page).scrollIntoViewIfNeeded();

    const headings = await reorderHeadings(page);
    const column = headings.indexOf('storage after refill');
    expect(column).toBeGreaterThan(0);
    expect(column).toBe(headings.indexOf('machine need') + 1);
    expect(column).toBe(headings.indexOf('need to order') - 1);
    // The same locator the storage-only run asserts nothing for, so that run cannot pass vacuously.
    await expect(reorderSection(page).getByText('Storage after refill', { exact: true })).not.toHaveCount(0);
    await expect(reorderSection(page).getByText('Machine need', { exact: true })).not.toHaveCount(0);

    const heading = reorderSection(page).locator('thead th').nth(column);
    const headingBox = await box(heading);
    const machineNeed = await box(reorderSection(page).locator('thead th').nth(column - 1));
    const needToOrder = await box(reorderSection(page).locator('thead th').nth(column + 1));
    // Laid out between the two columns the issue names, not only listed between them.
    expect(headingBox.x).toBeGreaterThanOrEqual(machineNeed.x + machineNeed.width - 1);
    expect(headingBox.x + headingBox.width).toBeLessThanOrEqual(needToOrder.x + 1);

    const rows = reorderSection(page).locator('tbody tr');
    const expected = ['-3', '-8', '-6', '0', '2'];
    await expect(rows).toHaveCount(expected.length);
    const colours: Record<string, string[]> = { shortfall: [], remainder: [] };
    for (const [index, value] of expected.entries()) {
      const cell = rows.nth(index).locator('td').nth(column);
      await expect(cell).toHaveText(value);
      // A shortfall is signed and styled; zero and a remainder are plain numbers.
      const isShortfall = value.startsWith('-');
      expect(await hasClass(cell, 'value-negative')).toBe(isShortfall);
      colours[isShortfall ? 'shortfall' : 'remainder'].push(await paintedColour(cell));

      // Every cell sits in the heading's own column, which is what "aligned" means once the table
      // has laid itself out, and its number is inside the column rather than spilling out of it.
      const cellBox = await box(cell);
      expect(cellBox.x).toBeCloseTo(headingBox.x, 0);
      expect(cellBox.width).toBeCloseTo(headingBox.width, 0);
      expect(await cell.evaluate((node) => node.scrollWidth - node.clientWidth)).toBeLessThanOrEqual(0);
    }
    // One styling for a shortfall, one for everything else, and they are not the same colour.
    expect(new Set(colours.shortfall).size).toBe(1);
    expect(new Set(colours.remainder).size).toBe(1);
    expect(colours.shortfall[0]).not.toBe(colours.remainder[0]);

    // The widened table still fits inside its card at 1440px: its horizontal scroll container has
    // nothing to scroll, so the added column reaches nobody only by dragging the table sideways.
    const tableOverflow = await reorderSection(page)
      .locator('div.overflow-x-auto:has(table)')
      .evaluate((node) => node.scrollWidth - node.clientWidth);
    expect(tableOverflow).toBeLessThanOrEqual(0);

    // The extra column changed neither the paging nor the figures beside it.
    await expect(page.getByTestId('reorder-range')).toHaveText(/Showing 1.5 of 7 products/);
    await expect(rows.first().locator('td').nth(column - 1)).toHaveText('7');
    await expect(rows.first().locator('td').nth(column + 1)).toContainText('26');
    await expectNoHorizontalOverflow(page);
  });

  /**
   * The same figure on the narrow-screen cards (issue #1020), where the question a browser answers
   * is whether the added entry fits: the card's two-column grid puts it on its own row under
   * Storage stock, aligned with it, with its value inside the card and inside its own box, and the
   * page still does not scroll sideways at either phone width.
   */
  for (const width of [360, 390]) {
    test(`Storage after refill on the reorder cards at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);
      await reorderSection(page).scrollIntoViewIfNeeded();
      await expect(reorderCards(page)).toHaveCount(5);

      const first = reorderCards(page).first();
      const labels = (await first.locator('dt').allInnerTexts()).map((label) => label.trim());
      expect(labels.indexOf('Storage after refill')).toBe(labels.indexOf('Machine need') + 1);

      // The shortfall, exactly nothing left, and a remainder, on the cards that carry each.
      await expect(cardValue(first, 'Storage after refill')).toHaveText('-3 units');
      expect(await hasClass(cardValue(first, 'Storage after refill'), 'value-negative')).toBe(true);
      await expect(cardValue(reorderCards(page).nth(3), 'Storage after refill')).toHaveText('0 units');
      expect(await hasClass(cardValue(reorderCards(page).nth(3), 'Storage after refill'), 'value-negative')).toBe(false);
      await expect(cardValue(reorderCards(page).nth(4), 'Storage after refill')).toHaveText('2 units');

      // Its own row of the grid, under and aligned with Storage stock rather than squeezed beside
      // Machine need, and the quantities around it are the ones they always were.
      const storageStock = await box(first.locator('dt:text-is("Storage stock")'));
      const machineNeed = await box(first.locator('dt:text-is("Machine need")'));
      const afterRefill = await box(first.locator('dt:text-is("Storage after refill")'));
      expect(afterRefill.y).toBeGreaterThan(machineNeed.y);
      expect(afterRefill.x).toBeCloseTo(storageStock.x, 0);
      await expect(cardValue(first, 'Storage stock')).toHaveText('4 units');
      await expect(cardValue(first, 'Machine need')).toHaveText('7 units');

      // Nothing spills out of the card or out of its own box, and the page does not scroll sideways.
      const value = cardValue(first, 'Storage after refill');
      const valueBox = await box(value);
      const cardBox = await box(first);
      expect(valueBox.x).toBeGreaterThanOrEqual(cardBox.x);
      expect(valueBox.x + valueBox.width).toBeLessThanOrEqual(cardBox.x + cardBox.width);
      expect(await value.evaluate((node) => node.scrollWidth - node.clientWidth)).toBeLessThanOrEqual(0);
      await expectNoHorizontalOverflow(page);
    });
  }

  /**
   * Storage-only mode (issue #727) withholds the machine need, and issue #1020's column is withheld
   * with it: the subtraction there would be made from an unread `0`. The synthetic reorder read
   * declares that mode with the response headers the API publishes, so this is the real rendering
   * rather than a styled one, and the capture is the evidence that neither layout carries the figure
   * while every storage figure and the pager stay exactly as they were.
   */
  for (const width of [1440, 390]) {
    test(`Reorder priorities withholds Storage after refill in storage-only mode at ${width}px`, async ({ page }) => {
      await openDashboard(page, width, { storageOnlyReorder: true });
      const section = reorderSection(page);
      await section.scrollIntoViewIfNeeded();

      await expect(section.getByText(/^Storage stock only: these figures do not include/)).toBeVisible();
      // Not hidden by a class in one layout or the other: neither figure is rendered at all.
      await expect(section.getByText('Machine need', { exact: true })).toHaveCount(0);
      await expect(section.getByText('Storage after refill', { exact: true })).toHaveCount(0);
      await expect(section.getByText('Storage stock', { exact: true }).first()).toBeAttached();
      await expect(page.getByTestId('reorder-range')).toHaveText(/Showing 1.5 of 7 products/);
      await expectNoHorizontalOverflow(page);

      // An element taller than the viewport is captured by stitching, which would paint the fixed
      // bottom bar across the middle of the card; growing the viewport to the document first is the
      // same answer the full-page captures use.
      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await section.screenshot({ path: join(screenshotDirectory, `reorder-storage-only-${width}px.png`) });
    });
  }

  /**
   * Issue #1015's own evidence, and the part of it only a browser can answer: the card's
   * explanation is now behind one help button beside its title, and the panel that button opens
   * has to stand *beside* the button in the top layer - clipped by neither the `overflow-hidden`
   * card nor anything else, inside the viewport even at 360px, not over its own trigger, and
   * without moving the table it explains. Each of those is measured here from the rendered boxes
   * rather than argued from the classes, at every width the issue names.
   */
  for (const width of [360, 390, 768, 1440]) {
    test(`Site and machine performance help panel open at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);
      // Centred rather than merely scrolled into view, so the card header is in a settled place on
      // every width and the capture is of the panel beside its button rather than of a page edge.
      await page
        .locator('#site-machine-performance-title')
        .evaluate((heading) => heading.scrollIntoView({ block: 'center' }));
      const button = performanceHelpButton(page);
      const figures = page
        .locator('[data-testid="performance-table"]:visible, [data-testid="performance-cards"]:visible')
        .first();

      // Immediately after the words, on this width's own line breaks.
      const placement = await measureHelpButtonPlacement(page);
      expect(placement.onTitleLastLine).toBe(true);
      expect(placement.gapAfterLastWord).toBeGreaterThanOrEqual(0);
      expect(placement.gapAfterLastWord).toBeLessThanOrEqual(8);

      // Where the figures sit before the panel exists: an anchored panel must not push them.
      const figuresBefore = await box(figures);
      await button.click();

      const panel = page.getByTestId('help-toggletip-panel');
      await expect(panel).toBeVisible();
      await expect(button).toHaveAttribute('aria-expanded', 'true');
      await expect(panel).toHaveText(/need not equal the sum of its machines'\.$/);

      expect(await measureHelpPanel(page)).toEqual({
        coversTrigger: false,
        insideViewport: true,
        onTopThroughout: true
      });
      // The figures have not moved, which is what an in-flow disclosure could not manage.
      expect(await box(figures)).toEqual(figuresBefore);
      await expectNoHorizontalOverflow(page);

      await page.screenshot({ path: join(screenshotDirectory, `performance-help-${width}px.png`) });

      // Escape dismisses it and hands focus back, from a pointer opening as well as a keyboard one.
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('help-toggletip-panel')).toHaveCount(0);
      await expect(button).toHaveAttribute('aria-expanded', 'false');
      await expect(button).toBeFocused();
    });
  }

  test('the help button takes keyboard focus, shows it, and opens from the keyboard', async ({ page }) => {
    await openDashboard(page, 1440);
    const button = performanceHelpButton(page);

    await button.focus();
    await expect(button).toBeFocused();
    // Focus alone shows the explanation, and the ring is captured with the card header around it.
    await expect(page.getByTestId('help-toggletip-panel')).toBeVisible();
    await page
      .locator('section[aria-labelledby="site-machine-performance-title"] .card-header')
      .screenshot({ path: join(screenshotDirectory, 'performance-help-focus-1440px.png') });

    // Space pins it open, Enter closes it again: one key press is one toggle.
    await page.keyboard.press(' ');
    await expect(page.getByTestId('help-toggletip-panel')).toBeVisible();
    await expect(button).toHaveAttribute('aria-expanded', 'true');

    await page.keyboard.press('Enter');
    await expect(page.getByTestId('help-toggletip-panel')).toHaveCount(0);
    await expect(button).toBeFocused();
  });

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

  /**
   * Issue #1014's own evidence, and the part of it that nothing but a browser can give: the
   * hierarchy is *measured* in the rendered page rather than reasoned from the Tailwind values,
   * because the question the issue asks - where does a machine name actually start compared with
   * its site's name - is a layout outcome, not a class list.
   */
  test('every machine name is measurably nested under its site name at 1440px', async ({ page }) => {
    await openDashboard(page, 1440);
    /** The issue's rule: at least 1.5rem, which is 24px at the root font size the application ships. */
    const minimumChildIndent = 24;

    const siteOneName = await leftEdge(siteName(page, 'site-1'));
    // Every machine of both sites, including the two standing at the deliberately long-named one.
    for (const child of [
      { group: 'site-1', machineId: 1 },
      { group: 'site-2', machineId: 2 },
      { group: 'site-2', machineId: 3 }
    ]) {
      const machineNameX = await leftEdge(page.locator(`tbody#site-machines-${child.group} [data-testid="machine-name-${child.machineId}"]`));
      expect(machineNameX - (await leftEdge(siteName(page, child.group)))).toBeGreaterThanOrEqual(minimumChildIndent);
    }

    // The structural cue, not background colour alone: a rule drawn down the left of every child.
    await expect(page.getByTestId('machine-row-indent-1')).toHaveCSS('border-left-width', '2px');

    // "Other machines" and an empty site's own line are presented as children in the same place:
    // the indented, ruled box itself, which is what carries the cue, starts exactly where a site's
    // machine row does.
    const childBox = await leftEdge(page.getByTestId('machine-row-indent-1'));
    expect(await leftEdge(page.getByTestId('machine-row-indent-4'))).toBeCloseTo(childBox, 0);
    expect(await leftEdge(page.getByText('No machines at this site.').filter({ visible: true }).first())).toBeCloseTo(childBox, 0);

    // The glyph keeps its own space: collapsing a group swaps ▾ for ▴ and moves no site name, so a
    // machine name can never come to line up with one.
    await siteExpander(page, 'site-1').click();
    await expect(siteExpander(page, 'site-1')).toHaveAttribute('aria-expanded', 'false');
    expect(await leftEdge(siteName(page, 'site-1'))).toBeCloseTo(siteOneName, 0);

    // The long names wrap inside the Site / machine column instead of widening it or the page.
    await expectNoHorizontalOverflow(page);
    const nameColumn = (await page.locator('[data-testid="performance-table"] thead th').first().boundingBox())!;
    const longMachineName = (await page.locator('tbody#site-machines-site-2 [data-testid="machine-name-3"]').boundingBox())!;
    expect(longMachineName.x + longMachineName.width).toBeLessThanOrEqual(nameColumn.x + nameColumn.width);
  });

  for (const width of [360, 390]) {
    test(`machine cards stay inside their site card and indented at ${width}px`, async ({ page }) => {
      await openDashboard(page, width);

      // The two-machine site, whose name is the deliberately long one: the hardest case for a
      // narrow screen, and the one that would overflow if the indent were taken from the viewport.
      const siteCard = page.getByTestId('performance-card-group-site-2');
      const machineCard = siteCard.getByTestId('performance-machine-card-2');
      await expect(machineCard).toBeVisible();

      const site = (await siteCard.boundingBox())!;
      const machine = (await machineCard.boundingBox())!;
      expect(machine.x).toBeGreaterThan(site.x);
      expect(machine.x + machine.width).toBeLessThanOrEqual(site.x + site.width);
      expect(machine.y).toBeGreaterThan(site.y);
      expect(machine.y + machine.height).toBeLessThanOrEqual(site.y + site.height);

      expect(await leftEdge(machineCard.getByTestId('machine-name-2'))).toBeGreaterThan(await leftEdge(siteName(page, 'site-2')));
      await expect(page.locator('ul#site-machine-cards-site-2')).toHaveCSS('border-left-width', '2px');

      await expectNoHorizontalOverflow(page);
    });
  }

  /**
   * The one group that is deliberately *not* nested: when the sites read fails there is no site to
   * be a child of, so every machine falls back to one unheaded group that keeps no indent and no
   * connector - nothing may look nested under a site, an error message or an empty message.
   */
  for (const width of [1440, 390]) {
    test(`machines are not nested under a failed sites read at ${width}px`, async ({ page }) => {
      await openDashboard(page, width, { failSitesRead: true });

      await expect(page.getByTestId('sites-unavailable')).toBeVisible();
      await expect(page.locator('[data-testid^="performance-expander-"], [data-testid^="performance-card-expander-"]')).toHaveCount(0);
      await expect(page.getByTestId('machine-row-indent-1')).toHaveCSS('border-left-width', '0px');
      await expect(page.locator('ul#site-machine-cards-other')).toHaveCSS('border-left-width', '0px');
      await expectNoHorizontalOverflow(page);

      const height = await page.evaluate(() => document.documentElement.scrollHeight);
      await page.setViewportSize({ width, height });
      await page.screenshot({ path: join(screenshotDirectory, `performance-no-sites-${width}px.png`) });
    });
  }

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
