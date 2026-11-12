import { Page, Route, expect } from '@playwright/test';

/**
 * Shared synthetic answers and browser measurements for the visual specs that open the Suppliers
 * page: its Add/Edit dialogs (issue #1174, `suppliers-dialogs.visual.ts`) and the redesigned
 * register itself (issue #1175, `suppliers-register-redesign.visual.ts`). It is not a spec - the
 * visual config only runs files ending in `.visual.ts` - so importing it registers no tests.
 *
 * Every `/api/` request is answered in the browser with neutral synthetic suppliers, so no API, no
 * database, no business name, contact, phone, email or address is involved and none can appear in a
 * capture. **Nothing here writes**: every non-`GET` is refused, so a run that ever drifted into
 * creating, updating or deleting a supplier fails rather than quietly photographing a save.
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

/**
 * Two synthetic suppliers: one the business added itself, and one that came from the platform's
 * supplier list, whose Name and Website are locked with the explanatory note (issue #934) and which
 * has no Delete. They are the pair the dialog evidence of issue #1174 is captured against.
 */
export const SAMPLE_SUPPLIERS = [
  {
    id: 4,
    name: 'Sample Wholesale Distributors',
    contactName: 'Sample Contact',
    phone: '02 0000 0000',
    email: 'orders@sample-wholesale.invalid',
    address: '1 Sample Street\nSampleville NSW 2000',
    url: 'https://sample-wholesale.invalid',
    isLinked: false,
    isHidden: false,
    canRename: true,
    canDelete: true
  },
  {
    id: 7,
    name: 'Sample Platform Supplier',
    contactName: 'Sample Account Manager',
    phone: '03 0000 0000',
    email: 'accounts@sample-platform.invalid',
    address: '2 Sample Parade\nSample Bay VIC 3000',
    url: 'https://sample-platform.invalid',
    isLinked: true,
    isHidden: false,
    canRename: false,
    canDelete: false
  }
];

/** What the Suppliers page asks for; each spec supplies the register it wants photographed. */
export interface SuppliersPageData {
  /** `GET /api/suppliers` (including the ones marked "Not used"); {@link SAMPLE_SUPPLIERS} by default. */
  suppliers?: unknown[];
  /** `GET /api/suppliers/available-presets`; empty by default, which hides that section. */
  presets?: unknown[];
}

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

export async function answerSuppliersApi(page: Page, data: SuppliersPageData = {}): Promise<void> {
  const suppliers = data.suppliers ?? SAMPLE_SUPPLIERS;
  const presets = data.presets ?? [];

  await page.route('**/api/**', (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;

    // Nothing here writes. A create, an update, a delete or a "Not used" switch is refused rather
    // than photographed, so this run cannot capture a save it also performed.
    if (request.method() !== 'GET') {
      return json(route, { title: 'This visual run makes no writes.' }, 500);
    }

    if (path.endsWith('/me/access')) {
      return json(route, {
        role: 'Owner',
        capabilities: OWNER_CAPABILITIES,
        businessName: 'Sample Vending',
        timeZoneId: 'Australia/Sydney',
        entitlement: { enforced: true, plan: 'Pro', source: 'BillingSubscription', endsAtUtc: null }
      });
    }
    if (path.endsWith('/me/account-state')) return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/suppliers/available-presets')) return json(route, presets);
    if (path.endsWith('/suppliers')) return json(route, suppliers);
    return json(route, []);
  });
}

/** Opens the Suppliers page at one viewport, with animations off, once the register has loaded. */
export async function openSuppliersPage(page: Page, width: number, height: number, data: SuppliersPageData = {}): Promise<void> {
  await page.setViewportSize({ width, height });
  await answerSuppliersApi(page, data);
  await page.goto('/suppliers');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }'
  });
  await expect(page.getByRole('heading', { name: 'Suppliers', level: 1 })).toBeVisible();
  // The count renders only once the list itself has loaded, so this is the register being ready.
  await expect(page.locator('[data-testid="suppliers-count"]')).toBeVisible();
}

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
export const TOLERANCE = 1;

/** The page does not scroll sideways: the one overflow rule both Suppliers issues state. */
export async function expectNoPageHorizontalOverflow(page: Page, width: number): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, `the page does not scroll sideways at ${width}px`).toBeLessThanOrEqual(TOLERANCE);
}

/** Pinch zoom stays available: the shell's viewport meta locks neither the scale nor the user. */
export async function expectPinchZoomAllowed(page: Page): Promise<void> {
  const viewport = await page.locator('meta[name="viewport"]').getAttribute('content');
  expect(viewport, 'the shell declares a viewport').toBeTruthy();
  expect(viewport ?? '').not.toMatch(/user-scalable\s*=\s*no|user-scalable\s*=\s*0|maximum-scale\s*=\s*1(\.0)?\b/);
}

/** How many rendered elements the selector matches, and the smallest box among them. */
async function renderedBoxes(page: Page, selector: string): Promise<{ count: number; minHeight: number; minWidth: number }> {
  return page.locator(selector).evaluateAll((elements) => {
    const boxes = elements
      .filter((element) => (element as HTMLElement).offsetParent !== null || element.closest('dialog') !== null)
      .map((element) => element.getBoundingClientRect());
    return {
      count: boxes.length,
      minHeight: Math.min(...boxes.map((box) => box.height)),
      minWidth: Math.min(...boxes.map((box) => box.width))
    };
  });
}

/**
 * Every control these selectors match is at least a 44px touch target, and each selector matches
 * something - a selector that matched nothing would otherwise pass this silently.
 */
export async function expectTouchTargets(page: Page, width: number, selectors: string[], minimum = 44): Promise<void> {
  for (const selector of selectors) {
    const boxes = await renderedBoxes(page, selector);
    expect(boxes.count, `${selector} is on screen at ${width}px`).toBeGreaterThan(0);
    expect(boxes.minHeight, `${selector} is at least ${minimum}px tall at ${width}px`).toBeGreaterThanOrEqual(minimum);
  }
}

/** The same, for a control that has to be square rather than a tall sliver. */
export async function expectSquareTouchTargets(page: Page, width: number, selectors: string[], minimum = 44): Promise<void> {
  await expectTouchTargets(page, width, selectors, minimum);
  for (const selector of selectors) {
    const boxes = await renderedBoxes(page, selector);
    expect(boxes.minWidth, `${selector} is at least ${minimum}px wide at ${width}px`).toBeGreaterThanOrEqual(minimum);
  }
}
