import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual and layout evidence for issue #1170, the redesigned Operating Expenses register:
 * desktop at 1440px, the More filters disclosure, the delete confirmation reached by keyboard alone,
 * and the narrow-screen cards at 390px, with the overflow and touch-target measurements taken down
 * to 360px.
 *
 * Like the other visual runs it starts no API and no database: every `/api/` request is answered in
 * the browser with neutral synthetic expenses, so no business data, account name or credential can
 * appear in a capture. Every non-GET is refused rather than answered, so a run that ever reached the
 * delete itself fails instead of silently photographing a write.
 *
 * Each capture also asserts what jsdom cannot, because jsdom computes no layout: that the page does
 * not scroll sideways at 1440px, 390px or 360px, that the table gives way to the cards below `md`,
 * that the page-local controls reach 44px on a phone, that pinch zoom stays allowed, and that a long
 * unbroken description wraps inside its card instead of being clipped.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1170');
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

const suppliers = [
  { id: 3, name: 'Sample Insurance Brokers' },
  { id: 4, name: 'Sample Cleaning Services' }
];

/** One active machine and one deactivated one, which the filter lists as well. */
const machines = [
  { id: 41, name: 'Sample Lobby Machine', isActive: true, isProviderNamed: false },
  { id: 42, name: 'Sample Gym Machine (retired)', isActive: false, isProviderNamed: false }
];

/** Category numbers are `components/expenses/expense-categories.ts`: 1 Insurance, 3 Software, 8 Other. */
function expense(
  id: number,
  description: string,
  overrides: Partial<{
    expenseDate: string;
    category: number;
    amountExGst: number;
    gstAmount: number;
    totalAmount: number;
    supplierId: number | null;
    supplierName: string | null;
    attachmentFileName: string | null;
  }> = {}
) {
  const amountExGst = overrides.amountExGst ?? 90.91;
  const gstAmount = overrides.gstAmount ?? 9.09;
  return {
    id,
    expenseDate: overrides.expenseDate ?? `2026-02-${String((id % 28) + 1).padStart(2, '0')}T00:00:00Z`,
    category: overrides.category ?? 1,
    description,
    amountExGst,
    gstAmount,
    totalAmount: overrides.totalAmount ?? amountExGst + gstAmount,
    supplierId: overrides.supplierId ?? 3,
    supplierName: overrides.supplierName ?? 'Sample Insurance Brokers',
    machineId: null,
    siteId: null,
    attachmentFileName: overrides.attachmentFileName ?? null,
    attachmentContentType: overrides.attachmentFileName ? 'application/pdf' : null,
    attachmentFileSizeBytes: overrides.attachmentFileName ? 48_219 : null,
    servicePeriodStart: null,
    servicePeriodEnd: null,
    notes: null
  };
}

const LONG_DESCRIPTION =
  'SampleQuarterlyPublicLiabilityInsurancePremiumForTheWholeVendingFleetIncludingTheRetiredMachines';

/** Twenty-three expenses: three pages at the default ten, with every row state on the first page. */
const expenses = [
  expense(1, 'Public liability insurance, annual policy', {
    totalAmount: 1200,
    amountExGst: 1090.91,
    gstAmount: 109.09,
    attachmentFileName: 'sample-insurance-invoice-2026.pdf'
  }),
  expense(2, 'Accounting software subscription', { category: 3, supplierId: null, supplierName: null }),
  expense(3, LONG_DESCRIPTION, { category: 8 }),
  // A recorded zero is a figure, not a missing value: it shows as $0.00.
  expense(4, 'Goodwill credit from the cleaner', { category: 8, amountExGst: 0, gstAmount: 0, totalAmount: 0, supplierId: 4, supplierName: 'Sample Cleaning Services' }),
  ...Array.from({ length: 19 }, (_, index) =>
    expense(index + 5, `Sample recorded expense ${String(index + 5).padStart(2, '0')}`, {
      category: index % 2 === 0 ? 3 : 1
    })
  )
];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

/** Nothing on this page may write here: a delete that actually ran would fail the run. */
function answerWrite(route: Route): Promise<void> {
  return route.fulfill({
    status: 500,
    contentType: 'application/json',
    body: JSON.stringify({ title: 'This visual run makes no writes.' })
  });
}

type ApiOptions = { holdList?: boolean; list?: ReturnType<typeof expense>[] };

