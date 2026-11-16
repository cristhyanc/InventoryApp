import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { OWNER_CAPABILITIES, expectNoHorizontalOverflow, supplierOffer as offer } from './support/products-page';

/**
 * The browser evidence issue #1279 asks for: the Needs ordering page at 1440px and 390px, after
 * audit #1225's finding FE-08 gave its search box and category filter an accessible name.
 *
 * Run it from `frontend/inventory-app`, after `npm run e2e:install`:
 *
 * ```
 * npm run e2e:visual -- visual/needs-ordering-filter-labels.visual.ts
 * ```
 *
 * Name the file as above: the visual configuration rewrites every screenshot of every visual run,
 * so running the whole suite re-renders other issues' committed captures and leaves them modified
 * in the working tree. This file writes only to `docs/screenshots/issue-1279/`.
 *
 * Like every visual run it is human-invoked, is not part of `scripts/validate.sh` and starts no API
 * and no database: every `/api/` request is answered in the browser with neutral synthetic products,
 * categories, purchase costs and supplier offers, so no business data, account name or credential
 * can appear in a capture, and nothing is ever saved.
 *
 * It takes the two pictures, and it also measures the thing a picture cannot prove on its own -
 * that the change really was label-only:
 *
 *  - both controls are findable **by role and accessible name**, in the real browser's own
 *    accessibility tree rather than by a computed-name helper in jsdom;
 *  - **no visible label was added**: the toolbar carries no `<label>` element and no visible text
 *    reading "Category:" or "Search name or SKU" (the search box's placeholder is not text content);
 *  - **the layout is unchanged by the labels**: every toolbar box is measured, both `aria-label`
 *    attributes are then removed from the live DOM - which is exactly the markup this page had
 *    before the fix - and every box is measured again. The geometry has to be identical, at each
 *    width, which is the acceptance criterion's "unchanged apart from any visible label" stated as
 *    a measurement rather than as an argument about how `aria-label` behaves;
 *  - neither width scrolls sideways.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1279');
mkdirSync(screenshotDirectory, { recursive: true });

/** This spec sets every width itself, so it runs once rather than once per configured project. */
const PRIMARY_PROJECT = 'desktop-1440';

/** The wording the Products list's own filters use, which is what the two controls had to match. */
const SEARCH_NAME = 'Search name or SKU';
const CATEGORY_NAME = 'Category:';

/** `InventoryApi.Adapters.Mapping.ReorderListingMetadataHeaders.Mode`, as the listing publishes it. */
const REORDER_MODE_HEADER = 'X-Reorder-Mode';

const categories = [
  { id: 1, name: 'Snacks' },
  { id: 2, name: 'Drinks' },
  { id: 3, name: 'Sweets' }
];

function product(id: number, name: string, sku: string, categoryId: number, stock: number, threshold: number, restockTo: number) {
  return {
    id,
    name,
    sku,
    description: null,
    averageUnitCost: 2,
    averageUnitCostForDisplay: 2,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: stock,
    maxStockInMachine: 10,
    machineReplenishmentNeed: Math.max(0, 10 - stock),
    onOrderQuantity: 0,
    lowStockThreshold: threshold,
    restockTo,
    needToOrder: Math.max(0, restockTo - stock),
    mdbCode: null,
    unit: 'ea',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId,
    category: categories.find((c) => c.id === categoryId) ?? null,
    isActive: true,
    isLowStock: true,
    isReorderAlert: true,
    canDelete: false,
    deletionRefusal: null
  };
}

/** Every product here is below its threshold: this endpoint answers the reorder alerts only. */
const products = [
  product(1, 'Sample Beef Jerky Original 25g', 'SMP-JRK-25', 1, 2, 5, 20),
  product(2, 'Sample Mint Bubbles Chocolate Bar With A Deliberately Long Name 40g', 'SMP-MNT-40', 3, 0, 4, 18),
  product(3, 'Sample Cheese & Crackers 38g', 'SMP-CHC-38', 1, 3, 6, 24),
  product(4, 'Sample Iced Coffee Double Shot 237mL', 'SMP-ICD-237', 2, 1, 5, 20),
  product(5, 'Sample Ginger Beer 250mL', 'SMP-GNB-250', 2, 4, 8, 32),
  product(6, 'Sample Hazelnut Wafer 43g', 'SMP-HZW-43', 3, 2, 5, 15)
];

