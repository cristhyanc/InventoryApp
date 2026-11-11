import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual and layout evidence for issue #1167: Add expense and Edit expense, the two dedicated
 * pages that replaced the register's inline card, at desktop (1440px) and narrow mobile (390px).
 *
 * Like the other visual runs it starts no API and no database: every `/api/` request is answered in
 * the browser with neutral synthetic data, so no business data, account name or credential can
 * appear in a capture. It **submits nothing** - no create, no update and no attachment upload - so
 * no expense is recorded and no document is stored anywhere.
 *
 * Each capture also asserts what jsdom cannot, because jsdom computes no layout: that neither page
 * scrolls sideways at 1440px, 390px or 360px (the narrowest width the issue names), that every field
 * group stacks into one column below `sm` and sits beside its neighbour above it, and that Tab walks
 * the whole form in reading order and ends on the page's one primary action.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1167');
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

/** One active machine and one deactivated one: the recorded machine of an edited expense is kept. */
const machines = [
  { id: 41, name: 'Sample Lobby Machine', isActive: true, isProviderNamed: false },
  { id: 42, name: 'Sample Gym Machine (retired)', isActive: false, isProviderNamed: false }
];

/**
 * The expense Edit opens, as `GET /api/operating-expenses/{id}` publishes it: a nested `supplier`,
 * a stored supporting document, and the `siteId`/service period this form has no control for and
 * carries through untouched.
 */
const storedExpense = {
  id: 7,
  expenseDate: '2026-02-01T00:00:00Z',
  // 1 is Insurance; see components/expenses/expense-categories.ts.
  category: 1,
  description: 'Public liability insurance, annual policy',
  amountExGst: 1090.91,
  gstAmount: 109.09,
  totalAmount: 1200,
  supplierId: 3,
  supplier: { id: 3, name: 'Sample Insurance Brokers' },
  siteId: 12,
  machineId: 42,
  attachmentFileName: 'sample-insurance-invoice-2026.pdf',
  attachmentContentType: 'application/pdf',
  attachmentFileSizeBytes: 48_219,
  servicePeriodStart: '2026-02-01T00:00:00Z',
  servicePeriodEnd: '2027-01-31T00:00:00Z',
  notes: 'Renewed for another year.',
  createdAt: '2026-02-01T01:00:00Z',
  updatedAt: '2026-02-01T01:00:00Z'
};

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

/** The synthetic answer for each GET these pages read, by path suffix. First match wins. */
const reads: [suffix: string, body: unknown][] = [
  ['/me/access', { role: 'Owner', capabilities: OWNER_CAPABILITIES, businessName: 'Sample Vending', timeZoneId: 'Australia/Sydney' }],
  ['/me/account-state', { state: 'Member', onboardingEnabled: false, rejectionReason: null }],
  ['/business/current', { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' }],
  ['/nayax-connection/status', { status: 'Ready', machineChecksPending: false }],
  ['/suppliers', suppliers],
  ['/local-machines', machines],
  ['/operating-expenses/7', storedExpense]
];

/**
 * Nothing on these pages is allowed to write here. A capture that ever reached a create, an update
 * or an upload would be answered with a refusal rather than a success, so a run that drifts into
 * submitting the form fails instead of silently photographing a save.
 */
function answerWrite(route: Route): Promise<void> {
  return json(route, { title: 'This visual run makes no writes.' }, 500);
}

async function answerApi(page: Page): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() !== 'GET') return answerWrite(route);
    const read = reads.find(([suffix]) => path.endsWith(suffix));
    return json(route, read ? read[1] : []);
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
  await expect(page.locator('[data-testid="expense-form"]')).toBeVisible();
}

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

async function expectNoHorizontalOverflow(page: Page, width: number): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `no horizontal overflow at ${width}px`).toBeLessThanOrEqual(tolerance);

  for (const section of ['details', 'amounts', 'supporting']) {
    const locator = page.locator(`[data-testid="expense-section-${section}"]`);
    const sectionOverflow = await locator.evaluate((element) => element.scrollWidth - element.clientWidth);
    expect(sectionOverflow, `${section} section does not overflow at ${width}px`).toBeLessThanOrEqual(tolerance);

    const box = (await locator.boundingBox())!;
    expect(box.x, `${section} section inside the left edge at ${width}px`).toBeGreaterThanOrEqual(-tolerance);
    expect(box.x + box.width, `${section} section inside the right edge at ${width}px`).toBeLessThanOrEqual(
      width + tolerance
    );
  }
}

/** The three amounts: one row above `sm`, one column below it. */
async function expectAmountsLayout(page: Page, width: number): Promise<void> {
  const exGst = (await page.locator('#expense-amount-ex-gst').boundingBox())!;
  const gst = (await page.locator('#expense-gst').boundingBox())!;
  const total = (await page.locator('#expense-total').boundingBox())!;

  if (width >= 640) {
    expect(Math.abs(exGst.y - gst.y), `amounts on one row at ${width}px`).toBeLessThanOrEqual(2);
    expect(Math.abs(gst.y - total.y), `amounts on one row at ${width}px`).toBeLessThanOrEqual(2);
    expect(exGst.x + exGst.width, `Amount ex GST left of GST at ${width}px`).toBeLessThanOrEqual(gst.x + tolerance);
  } else {
    expect(gst.y, `GST stacks below Amount ex GST at ${width}px`).toBeGreaterThanOrEqual(
      exGst.y + exGst.height - tolerance
    );
    expect(total.y, `Total stacks below GST at ${width}px`).toBeGreaterThanOrEqual(gst.y + gst.height - tolerance);
    expect(Math.abs(exGst.x - total.x), `the stacked amounts share a left edge at ${width}px`).toBeLessThanOrEqual(2);
  }
}