async function answerApi(page: Page, options: ApiOptions = {}): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() !== 'GET') return answerWrite(route);
    if (path.endsWith('/me/access')) {
      return json(route, {
        role: 'Owner',
        capabilities: OWNER_CAPABILITIES,
        businessName: 'Sample Vending',
        timeZoneId: 'Australia/Sydney'
      });
    }
    if (path.endsWith('/me/account-state')) return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/suppliers')) return json(route, suppliers);
    if (path.endsWith('/local-machines')) return json(route, machines);
    if (path.endsWith('/operating-expenses')) {
      if (options.holdList) return new Promise<void>(() => undefined);
      return json(route, options.list ?? expenses);
    }
    return json(route, []);
  });
}

async function openRegister(page: Page, width: number, options: ApiOptions = {}): Promise<void> {
  await page.setViewportSize({ width, height: width >= 1024 ? 900 : 844 });
  await answerApi(page, options);
  await page.goto('/expenses');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }'
  });
  await expect(page.getByRole('heading', { name: 'Operating expenses', level: 1 })).toBeVisible();
  // The pager exists only once rows have arrived, so a held or deliberately empty list waits for
  // the state it is there to show instead.
  if (options.holdList) {
    await expect(page.locator('[data-testid="expenses-loading"]')).toBeVisible();
  } else if (options.list?.length === 0) {
    await expect(page.locator('[data-testid="expenses-empty"]')).toBeVisible();
  } else {
    await expect(page.locator('[data-testid="list-pagination-range"]')).toBeVisible();
  }
}

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

async function expectNoHorizontalOverflow(page: Page, width: number): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, `no horizontal overflow at ${width}px`).toBeLessThanOrEqual(tolerance);
}

async function capture(page: Page, name: string, fullPage = true): Promise<void> {
  await page.screenshot({ path: join(screenshotDirectory, `${name}.png`), fullPage });
}

/** The minimum rendered height of each visible element the locator matches. */
function minHeight(page: Page, selector: string): Promise<number> {
  return page.locator(selector).evaluateAll((elements) =>
    Math.min(
      ...elements
        .filter((element) => (element as HTMLElement).offsetParent !== null)
        .map((element) => element.getBoundingClientRect().height)
    )
  );
}

test.use({ timezoneId: 'Australia/Sydney', locale: 'en-AU' });

