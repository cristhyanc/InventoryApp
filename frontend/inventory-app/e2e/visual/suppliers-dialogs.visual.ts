import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The browser-level evidence for issue #1174: the Suppliers page's Add/Edit dialog at the widths the
 * issue names - the empty Add modal and a linked supplier's Edit modal at 1440px, and the full-screen
 * phone sheet at 390px and 360px.
 *
 * It exists because jsdom computes no layout and has no native `<dialog>` top layer, so
 * `supplier-form-dialog.component.spec.ts` and `modal-dialog.component.spec.ts` can prove the markup,
 * the labelling and the Tab cycle but not one number the issue asks for: the ~600px desktop panel
 * bounded by the viewport, the body that scrolls while the action row stays put, the full-screen
 * sheet, the absence of sideways scroll, and the 44px touch targets. Those are measured here, in
 * Chromium, against the real component CSS.
 *
 * Like every other visual run it starts no API and no database: each `/api/` request is answered in
 * the browser with neutral synthetic suppliers, so no business name, contact, phone, email or address
 * can appear in a capture. **It writes nothing**: every non-`GET` is refused, so a run that ever
 * drifted into creating, updating or deleting a supplier would fail rather than quietly photograph a
 * save. It is human-invoked and is not part of `scripts/validate.sh`.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1174');
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

/**
 * Two synthetic suppliers: one the business added itself, and one that came from the platform's
 * supplier list, which is the row whose Edit the issue asks to see - its Name and Website are locked
 * with the explanatory note (issue #934), and it has no Delete.
 */
const suppliers = [
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

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function answerApi(page: Page): Promise<void> {
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
    if (path.endsWith('/suppliers/available-presets')) return json(route, []);
    if (path.endsWith('/suppliers')) return json(route, suppliers);
    return json(route, []);
  });
}

async function openSuppliers(page: Page, width: number, height: number): Promise<void> {
  await page.setViewportSize({ width, height });
  await answerApi(page);
  await page.goto('/suppliers');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }'
  });
  await expect(page.getByRole('heading', { name: 'Suppliers', level: 1 })).toBeVisible();
  await expect(page.locator('[data-testid="supplier-edit-7"]')).toBeVisible();
}

const dialog = '[data-testid="supplier-form-dialog"]';

/** Opens the empty Add dialog from the header control that the focus check then expects back. */
async function openAdd(page: Page): Promise<void> {
  await page.locator('[data-testid="add-supplier"]').click();
  await expect(page.locator('[data-testid="supplier-form-title"]')).toHaveText('Add supplier');
  await expect(page.locator('#supplierName')).toHaveValue('');
}

/** Opens the linked supplier's Edit dialog, whose Name and Website are locked (issue #934). */
async function openLinkedEdit(page: Page): Promise<void> {
  await page.locator('[data-testid="supplier-edit-7"]').click();
  await expect(page.locator('[data-testid="supplier-form-title"]')).toHaveText('Edit supplier');
  await expect(page.locator('#supplierName')).toHaveValue('Sample Platform Supplier');
}

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

/**
 * Nothing scrolls sideways - neither the page behind the dialog, nor the panel, nor its scrollable
 * body - and the panel itself stays inside the viewport.
 */
async function expectNoHorizontalOverflow(page: Page, width: number): Promise<void> {
  const overflow = await page.evaluate(() => {
    const panel = document.querySelector<HTMLElement>('[data-testid="supplier-form-dialog"]');
    const body = panel?.querySelector<HTMLElement>('.app-dialog-body');
    return {
      page: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      panel: panel === null || panel === undefined ? 0 : panel.scrollWidth - panel.clientWidth,
      body: body === null || body === undefined ? 0 : body.scrollWidth - body.clientWidth
    };
  });

  expect(overflow.page, `the page does not scroll sideways at ${width}px`).toBeLessThanOrEqual(tolerance);
  expect(overflow.panel, `the dialog panel does not scroll sideways at ${width}px`).toBeLessThanOrEqual(tolerance);
  expect(overflow.body, `the dialog body does not scroll sideways at ${width}px`).toBeLessThanOrEqual(tolerance);

  const box = (await page.locator(dialog).boundingBox())!;
  expect(box.x, `the panel's left edge is inside the viewport at ${width}px`).toBeGreaterThanOrEqual(-tolerance);
  expect(box.x + box.width, `the panel's right edge is inside the viewport at ${width}px`).toBeLessThanOrEqual(
    width + tolerance
  );
}