function cost(productId: number, last: number, lastSupplier: string, low: number, lowSupplier: string) {
  return {
    productId,
    lastUnitCost: last,
    lastSupplierName: lastSupplier,
    lastPurchaseDate: '2026-10-18T00:00:00',
    lowestUnitCost: low,
    lowestSupplierName: lowSupplier,
    lowestPurchaseDate: '2026-08-04T00:00:00'
  };
}

/** Product 6 is absent on purpose, so its history cells show the em dash rather than a cost. */
const purchaseCosts = [
  cost(1, 3.48, 'Sample Wholesale', 3.47, 'Sample Wholesale'),
  cost(2, 1.5, 'Sample Grocer', 1.5, 'Sample Market'),
  cost(3, 2.5, 'Sample Grocer', 2.5, 'Sample Grocer'),
  cost(4, 2.94, 'Sample Market', 2.2, 'Sample Market'),
  cost(5, 1.2, 'Sample Wholesale', 1.2, 'Sample Wholesale')
];

const MATCHED = 3;
const cheapestSuppliers = {
  displayBasis: 2,
  products: [
    { productId: 1, trackingStatus: MATCHED, cheapestUnitPrice: 3.82, suppliers: [offer(1, 'Sample Wholesale South Coast', 12, 45.84, 3.82, { isCheapest: true })] },
    { productId: 2, trackingStatus: MATCHED, cheapestUnitPrice: 1.5, suppliers: [offer(2, 'Sample Grocer', 1, 1.5, 1.5, { isCheapest: true })] },
    { productId: 4, trackingStatus: MATCHED, cheapestUnitPrice: 1.84, suppliers: [offer(2, 'Sample Grocer', 12, 22.08, 1.84, { isCheapest: true })] },
    { productId: 5, trackingStatus: MATCHED, cheapestUnitPrice: 1.2, suppliers: [offer(1, 'Sample Wholesale South Coast', 24, 28.8, 1.2, { isCheapest: true })] }
  ]
};

const suppliers = [
  { id: 1, name: 'Sample Wholesale South Coast' },
  { id: 2, name: 'Sample Grocer' },
  { id: 3, name: 'Sample Market' }
];

function fulfilJson(route: Route, body: unknown, headers: Record<string, string> = {}): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', headers, body: JSON.stringify(body) });
}

/** Every request this page makes, answered in the browser. Nothing reaches an API. */
async function answerNeedsOrderingApi(page: Page): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/me/access')) {
      return fulfilJson(route, { role: 'Owner', capabilities: OWNER_CAPABILITIES, businessName: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    }
    if (path.endsWith('/me/account-state')) return fulfilJson(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return fulfilJson(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return fulfilJson(route, { status: 'Ready', machineChecksPending: false });
    // The machine-aware listing, so the Machine need column is the one an operator normally sees.
    // The listing's mode travels as a header, which a cross-origin reader may only see when it is
    // exposed; an unread mode would simply leave the column shown, so this is explicit either way.
    if (path.endsWith('/products/alerts/low-stock')) {
      return fulfilJson(route, products, { [REORDER_MODE_HEADER]: 'MachineAware', 'Access-Control-Expose-Headers': REORDER_MODE_HEADER });
    }
    if (path.endsWith('/products/purchase-cost-summary')) return fulfilJson(route, purchaseCosts);
    if (path.endsWith('/cheapest-suppliers/summary')) return fulfilJson(route, cheapestSuppliers);
    if (path.endsWith('/categories')) return fulfilJson(route, categories);
    if (path.endsWith('/suppliers')) return fulfilJson(route, suppliers);
    if (route.request().method() !== 'GET') return route.fulfill({ status: 204, body: '' });
    return fulfilJson(route, []);
  });
}

