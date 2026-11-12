import { Page, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import {
  SAMPLE_SUPPLIERS,
  SuppliersPageData,
  TOLERANCE as tolerance,
  expectNoPageHorizontalOverflow,
  expectPinchZoomAllowed,
  expectSquareTouchTargets,
  expectTouchTargets,
  openSuppliersPage
} from './support/suppliers-page';

/**
 * The browser-level evidence for issue #1175: the redesigned Suppliers register at the three widths
 * the issue names - the desktop list at 1440px and the phone cards at 390px and 360px - with a
 * deliberately long supplier name and a long website among the synthetic rows.
 *
 * It exists because jsdom computes no layout, so `supplier-list.component.spec.ts` can prove the
 * markup, the search, the usage filter, the states and the row actions but not one number the issue
 * asks for: that the search box, the All/In use/Not used filter and **Add supplier** are all
 * reachable with no page-level sideways scroll at 360px, that every control the phone layout offers
 * is a 44px touch target, that pinch zoom is still permitted, that a row's overflow menu is
 * keyboard navigable, and that opening and closing the Add/Edit dialog leaves the search, the usage
 * filter and the scroll position exactly as they were. Those are measured here, in Chromium,
 * against the real component CSS.
 *
 * Like every other visual run it starts no API and no database: each `/api/` request is answered in
 * the browser with the neutral synthetic suppliers of `support/suppliers-page.ts`, so no business
 * name, contact, phone, email or address can appear in a capture, and **nothing writes** - every
 * non-`GET` is refused, so a run that drifted into a create, an update, a delete or a "Not used"
 * switch would fail rather than quietly photograph it. It is human-invoked and is not part of
 * `scripts/validate.sh`.
 *
 * One boundary worth stating: the measured controls are the register's own - the toolbar, **Add
 * supplier**, each card's Edit and overflow trigger and the menu items. The **Available to add**
 * section below the register, which issue #1175 restyles with its behaviour frozen, keeps the
 * compact row control it had before the redesign and is captured but not measured.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1175');
mkdirSync(screenshotDirectory, { recursive: true });

/** A supplier that came from the platform's list: locked name and website, and never deletable. */
function linked(id: number, name: string, url: string | null, isHidden: boolean, contact: Record<string, unknown> = {}) {
  return {
    id,
    name,
    contactName: null,
    phone: null,
    email: null,
    address: null,
    url,
    isLinked: true,
    isHidden,
    canRename: false,
    canDelete: false,
    ...contact
  };
}

/** A supplier the business added itself: always "In use", and deletable unless the API says not. */
function custom(id: number, name: string, url: string | null, canDelete: boolean, contact: Record<string, unknown> = {}) {
  return {
    id,
    name,
    contactName: null,
    phone: null,
    email: null,
    address: null,
    url,
    isLinked: false,
    isHidden: false,
    canRename: true,
    canDelete,
    ...contact
  };
}

/** The one row the issue's "long names and URLs stay usable" criterion is measured on. */
const LONG_NAME =
  'Sample Regional Wholesale Distributors and Vending Consumables Supply Company (Northern Rivers Division)';
const LONG_URL =
  'https://orders.sample-regional-wholesale-distributors-and-vending-consumables.invalid/customer/catalogue';

/**
 * The row that proves an unsafe stored website is never rendered as a link (issue #847). The value
 * is deliberately inert: it carries no scheme at all, so it is not a web address the register could
 * link to and not an address of any other kind either - there is nothing here for a browser, or for
 * a reader of this file, to navigate to.
 */
const UNUSABLE_WEBSITE_SUPPLIER = 'Sample Depot (unusable website recorded)';
const UNUSABLE_URL = 'sample-depot.invalid/catalogue';

/**
 * Ten synthetic suppliers: the pair the dialog evidence uses, the long-name/long-website row, two
 * more marked "Not used", a row with no contact details and no website at all, a row whose stored
 * website is not a usable web address (so it stays plain text, issue #847), and a custom row the
 * API refuses to delete. Together they put every state of the Supplier, Contact, Website, Usage and
 * Actions columns on screen in one capture.
 */
const suppliers = [
  ...SAMPLE_SUPPLIERS,
  linked(11, LONG_NAME, LONG_URL, true, {
    contactName: 'Sample Regional Account Management Team',
    email: 'regional.orders.northern.rivers@sample-regional-wholesale.invalid',
    phone: '02 0000 0011'
  }),
  custom(12, 'Sample Depot (no details recorded)', null, true),
  custom(13, UNUSABLE_WEBSITE_SUPPLIER, UNUSABLE_URL, true, {
    contactName: 'Sample Depot Office'
  }),
  linked(14, 'Sample Beverage Distributors', 'https://sample-beverages.invalid', false, {
    email: 'orders@sample-beverages.invalid'
  }),
  linked(16, 'Sample Confectionery Wholesale', 'https://sample-confectionery.invalid', true, {
    phone: '07 0000 0016'
  }),
  custom(17, 'Sample Paper Goods Supply', 'https://sample-paper-goods.invalid', false, {
    contactName: 'Sample Paper Office',
    phone: '08 0000 0017'
  }),
  custom(18, 'Sample Snack Importers', 'https://sample-snacks.invalid', true, {
    email: 'accounts@sample-snacks.invalid'
  }),
  custom(19, 'Sample Vending Supplies North', 'https://sample-vending-north.invalid', true, {
    contactName: 'Sample North Office',
    email: 'orders@sample-vending-north.invalid',
    phone: '02 0000 0019'
  })
];

/** Two records from the platform's list, so the **Available to add** section is on the page too. */
const presets = [
  { id: 31, name: 'Sample Statewide Vending Supply', url: 'https://sample-statewide-vending-supply.invalid' },
  { id: 32, name: 'Sample Coastal Beverage Wholesale', url: 'https://sample-coastal-beverage-wholesale.invalid' }
];

const data: SuppliersPageData = { suppliers, presets };

const search = '[data-testid="suppliers-search"]';
const usageButtons = [
  '[data-testid="suppliers-usage-all"]',
  '[data-testid="suppliers-usage-in-use"]',
  '[data-testid="suppliers-usage-not-used"]'
];
const addSupplier = '[data-testid="add-supplier"]';

/** Opens the register at one viewport with the ten synthetic suppliers loaded. */
async function openRegister(page: Page, width: number, height: number): Promise<void> {
  await openSuppliersPage(page, width, height, data);
  await expect(page.locator('[data-testid="suppliers-count"]')).toHaveText('10 suppliers');
}

/** Every control is inside the viewport horizontally: "reachable" in the issue's own sense. */
async function expectInsideViewport(page: Page, width: number, selectors: string[]): Promise<void> {
  for (const selector of selectors) {
    const box = (await page.locator(selector).boundingBox())!;
    expect(box.x, `${selector} starts inside the viewport at ${width}px`).toBeGreaterThanOrEqual(-tolerance);
    expect(box.x + box.width, `${selector} ends inside the viewport at ${width}px`).toBeLessThanOrEqual(width + tolerance);
  }
}

/**
 * The toolbar above the cards, and the controls one card on screen offers: `supplierId` names a
 * supplier the current search and usage filter actually leave rendered.
 */
async function expectMobileTouchTargets(page: Page, width: number, supplierId: number): Promise<void> {
  await expectTouchTargets(page, width, [
    addSupplier,
    search,
    ...usageButtons,
    `[data-testid="supplier-edit-${supplierId}"]:visible`,
    `[data-testid="supplier-card-actions-${supplierId}"]`
  ]);
  // The overflow trigger is square, not a tall sliver: it carries an icon and no label.
  await expectSquareTouchTargets(page, width, [`[data-testid="supplier-card-actions-${supplierId}"]`]);
}

async function capture(page: Page, width: number, name: string, fullPage = true): Promise<void> {
  await expectNoPageHorizontalOverflow(page, width);
  await page.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`), fullPage });
}

test.describe('The Suppliers register (issue #1175)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  test('1 the desktop register at 1440px', async ({ page }) => {
    await openRegister(page, 1440, 900);

    await expect(page.locator('[data-testid="suppliers-table"] thead th')).toHaveText([
      'Supplier',
      'Contact',
      'Website',
      'Usage',
      'Actions'
    ]);
    await expect(page.locator('[data-testid="supplier-row"]')).toHaveCount(10);
    await expect(page.locator('[data-testid="supplier-cards"]')).toBeHidden();

    // The badge marks the rows that came from the platform's list, and only those (issue #934).
    await expect(page.locator('[data-testid="suppliers-table"] .badge', { hasText: 'Platform list' })).toHaveCount(4);

    // A safe website is its short domain behind a link to the full stored address; one that is not
    // a usable web address stays plain text, with nothing to navigate to (issue #847).
    const website = page.getByRole('link', { name: `Website for ${LONG_NAME}, opens in a new tab` });
    await expect(website).toHaveText(/orders\.sample-regional-wholesale-distributors-and-vending-consumables\.invalid/);
    await expect(website).toHaveAttribute('href', LONG_URL);

    const unusable = page.locator('[data-testid="supplier-row"]').filter({ hasText: UNUSABLE_WEBSITE_SUPPLIER });
    await expect(unusable).toContainText(UNUSABLE_URL);
    await expect(
      page.getByRole('link', { name: `Website for ${UNUSABLE_WEBSITE_SUPPLIER}, opens in a new tab` })
    ).toHaveCount(0);
    // The whole row offers no link at all: the refused value is text, not something to follow.
    await expect(unusable.getByRole('link')).toHaveCount(0);

    // The long name and the long domain wrap inside their own columns rather than widening the
    // table past the card that holds it: the issue's "long names and URLs stay usable" criterion.
    const longRow = (await page.locator('[data-testid="supplier-row"]').filter({ hasText: LONG_NAME }).boundingBox())!;
    expect(longRow.x + longRow.width, 'the long-name row stays inside the viewport at 1440px').toBeLessThanOrEqual(
      1440 + tolerance
    );
    const table = await page
      .locator('[data-testid="suppliers-table"]')
      .evaluate((element) => ({ scrollWidth: element.scrollWidth, clientWidth: element.clientWidth }));
    expect(table.scrollWidth, 'the table needs no sideways scroll of its own at 1440px').toBeLessThanOrEqual(
      table.clientWidth + tolerance
    );

    // The platform-list section stays below the register, with its own heading (issue #935).
    await expect(page.locator('[data-testid="available-presets"] h2')).toHaveText('Available to add');

    await expectPinchZoomAllowed(page);
    await capture(page, 1440, 'suppliers-register');
  });

  // The phone layout at both widths the issue names.
  for (const [width, height] of [
    [390, 844],
    [360, 640]
  ]) {
    test(`2 the phone register at ${width}px`, async ({ page }) => {
      await openRegister(page, width, height);

      await expect(page.locator('[data-testid="supplier-card"]')).toHaveCount(10);
      await expect(page.locator('[data-testid="suppliers-table"]')).toBeHidden();

      // Search, the usage filter and Add supplier are all on screen, with no page-level sideways
      // scroll and no control running past the viewport edge.
      await expectInsideViewport(page, width, [search, ...usageButtons, addSupplier]);
      await expectNoPageHorizontalOverflow(page, width);
      await expectMobileTouchTargets(page, width, 4);
      await expectPinchZoomAllowed(page);

      // The long name and the long domain wrap inside the card instead of widening the page.
      const card = page.locator('[data-testid="supplier-card"]').filter({ hasText: LONG_NAME });
      const box = (await card.boundingBox())!;
      expect(box.x + box.width, `the long-name card stays inside the viewport at ${width}px`).toBeLessThanOrEqual(
        width + tolerance
      );

      await capture(page, width, 'suppliers-register');
    });

    test(`3 the phone toolbar with a search and a usage filter at ${width}px`, async ({ page }) => {
      await openRegister(page, width, height);

      // Searching and filtering adds two more controls to the toolbar - Clear search inside the box
      // and Clear filters beside it - which have to wrap into the same 44px rows without pushing
      // the page sideways.
      await page.locator(search).fill('sample');
      await page.locator('[data-testid="suppliers-usage-not-used"]').click();
      await expect(page.locator('[data-testid="supplier-card"]')).toHaveCount(2);
      await expect(page.locator('[data-testid="suppliers-usage-not-used"]')).toHaveAttribute('aria-pressed', 'true');

      await expectInsideViewport(page, width, [
        search,
        ...usageButtons,
        addSupplier,
        '[data-testid="suppliers-search-clear"]',
        '[data-testid="suppliers-clear-filters"]'
      ]);
      await expectNoPageHorizontalOverflow(page, width);
      // Supplier 11 is the long-name row, and one of the two this filter leaves on screen.
      await expectMobileTouchTargets(page, width, 11);
      await expectTouchTargets(page, width, ['[data-testid="suppliers-clear-filters"]']);
      await expectSquareTouchTargets(page, width, ['[data-testid="suppliers-search-clear"]']);

      await capture(page, width, 'suppliers-register-search-and-filter');
    });
  }

  test('4 the search and the usage filter combine, down to no match, at 1440px', async ({ page }) => {
    await openRegister(page, 1440, 900);

    // The search alone.
    await page.locator(search).fill('beverage');
    await expect(page.locator('[data-testid="supplier-row"]')).toHaveCount(1);
    // The count is the whole loaded register, not the filtered rows.
    await expect(page.locator('[data-testid="suppliers-count"]')).toHaveText('10 suppliers');

    // Combined with a usage filter that excludes the one match, which is the no-match state.
    await page.locator('[data-testid="suppliers-usage-not-used"]').click();
    await expect(page.locator('[data-testid="suppliers-no-match"]')).toContainText('No suppliers match your search or filter.');
    await capture(page, 1440, 'suppliers-register-no-match', false);

    // Its own action clears both, and the whole register comes back.
    await page.locator('[data-testid="suppliers-reset"]').click();
    await expect(page.locator(search)).toHaveValue('');
    await expect(page.locator('[data-testid="suppliers-usage-all"]')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('[data-testid="supplier-row"]')).toHaveCount(10);
  });

  test("5 a row's overflow menu is keyboard navigable at 1440px", async ({ page }) => {
    await openRegister(page, 1440, 900);

    // The long-name row: a supplier from the platform's list that the business has marked "Not
    // used", so its menu holds GST defaults and the switch back, and no Delete (issue #934).
    const trigger = page.locator('[data-testid="supplier-actions-11"]');
    await trigger.focus();
    await page.keyboard.press('ArrowDown');

    const menu = page.locator('[data-testid="supplier-actions-11-panel"]');
    await expect(menu).toHaveAttribute('role', 'menu');
    const items = menu.getByRole('menuitem');
    await expect(items).toHaveText(['GST defaults', 'Mark as in use']);

    // Opening focuses the first item; Arrow Down/Up, Home and End walk the menu and wrap.
    await expect(items.nth(0)).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(items.nth(1)).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(items.nth(0)).toBeFocused();
    await page.keyboard.press('ArrowUp');
    await expect(items.nth(1)).toBeFocused();
    await page.keyboard.press('Home');
    await expect(items.nth(0)).toBeFocused();
    await page.keyboard.press('End');
    await expect(items.nth(1)).toBeFocused();
    await capture(page, 1440, 'suppliers-row-menu-open', false);

    // Escape closes it and gives focus back to the trigger, with nothing chosen and nothing written.
    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);
    await expect(trigger).toBeFocused();

    // A supplier the business added itself offers Delete instead of the usage switch, and only
    // while the API still allows it: row 17 is refused, row 19 is not.
    await page.locator('[data-testid="supplier-actions-19"]').click();
    await expect(page.locator('[data-testid="supplier-actions-19-panel"]').getByRole('menuitem')).toHaveText([
      'GST defaults',
      'Delete'
    ]);
    await page.keyboard.press('Escape');
    await page.locator('[data-testid="supplier-actions-17"]').click();
    await expect(page.locator('[data-testid="supplier-actions-17-panel"]').getByRole('menuitem')).toHaveText(['GST defaults']);
    await page.keyboard.press('Escape');
  });

  test("6 a card's overflow menu is keyboard navigable at 390px", async ({ page }) => {
    await openRegister(page, 390, 844);

    const trigger = page.locator('[data-testid="supplier-card-actions-11"]');
    await trigger.focus();
    await page.keyboard.press('Enter');

    const menu = page.locator('[data-testid="supplier-card-actions-11-panel"]');
    const items = menu.getByRole('menuitem');
    await expect(items).toHaveText(['GST defaults', 'Mark as in use']);
    await expect(items.nth(0)).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(items.nth(1)).toBeFocused();

    // The menu itself is a touch target too, and it stays inside the viewport beside the card.
    await expectTouchTargets(page, 390, [`[data-testid="supplier-card-actions-11-panel"] [role="menuitem"]`]);
    await expectInsideViewport(page, 390, ['[data-testid="supplier-card-actions-11-panel"]']);
    await capture(page, 390, 'suppliers-card-menu-open', false);

    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);
    await expect(trigger).toBeFocused();
  });

  test('7 opening and closing Add and Edit keeps the search, the usage filter and the scroll position at 1440px', async ({
    page
  }) => {
    await openRegister(page, 1440, 900);

    await page.locator(search).fill('sample');
    await page.locator('[data-testid="suppliers-usage-in-use"]').click();
    await expect(page.locator('[data-testid="supplier-row"]')).toHaveCount(8);

    const dialog = page.locator('[data-testid="supplier-form-dialog"]');
    const title = page.locator('[data-testid="supplier-form-title"]');
    const scrollY = (): Promise<number> => page.evaluate(() => window.scrollY);

    /** The register behind the dialog is untouched: same search, same filter, same rows. */
    async function expectRegisterUnchanged(what: string): Promise<void> {
      await expect(page.locator(search), `the search survives ${what}`).toHaveValue('sample');
      await expect(
        page.locator('[data-testid="suppliers-usage-in-use"]'),
        `the usage filter survives ${what}`
      ).toHaveAttribute('aria-pressed', 'true');
      await expect(page.locator('[data-testid="supplier-row"]'), `the filtered rows survive ${what}`).toHaveCount(8);
    }

    /**
     * One opener, from a scrolled register: the page is scrolled until the control sits just inside
     * the top of the viewport - a real offset, with the control on screen as an operator clicking
     * it would have it - and the dialog is then opened and dismissed from the keyboard. Focus,
     * rather than Playwright's click, is what puts the control under the keyboard, so the dialog's
     * own focus return (issue #1174) lands back on it and cannot be mistaken for the page moving.
     */
    async function expectDialogKeepsRegister(opener: string, expectedTitle: string): Promise<void> {
      const control = page.locator(opener);
      const top = await control.evaluate((element) => element.getBoundingClientRect().top + window.scrollY - 8);
      await page.evaluate((y) => window.scrollTo(0, y), top);
      await control.focus();

      const before = await scrollY();
      expect(before, `the register is scrolled before ${expectedTitle} opens`).toBeGreaterThan(0);

      await page.keyboard.press('Enter');
      await expect(title).toHaveText(expectedTitle);
      expect(await scrollY(), `the open ${expectedTitle} dialog leaves the register where it was`).toBeCloseTo(before, 0);

      // Dismissed without a change, so nothing is saved and the register is not reloaded.
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(control, `focus returns to the control that opened ${expectedTitle}`).toBeFocused();

      await expectRegisterUnchanged(expectedTitle);
      expect(await scrollY(), `the scroll position survives ${expectedTitle}`).toBeCloseTo(before, 0);
    }

    await expectDialogKeepsRegister('[data-testid="supplier-edit-19"]:visible', 'Edit supplier');
    await expectDialogKeepsRegister(addSupplier, 'Add supplier');
  });
});