/** Pinch zoom stays available: the shell's viewport meta locks neither the scale nor the user. */
async function expectPinchZoomAllowed(page: Page): Promise<void> {
  const viewport = await page.locator('meta[name="viewport"]').getAttribute('content');
  expect(viewport, 'the shell declares a viewport').toBeTruthy();
  expect(viewport ?? '').not.toMatch(/user-scalable\s*=\s*no|user-scalable\s*=\s*0|maximum-scale\s*=\s*1(\.0)?\b/);
}

/** The minimum rendered height of each visible element the selector matches. */
function minHeight(page: Page, selector: string): Promise<number> {
  return page
    .locator(selector)
    .evaluateAll((elements) =>
      Math.min(
        ...elements
          .filter((element) => (element as HTMLElement).offsetParent !== null || element.closest('dialog') !== null)
          .map((element) => element.getBoundingClientRect().height)
      )
    );
}

/** Every control the phone sheet offers is at least a 44px touch target. */
async function expectMobileTouchTargets(page: Page, width: number): Promise<void> {
  const selectors = [
    `${dialog} .app-dialog-close`,
    `${dialog} [data-testid="supplier-form-submit"]`,
    `${dialog} .app-dialog-footer button`,
    `${dialog} input`,
    `${dialog} textarea`
  ];

  for (const selector of selectors) {
    expect(await minHeight(page, selector), `${selector} is at least 44px tall at ${width}px`).toBeGreaterThanOrEqual(44);
  }

  // The Close control is square, not just tall: a 44px-high sliver is not a 44px target.
  const close = (await page.locator(`${dialog} .app-dialog-close`).boundingBox())!;
  expect(close.width, `the Close control is at least 44px wide at ${width}px`).toBeGreaterThanOrEqual(44);
}