/**
 * Issue #1187: at desktop width every section fills the content area beside the sidebar, the same
 * width the page header uses, instead of sitting in an 840px-capped column.
 */
async function expectFullWidthLayout(page: Page, width: number): Promise<void> {
  const header = (await page.locator('[data-testid="expense-form-header"]').boundingBox())!;

  for (const section of ['details', 'amounts', 'supporting']) {
    const box = (await page.locator(`[data-testid="expense-section-${section}"]`).boundingBox())!;
    expect(box.width, `${section} section is wider than 840px at ${width}px`).toBeGreaterThan(840);
    expect(Math.abs(box.x - header.x), `${section} section left edge lines up with the header at ${width}px`).toBeLessThanOrEqual(
      2
    );
    expect(
      Math.abs(box.x + box.width - (header.x + header.width)),
      `${section} section right edge lines up with the header at ${width}px`
    ).toBeLessThanOrEqual(2);
  }
}

/**
 * One whole-page capture. The viewport is grown to the page rather than passing `fullPage`, which is
 * the idiom the other visual runs use.
 */
async function capture(page: Page, width: number, name: string): Promise<void> {
  await expectNoHorizontalOverflow(page, width);
  await expectAmountsLayout(page, width);
  if (width === 1440) {
    await expectFullWidthLayout(page, width);
  }
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  await page.setViewportSize({ width, height });
  await page.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`) });
}

/**
 * Everything Tab reaches on Edit expense, in reading order: the nine fields (Machine is the optional
 * one), the stored document's View document button where it sits - inside the Supporting document
 * field - and then the action bar.
 */
const editTabOrder = [
  '#expense-date',
  '#expense-category',
  '#expense-description',
  '#expense-amount-ex-gst',
  '#expense-gst',
  '#expense-total',
  '#expense-supplier',
  '#expense-machine',
  '#expense-attachment',
  '[data-testid="expense-view-document"]',
  '#expense-notes',
  '[data-testid="expense-form-cancel"]',
  '[data-testid="expense-form-submit"]'
];

test.use({ timezoneId: 'Australia/Sydney', locale: 'en-AU' });

test.describe('Add and Edit expense visual evidence (issue #1167)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of [1440, 390]) {
    test(`Add expense at ${width}px`, async ({ page }) => {
      await open(page, width, '/expenses/new', 'Add expense');
      await expect(page.locator('[data-testid="expense-form-submit"]')).toHaveText('Save expense');
      await expect(page.locator('[data-testid="expense-current-attachment"]')).toHaveCount(0);
      await capture(page, width, 'add-expense');
    });

    test(`Edit expense, with a stored supporting document, at ${width}px`, async ({ page }) => {
      await open(page, width, '/expenses/7/edit', 'Edit expense');
      await expect(page.locator('[data-testid="expense-form-submit"]')).toHaveText('Save changes');
      await expect(page.locator('[data-testid="expense-current-attachment"]')).toContainText(
        'sample-insurance-invoice-2026.pdf'
      );
      await expect(page.locator('[data-testid="expense-attachment-replacement-note"]')).toBeVisible();
      await capture(page, width, 'edit-expense');
    });
  }

  // 360px is the narrowest width the issue names, and is not captured: it is a measurement, and the
  // 390px capture is the phone-width evidence.
  for (const [url, heading] of [
    ['/expenses/new', 'Add expense'],
    ['/expenses/7/edit', 'Edit expense']
  ]) {
    test(`${heading} does not scroll sideways at 360px`, async ({ page }) => {
      await open(page, 360, url, heading);
      await expectNoHorizontalOverflow(page, 360);
      await expectAmountsLayout(page, 360);
    });
  }

  test('Tab walks the whole form in reading order and ends on the primary action', async ({ page }) => {
    await open(page, 1440, '/expenses/7/edit', 'Edit expense');

    // Back to expenses is the first thing in the page, ahead of the form it leaves.
    await page.locator('[data-testid="expense-form-back"]').focus();
    await page.keyboard.press('Tab');
    await expect(page.locator('#expense-date'), 'Tab reaches the first field').toBeFocused();

    // The walk then starts at Category: a date input is one control with three internal segments in
    // Chromium, and Tab steps through those segments before it leaves the control, which is the
    // browser's own date affordance rather than anything this page decides.
    const walkStart = editTabOrder.indexOf('#expense-category');
    await page.locator(editTabOrder[walkStart]).focus();

    for (const next of editTabOrder.slice(walkStart + 1)) {
      await page.keyboard.press('Tab');
      await expect(page.locator(next), `Tab reaches ${next}`).toBeFocused();
    }
  });

  test('the primary action and Cancel are at least 44px tall at 390px', async ({ page }) => {
    await open(page, 390, '/expenses/7/edit', 'Edit expense');
    const selectors = ['[data-testid="expense-form-submit"]', '[data-testid="expense-form-cancel"]'];
    const boxes = await Promise.all(selectors.map((selector) => page.locator(selector).boundingBox()));
    boxes.forEach((box, index) => expect(box!.height, `${selectors[index]} height`).toBeGreaterThanOrEqual(44));
  });
});