test.describe('Operating Expenses register redesign (issue #1170)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  test('1 desktop at 1440px', async ({ page }) => {
    await openRegister(page, 1440);

    await expect(page.locator('thead th')).toHaveText([
      'Date',
      'Description',
      'Category',
      'Supplier',
      'Document',
      'Ex GST',
      'GST',
      'Total',
      'Actions'
    ]);
    await expect(page.locator('[data-testid="expense-row"]')).toHaveCount(10);
    // The Totals footer and the pager both speak about the whole filtered list, not the page.
    await expect(page.locator('[data-testid="expenses-totals"]')).toContainText('Totals (all 23 filtered expenses)');
    await expect(page.locator('[data-testid="list-pagination-range"]')).toContainText('Showing 1–10 of 23 expenses');
    await expect(page.locator('[data-testid="list-pagination-page"]')).toHaveText('Page 1 of 3');
    await expectNoHorizontalOverflow(page, 1440);
    await capture(page, 'expenses-register-desktop-1440');
  });

  test('2 desktop More filters, Supplier and Machine, at 1440px', async ({ page }) => {
    await openRegister(page, 1440);

    const disclosure = page.locator('[data-testid="expenses-more-filters"]');
    await expect(disclosure).toHaveAttribute('aria-expanded', 'false');
    await disclosure.click();
    await expect(disclosure).toHaveAttribute('aria-expanded', 'true');
    await expect(page.locator('#expense-supplier-filter')).toBeVisible();
    // The deactivated machine is listed too, so a retired machine's expenses stay findable.
    await expect(page.locator('#expense-machine-filter option')).toHaveText([
      'All machines',
      'Sample Lobby Machine',
      'Sample Gym Machine (retired)'
    ]);
    await expectNoHorizontalOverflow(page, 1440);
    await capture(page, 'expenses-register-more-filters-1440', false);
  });

  test('3 the row menu and the delete confirmation are reachable by keyboard alone', async ({ page }) => {
    await openRegister(page, 1440);

    const trigger = page.locator('[data-testid="expense-row-menu"]').first();
    await trigger.focus();
    // Arrow Down opens the menu and focuses its first item, as the menu-button pattern expects.
    await page.keyboard.press('ArrowDown');
    const deleteItem = page.locator('[data-testid="expense-delete"]').first();
    await expect(deleteItem).toBeFocused();

    await page.keyboard.press('Enter');
    const dialog = page.locator('.confirm-modal');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('Public liability insurance, annual policy');
    await expect(dialog).toContainText('$1,200.00');
    // Focus is inside the dialog, on Cancel rather than on the destructive action.
    await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
    await capture(page, 'expenses-register-delete-confirmation-1440', false);

    // Tab is contained: it reaches Delete, then returns to Cancel rather than leaving the dialog.
    await page.keyboard.press('Tab');
    await expect(dialog.getByRole('button', { name: 'Delete' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();

    // Escape cancels, deletes nothing, and returns focus to the menu that opened it.
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(trigger).toBeFocused();
    await expect(page.locator('[data-testid="expense-row"]')).toHaveCount(10);
  });

  test('4 mobile at 390px', async ({ page }) => {
    await openRegister(page, 390);

    await expect(page.locator('[data-testid="expenses-table"]')).toBeHidden();
    await expect(page.locator('[data-testid="expense-card"]')).toHaveCount(10);
    await expect(page.locator('[data-testid="expenses-totals-summary"]')).toContainText('Totals (all 23 filtered expenses)');

    // Every page-local touch target reaches 44px.
    expect(await minHeight(page, '[data-testid="expenses-add"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="expenses-card-edit"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="expense-card-menu"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="expense-card-view-document"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="expenses-more-filters"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '[data-testid="expenses-apply-filters"]')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '#expense-from-filter')).toBeGreaterThanOrEqual(44);
    expect(await minHeight(page, '#expense-category-filter')).toBeGreaterThanOrEqual(44);

    // Pinch zoom stays allowed.
    const viewport = await page.locator('meta[name="viewport"]').getAttribute('content');
    expect(viewport ?? '').not.toMatch(/user-scalable\s*=\s*no|maximum-scale\s*=\s*1(\.0)?\b/);

    await expectNoHorizontalOverflow(page, 390);
    await capture(page, 'expenses-register-mobile-390');
  });

  test('5 the register and its filters do not scroll sideways at 360px', async ({ page }) => {
    await openRegister(page, 360);

    await expectNoHorizontalOverflow(page, 360);
    // The filter strip wraps rather than scrolling: nothing in the toolbar overflows it.
    const toolbarOverflow = await page
      .locator('[data-testid="expenses-toolbar"]')
      .evaluate((element) => element.scrollWidth - element.clientWidth);
    expect(toolbarOverflow, 'the toolbar wraps instead of scrolling at 360px').toBeLessThanOrEqual(tolerance);

    await page.locator('[data-testid="expenses-more-filters"]').click();
    await expectNoHorizontalOverflow(page, 360);
  });

  test('6 a long unbroken description wraps inside its card at 360px', async ({ page }) => {
    await openRegister(page, 360);

    const description = page.locator('[data-testid="expense-card-description"]', { hasText: LONG_DESCRIPTION });
    await expect(description).toBeVisible();
    const fits = await description.evaluate((element) => {
      const card = element.closest('[data-testid="expense-card"]') as HTMLElement;
      const box = element.getBoundingClientRect();
      const cardBox = card.getBoundingClientRect();
      return {
        noClip: element.scrollWidth <= element.clientWidth + 1,
        inside: box.left >= cardBox.left && box.right <= cardBox.right + 0.5,
        wrapped: box.height > 30
      };
    });
    expect(fits).toEqual({ noClip: true, inside: true, wrapped: true });
    await capture(page, 'expenses-register-mobile-long-description-360');
  });

  test('7 the empty and loading states at 1440px', async ({ page }) => {
    await openRegister(page, 1440, { holdList: true });
    await expect(page.locator('[data-testid="expenses-loading"]')).toBeVisible();
    await capture(page, 'expenses-register-loading-1440', false);

    await openRegister(page, 1440, { list: [] });
    await expect(page.locator('[data-testid="expenses-empty"]')).toContainText('No operating expenses recorded yet.');
    await expect(page.locator('[data-testid="expenses-empty-add"]')).toBeVisible();
    await capture(page, 'expenses-register-empty-1440', false);
  });
});