/** A capture of the viewport: a modal `<dialog>` lives in the top layer, which does not scroll. */
async function capture(page: Page, width: number, name: string): Promise<void> {
  await expectNoHorizontalOverflow(page, width);
  await page.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`) });
}

test.describe('The Suppliers page dialogs (issue #1174)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  test('the empty Add dialog at 1440px', async ({ page }) => {
    await openSuppliers(page, 1440, 900);
    await openAdd(page);

    const panel = (await page.locator(dialog).boundingBox())!;
    // About 600px wide (max-width: 37.5rem) and bounded by the window, with the body as the one
    // thing that scrolls: the desktop half of the issue's geometry criterion.
    expect(panel.width, 'the desktop panel is about 600px wide').toBeCloseTo(600, 0);
    expect(panel.height, 'the desktop panel stays inside the window').toBeLessThanOrEqual(900 - 32 + tolerance);
    await expect(page.locator(`${dialog} .app-dialog-body`)).toHaveCSS('overflow-y', 'auto');

    // The six fields, the two actions and the labelled Close control.
    await expect(page.locator(`${dialog} [data-testid="supplier-form-submit"]`)).toHaveText('Create supplier');
    await expect(page.locator(`${dialog} .app-dialog-close`)).toHaveAttribute('aria-label', 'Close');
    await expect(page.locator('[data-testid="supplier-locked-note"]')).toHaveCount(0);
    // Contact name and Phone share a row above `sm`, which is the desktop pairing the issue allows.
    const contact = (await page.locator('#supplierContactName').boundingBox())!;
    const phone = (await page.locator('#supplierPhone').boundingBox())!;
    expect(phone.x, 'Contact name and Phone sit side by side at 1440px').toBeGreaterThanOrEqual(
      contact.x + contact.width - tolerance
    );
    // Initial focus is the first field the operator can edit.
    await expect(page.locator('#supplierName')).toBeFocused();

    await expectPinchZoomAllowed(page);
    await capture(page, 1440, 'add-supplier-dialog');
  });

  test('a linked supplier in the Edit dialog at 1440px', async ({ page }) => {
    await openSuppliers(page, 1440, 900);
    await openLinkedEdit(page);

    const panel = (await page.locator(dialog).boundingBox())!;
    expect(panel.width, 'the desktop panel is about 600px wide').toBeCloseTo(600, 0);

    // The #934 lock, with its note, and the first editable field focused instead of the locked Name.
    await expect(page.locator('[data-testid="supplier-locked-note"]')).toContainText(
      "comes from the platform's supplier list"
    );
    await expect(page.locator('#supplierName')).toHaveAttribute('readonly', '');
    await expect(page.locator('#supplierUrl')).toHaveAttribute('readonly', '');
    await expect(page.locator('#supplierContactName')).toBeFocused();
    await expect(page.locator(`${dialog} [data-testid="supplier-form-submit"]`)).toHaveText('Save changes');

    await capture(page, 1440, 'edit-linked-supplier-dialog');
  });

  // The phone sheet at both widths the issue names: a full-screen dialog, 44px targets and zoom.
  for (const [width, height] of [
    [390, 844],
    [360, 640]
  ]) {
    test(`the full-screen form at ${width}px`, async ({ page }) => {
      await openSuppliers(page, width, height);
      await openAdd(page);

      const panel = (await page.locator(dialog).boundingBox())!;
      expect(panel.width, `the sheet fills the width at ${width}px`).toBeCloseTo(width, 0);
      expect(panel.height, `the sheet fills the height at ${width}px`).toBeCloseTo(height, 0);
      expect(panel.y, `the sheet starts at the top at ${width}px`).toBeCloseTo(0, 0);
      await expect(page.locator(dialog)).toHaveCSS('border-radius', '0px');

      // Mobile stacks the fields that pair on a desktop.
      const contact = (await page.locator('#supplierContactName').boundingBox())!;
      const phone = (await page.locator('#supplierPhone').boundingBox())!;
      expect(phone.y, `Contact name and Phone stack at ${width}px`).toBeGreaterThanOrEqual(
        contact.y + contact.height - tolerance
      );

      // The action row stays inside the sheet above a body that scrolls, rather than being pushed
      // off the bottom by the fields - the keyboard-aware part of the issue's mobile criterion.
      const footer = (await page.locator(`${dialog} .app-dialog-footer`).boundingBox())!;
      expect(footer.y + footer.height, `the action row is inside the viewport at ${width}px`).toBeLessThanOrEqual(
        height + tolerance
      );
      const body = await page
        .locator(`${dialog} .app-dialog-body`)
        .evaluate((element) => ({ scrollHeight: element.scrollHeight, clientHeight: element.clientHeight }));
      expect(body.scrollHeight, `the body is what scrolls at ${width}px`).toBeGreaterThanOrEqual(body.clientHeight);

      await expectMobileTouchTargets(page, width);
      await expectPinchZoomAllowed(page);
      await capture(page, width, 'add-supplier-dialog');
    });
  }

  test('Escape dismisses an unchanged form and gives focus back to the control that opened it', async ({ page }) => {
    await openSuppliers(page, 1440, 900);

    // From the header's Add supplier.
    await openAdd(page);
    await page.keyboard.press('Escape');
    await expect(page.locator(dialog)).toHaveCount(0);
    await expect(page.locator('[data-testid="add-supplier"]')).toBeFocused();

    // And from a row's Edit, which is a different opener on the same page.
    await openLinkedEdit(page);
    await page.keyboard.press('Escape');
    await expect(page.locator(dialog)).toHaveCount(0);
    await expect(page.locator('[data-testid="supplier-edit-7"]')).toBeFocused();
  });

  test('Escape on a changed form asks before discarding, and keeps every value', async ({ page }) => {
    await openSuppliers(page, 1440, 900);
    await openAdd(page);

    await page.locator('#supplierName').fill('Sample New Supplier');
    await page.locator('#supplierPhone').fill('07 0000 0000');
    await page.keyboard.press('Escape');

    // The question is a second dialog over the form, which is still there behind it.
    const discard = page.locator('[data-testid="supplier-discard-dialog"]');
    await expect(discard).toBeVisible();
    await expect(page.locator(dialog)).toHaveCount(1);
    await capture(page, 1440, 'discard-changes-dialog');

    // The safe answer is the nominated initial focus, so Enter alone cannot discard the work. Its
    // footer button is addressed directly because this shell's Close control is labelled
    // "Keep editing" too, which is the point: either dismissal keeps the form.
    const keepEditing = discard.locator('.app-dialog-footer').getByRole('button', { name: 'Keep editing' });
    await expect(keepEditing).toBeFocused();
    await keepEditing.click();
    await expect(discard).toHaveCount(0);
    await expect(page.locator('#supplierName')).toHaveValue('Sample New Supplier');
    await expect(page.locator('#supplierPhone')).toHaveValue('07 0000 0000');

    // Escape asks again rather than closing, and discarding returns focus to the opener.
    await page.locator('#supplierName').focus();
    await page.keyboard.press('Escape');
    await expect(discard).toBeVisible();
    await discard.getByRole('button', { name: 'Discard changes' }).click();
    await expect(page.locator(dialog)).toHaveCount(0);
    await expect(page.locator('[data-testid="add-supplier"]')).toBeFocused();
  });

  // 360px is measured for the Edit dialog too: the locked note is the longest unbreakable block of
  // text either dialog renders, and it is the one most likely to push the sheet sideways.
  test('the linked supplier Edit sheet does not scroll sideways at 360px', async ({ page }) => {
    await openSuppliers(page, 360, 640);
    await openLinkedEdit(page);

    await expectNoHorizontalOverflow(page, 360);
    await expectMobileTouchTargets(page, 360);
  });
});
