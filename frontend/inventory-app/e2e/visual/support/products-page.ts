import { Page, Route, expect } from '@playwright/test';

/**
 * Shared synthetic answers for the visual specs that open the Products page and its supplier
 * comparison (issues #1093, #1179 and #1183). It is not a spec itself - the visual config only
 * runs files ending in `.visual.ts` - so importing it registers no tests.
 *
 * Every `/api/` request is answered in the browser, so no API, database, business data, account
 * name or credential is involved, and nothing is ever saved.
 */
export const OWNER_CAPABILITIES = [
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

/** One observed supplier offer, as the cheapest-suppliers summary returns it. */
export function supplierOffer(
  supplierId: number,
  supplierName: string,
  packQuantity: number | null,
  packPrice: number | null,
  unitPrice: number | null,
  extra: Record<string, unknown> = {}
) {
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

/** What the Products page asks the API for; each spec supplies its own synthetic values. */
export interface ProductsPageData {
  products: unknown;
  categories: unknown;
  purchaseCosts: unknown;
  cheapestSuppliers: unknown;
}

function fulfilJson(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function answerProductsPageApi(page: Page, data: ProductsPageData): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/me/access')) {
      return fulfilJson(route, { role: 'Owner', capabilities: OWNER_CAPABILITIES, businessName: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    }
    if (path.endsWith('/me/account-state')) return fulfilJson(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return fulfilJson(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return fulfilJson(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/api/products')) return fulfilJson(route, data.products);
    if (path.endsWith('/categories')) return fulfilJson(route, data.categories);
    if (path.endsWith('/products/purchase-cost-summary')) return fulfilJson(route, data.purchaseCosts);
    if (path.endsWith('/cheapest-suppliers/summary')) return fulfilJson(route, data.cheapestSuppliers);
    if (route.request().method() !== 'GET') return route.fulfill({ status: 204, body: '' });
    return fulfilJson(route, []);
  });
}

/** Opens Products at the given width with animations off, once the comparison buttons are drawn. */
export async function openProductsPage(page: Page, width: number, data: ProductsPageData): Promise<void> {
  await page.setViewportSize({ width, height: width >= 1024 ? 1000 : 844 });
  await answerProductsPageApi(page, data);
  await page.goto('/products');
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
  await expect(page.getByRole('heading', { name: 'Products', level: 1 })).toBeVisible();
  await expect(page.locator('[data-testid="cheapest-supplier-compare"]:visible').first()).toBeVisible();
}

export async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}