/** Opens Needs ordering at the given width with animations off, once the rows are drawn. */
async function openNeedsOrdering(page: Page, width: number): Promise<void> {
  await page.setViewportSize({ width, height: width >= 1024 ? 1000 : 844 });
  await answerNeedsOrderingApi(page);
  await page.goto('/products/needs-ordering');
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
  await expect(page.getByRole('heading', { name: 'Products', level: 1 })).toBeVisible();
  // The rows are drawn: each product name is a link that looks the product up, named exactly.
  await expect(page.getByRole('link', { name: `Search Google for ${products[0].name}` })).toBeVisible();
}

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface ToolbarGeometry {
  toolbar: Box;
  controls: Box[];
  documentScrollWidth: number;
  documentScrollHeight: number;
}

/** The filter toolbar's own box and the box of every control in it, as the browser laid them out. */
function measureToolbar(page: Page): Promise<ToolbarGeometry> {
  return page.evaluate(() => {
    const search = document.querySelector('app-product-needs-ordering input');
    const toolbar = search?.closest('.card-body') ?? null;
    if (toolbar === null) throw new Error('The Needs ordering filter toolbar did not render.');
    const box = (element: Element): { x: number; y: number; width: number; height: number } => {
      const rect = element.getBoundingClientRect();
      return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
    };
    return {
      toolbar: box(toolbar),
      controls: Array.from(toolbar.querySelectorAll('input, select, button')).map(box),
      documentScrollWidth: document.documentElement.scrollWidth,
      documentScrollHeight: document.documentElement.scrollHeight
    };
  });
}

/**
 * Takes the two `aria-label` attributes off the live toolbar, which restores exactly the markup
 * this page had before issue #1279, and reports how many were removed.
 */
function removeToolbarLabels(page: Page): Promise<number> {
  return page.evaluate(() => {
    const search = document.querySelector('app-product-needs-ordering input');
    const toolbar = search?.closest('.card-body') ?? null;
    if (toolbar === null) throw new Error('The Needs ordering filter toolbar did not render.');
    const labelled = Array.from(toolbar.querySelectorAll('[aria-label]'));
    for (const element of labelled) element.removeAttribute('aria-label');
    return labelled.length;
  });
}

test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== PRIMARY_PROJECT, 'This spec sets each width itself, so it runs once.');
});

test.describe('Needs ordering filter labels (issue #1279, audit #1225 FE-08)', () => {
  for (const width of [1440, 390]) {
    test(`the filters are named, add no visible label and change no layout at ${width}px`, async ({ page }) => {
      await openNeedsOrdering(page, width);

      const searchBox = page.getByRole('textbox', { name: SEARCH_NAME });
      const categoryFilter = page.getByRole('combobox', { name: CATEGORY_NAME });
      await expect(searchBox).toBeVisible();
      await expect(categoryFilter).toBeVisible();

      const toolbar = page.locator('app-product-needs-ordering .card-body').filter({ has: searchBox });
      // No visible label was added: no `<label>` element, and neither name appears as visible text.
      // "All Categories" is the select's own option text - its value, not a label - so the match is
      // exact rather than substring.
      await expect(toolbar.locator('label')).toHaveCount(0);
      await expect(toolbar.getByText(CATEGORY_NAME, { exact: true })).toHaveCount(0);
      await expect(toolbar.getByText(SEARCH_NAME, { exact: true })).toHaveCount(0);

      // The page does not scroll sideways at this width, and neither does the toolbar itself.
      await expectNoHorizontalOverflow(page);
      const toolbarOverflow = await toolbar.evaluate((element) => element.scrollWidth - element.clientWidth);
      expect(toolbarOverflow).toBeLessThanOrEqual(1);

      await page.screenshot({ path: join(screenshotDirectory, `needs-ordering-filters-${width}px.png`), fullPage: true });

      // The layout proof: measured, then measured again without the two labels - the markup as it
      // was before the fix - with no other change to the page.
      const labelled = await measureToolbar(page);
      expect(await removeToolbarLabels(page)).toBe(2);
      const unlabelled = await measureToolbar(page);

      expect(unlabelled).toEqual(labelled);
      // The removal really did take the name away: a `<select>` has no other source for one, so the
      // combobox the issue asked to name is unreachable by that name again. The search box is not
      // asserted the same way on purpose - its placeholder repeats the wording, and a browser falls
      // back to a placeholder for a name, which is the discouraged naming the fix replaced.
      await expect(page.getByRole('combobox', { name: CATEGORY_NAME })).toHaveCount(0);
    });
  }
});
