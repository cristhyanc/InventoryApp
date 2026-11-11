import { Page, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { expectNoHorizontalOverflow, openProductsPage, supplierOffer as offer } from './support/products-page';

/**
 * The desktop and mobile browser evidence for issue #1183: the supplier comparison's
 * check-before-buying notice, the "Lowest unit price" badge that replaced "Cheapest", and the badge's
 * visible explainer.
 *
 * This is deliberately its own spec rather than another block in `products-redesign.visual.ts`. The
 * `e2e:visual` npm script chain forwards a positional path but swallows Playwright's own flags, so
 * `--grep` cannot narrow a shared file: running one issue's cases there re-renders every other
 * issue's committed captures, leaves them modified in the working tree, and the repair workflow then
 * refuses to publish. A file of its own is the unit the script can actually select, and it writes
 * only to `docs/screenshots/issue-1183/`.
 *
 * Like the other visual runs it starts no API and no database. Every `/api/` request is answered in
 * the browser with neutral synthetic offers, so no business data, account name or credential can
 * appear in a capture, and nothing is ever saved.
 *
 * What it measures, beyond taking a picture - the things jsdom cannot see:
 *
 *  - the notice is read **before** the results, with no hover and no scrolling: the dialog's scroller
 *    is still at the top when the comparison opens, the whole notice is inside that scroller's
 *    visible box, and the first supplier row begins below it;
 *  - the notice and the explainer really **wrap** (more than one line box) and are not clipped by
 *    their own box, do not scroll sideways, and stay inside the viewport, at 1440px and 390px;
 *  - **ranking and eligibility are unchanged**: the server's order is kept, a cheaper but ineligible
 *    offer is still shown with its reason and still takes no badge, a tie badges every tied offer,
 *    and a comparison whose only offer is ineligible badges nothing and explains nothing - while
 *    still showing the notice;
 *  - the related **Cheapest supplier help** is reachable and dismissable from the keyboard.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1183');
mkdirSync(screenshotDirectory, { recursive: true });

/** This spec sets every width itself, so it runs once rather than once per configured project. */
const PRIMARY_PROJECT = 'desktop-1440';

/** The exact owner-approved wording of issue #1183, as the operator must be able to read it. */
const NOTICE =
  'Check before buying: Prices and availability may change and can vary by location, membership or '
  + 'promotion. Comparisons use recorded unit prices from supported suppliers and exclude delivery '
  + 'and additional fees. Confirm the product, pack size and final total on the supplier’s '
  + 'website before purchasing.';

const EXPLAINER =
  'Lowest recorded unit price among the eligible offers shown, excluding delivery and additional '
  + 'fees. Check the supplier’s final price before buying.';

const LOWEST_UNIT_PRICE = 'Lowest unit price';

const categories = [
  { id: 1, name: 'Snacks' },
  { id: 2, name: 'Drinks' }
];

function product(id: number, name: string, sku: string, categoryId: number) {
  return {
    id,
    name,
    sku,
    description: null,
    averageUnitCost: 2,
    averageUnitCostForDisplay: 2,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: 8,
    maxStockInMachine: 10,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 20,
    needToOrder: 0,
    mdbCode: null,
    unit: 'ea',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId,
    category: categories.find((c) => c.id === categoryId) ?? null,
    isActive: true,
    isLowStock: false,
    isReorderAlert: false,
    canDelete: false,
    deletionRefusal: null
  };
}

const FOUR_OFFERS = 'Sample Iced Coffee Double Shot 237mL';
const TIED = 'Sample Cheese & Crackers 38g';
const INELIGIBLE_ONLY = 'Sample Hazelnut Wafer 43g';

const products = [product(1, FOUR_OFFERS, 'SMP-ICD-237', 2), product(2, TIED, 'SMP-CHC-38', 1), product(3, INELIGIBLE_ONLY, 'SMP-HZW-43', 1)];

const MATCHED = 3;
/** `SupplierOfferIneligibility.Stale`: rendered as "Last check is too old to compare". */
const STALE = 6;

/**
 * The server's own answers, in the server's own order. The frontend never re-ranks these, so the
 * fixtures are what pins the rendered ranking: in `FOUR_OFFERS` the fourth row is `Sample Bulk Club`
 * at **$1.50**, a lower unit price than the badged $1.84, but it is stale and therefore ineligible -
 * so it is listed with its reason and takes no badge.
 */
const cheapestSuppliers = {
  displayBasis: 2,
  products: [
    {
      productId: 1,
      trackingStatus: MATCHED,
      cheapestUnitPrice: 1.84,
      suppliers: [
        offer(2, 'Sample Grocer', 12, 22.08, 1.84, { isCheapest: true, isSpecial: true }),
        offer(3, 'Sample Market', 1, 2.2, 2.2),
        offer(1, 'Sample Wholesale South Coast', 24, 57.6, 2.4),
        offer(4, 'Sample Bulk Club', 6, 9, 1.5, { ineligibility: STALE, lastCheckedAtUtc: '2026-09-30T05:00:00Z' })
      ]
    },
    {
      productId: 2,
      trackingStatus: MATCHED,
      cheapestUnitPrice: 3,
      suppliers: [offer(2, 'Sample Grocer', 1, 3, 3, { isCheapest: true }), offer(3, 'Sample Market', 1, 3, 3, { isCheapest: true, isSpecial: true })]
    },
    {
      productId: 3,
      trackingStatus: MATCHED,
      cheapestUnitPrice: null,
      suppliers: [offer(2, 'Sample Grocer', 1, 1.35, 1.35, { ineligibility: STALE, lastCheckedAtUtc: '2026-09-28T05:00:00Z' })]
    }
  ]
};

function openProducts(page: Page, width: number): Promise<void> {
  return openProductsPage(page, width, { products, categories, purchaseCosts: [], cheapestSuppliers });
}

/** Opens one product's comparison and leaves it untouched - nothing scrolled, nothing hovered. */
async function openComparison(page: Page, width: number, productName: string, supplierCount: number) {
  await openProducts(page, width);
  const compare = page.getByRole('button', { name: `Compare ${supplierCount} supplier prices for ${productName}` });
  await compare.scrollIntoViewIfNeeded();
  await compare.click();

  const dialog = page.locator('dialog[open]');
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId('supplier-comparison-notice')).toBeVisible();
  return { dialog, compare };
}

/**
 * Measures the one thing a screenshot cannot prove on its own: that the notice is already fully
 * readable, above the results, in the dialog as it opens.
 *
 * The notice is a direct child of the dialog's scrollable content, so that element is its
 * `parentElement`; a scroller still at `scrollTop === 0` has not been scrolled past anything.
 */
function measureNotice(page: Page) {
  return page.evaluate(() => {
    const notice = document.querySelector('[data-testid="supplier-comparison-notice"]');
    const row = document.querySelector('[data-testid="cheapest-supplier-row"]');
    if (notice === null || row === null) throw new Error('The comparison did not render its notice and rows.');
    const scroller = notice.parentElement;
    if (scroller === null) throw new Error('The notice has no scrolling container.');

    const paragraph = notice.querySelector('p');
    if (paragraph === null) throw new Error('The notice has no paragraph.');
    // One client rect per rendered line box: this is what "the text wraps" means in a browser.
    const range = document.createRange();
    range.selectNodeContents(paragraph);

    const box = notice.getBoundingClientRect();
    return {
      text: paragraph.textContent?.trim() ?? '',
      lines: range.getClientRects().length,
      scrollTop: scroller.scrollTop,
      scroller: {
        top: scroller.getBoundingClientRect().top,
        bottom: scroller.getBoundingClientRect().bottom,
        scrollWidth: scroller.scrollWidth,
        clientWidth: scroller.clientWidth
      },
      notice: {
        top: box.top,
        bottom: box.bottom,
        left: box.left,
        right: box.right,
        scrollWidth: notice.scrollWidth,
        clientWidth: notice.clientWidth,
        scrollHeight: notice.scrollHeight,
        clientHeight: notice.clientHeight
      },
      firstRowTop: row.getBoundingClientRect().top,
      viewport: window.innerWidth
    };
  });
}

/** A block of text that is not clipped by its own box, does not scroll sideways and wraps. */
function expectReadable(element: { lines: number; scrollWidth: number; clientWidth: number; scrollHeight: number; clientHeight: number; left: number; right: number }, viewport: number) {
  expect(element.lines).toBeGreaterThan(1);
  expect(element.scrollWidth).toBeLessThanOrEqual(element.clientWidth + 1);
  expect(element.scrollHeight).toBeLessThanOrEqual(element.clientHeight + 1);
  expect(element.left).toBeGreaterThanOrEqual(0);
  expect(element.right).toBeLessThanOrEqual(viewport);
}

function measureExplainer(page: Page) {
  return page.evaluate(() => {
    const explainer = document.querySelector('[data-testid="supplier-comparison-badge-explainer"]');
    if (explainer === null) throw new Error('The comparison did not render its badge explainer.');
    const range = document.createRange();
    range.selectNodeContents(explainer);
    const box = explainer.getBoundingClientRect();
    return {
      text: explainer.textContent?.trim() ?? '',
      lines: range.getClientRects().length,
      scrollWidth: explainer.scrollWidth,
      clientWidth: explainer.clientWidth,
      scrollHeight: explainer.scrollHeight,
      clientHeight: explainer.clientHeight,
      left: box.left,
      right: box.right,
      viewport: window.innerWidth
    };
  });
}

function capture(page: Page, name: string) {
  return page.screenshot({ path: join(screenshotDirectory, `${name}.png`) });
}

test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== PRIMARY_PROJECT, 'This spec sets each width itself, so it runs once.');
});

test.describe('Supplier comparison notice and Lowest unit price badge (issue #1183)', () => {
  for (const width of [1440, 390]) {
    test(`1 the notice is readable above the results as the comparison opens at ${width}px`, async ({ page }) => {
      await openComparison(page, width, FOUR_OFFERS, 4);

      const measured = await measureNotice(page);
      expect(measured.text).toBe(NOTICE);
      // Nothing has been scrolled past, and the whole notice is inside the scroller's visible box.
      expect(measured.scrollTop).toBe(0);
      expect(measured.notice.top).toBeGreaterThanOrEqual(measured.scroller.top - 1);
      expect(measured.notice.bottom).toBeLessThanOrEqual(measured.scroller.bottom + 1);
      // The results start below the notice, so it is read first.
      expect(measured.firstRowTop).toBeGreaterThanOrEqual(measured.notice.bottom - 1);
      expectReadable({ ...measured.notice, lines: measured.lines }, measured.viewport);
      // The comparison's own content does not scroll sideways either.
      expect(measured.scroller.scrollWidth).toBeLessThanOrEqual(measured.scroller.clientWidth + 1);
      await expectNoHorizontalOverflow(page);

      await capture(page, `supplier-comparison-notice-${width}px`);
    });
  }

  test('2 ranking and eligibility are unchanged: server order, one badge, a cheaper ineligible offer takes none', async ({ page }) => {
    const { dialog } = await openComparison(page, 1440, FOUR_OFFERS, 4);

    // The server's order, kept as given - the dialog never re-ranks.
    await expect(dialog.locator('[data-testid="cheapest-supplier-row"] td[data-label="Supplier"]')).toHaveText([
      /^Sample Grocer\s*Lowest unit price$/,
      /^Sample Market$/,
      /^Sample Wholesale South Coast$/,
      /^Sample Bulk Club$/
    ]);

    // Exactly one badge, on the first row only.
    const badges = dialog.getByText(LOWEST_UNIT_PRICE, { exact: true });
    await expect(badges).toHaveCount(1);
    const rows = dialog.locator('[data-testid="cheapest-supplier-row"]');
    await expect(rows.first()).toContainText(LOWEST_UNIT_PRICE);

    // Sample Bulk Club's $1.50 is lower than the badged $1.84, but it is stale: it is still shown,
    // with its reason, and it is not badged. Eligibility, not the raw number, decides the badge.
    const ineligible = rows.nth(3);
    await expect(ineligible).toContainText('$1.50');
    await expect(ineligible).toContainText('Last check is too old to compare');
    await expect(ineligible).not.toContainText(LOWEST_UNIT_PRICE);

    // The badge is explained once, in visible text, not on hover.
    await expect(dialog.getByTestId('supplier-comparison-badge-explainer')).toHaveCount(1);
    const explainer = await measureExplainer(page);
    expect(explainer.text).toBe(EXPLAINER);
    expectReadable(explainer, explainer.viewport);
  });

  test('3 a tie still badges every tied offer at 1440px', async ({ page }) => {
    const { dialog } = await openComparison(page, 1440, TIED, 2);

    const rows = dialog.locator('[data-testid="cheapest-supplier-row"]');
    await expect(rows).toHaveCount(2);
    await expect(dialog.getByText(LOWEST_UNIT_PRICE, { exact: true })).toHaveCount(2);
    await expect(rows.nth(0)).toContainText('Sample Grocer');
    await expect(rows.nth(1)).toContainText('Sample Market');
    await expect(dialog.getByTestId('supplier-comparison-badge-explainer')).toHaveCount(1);
    await expectNoHorizontalOverflow(page);

    await capture(page, 'supplier-comparison-tied-badges-1440px');
  });

  test('4 an ineligible-only comparison badges nothing, explains nothing, and still carries the notice', async ({ page }) => {
    const { dialog } = await openComparison(page, 1440, INELIGIBLE_ONLY, 1);

    await expect(dialog.getByText(LOWEST_UNIT_PRICE, { exact: true })).toHaveCount(0);
    await expect(dialog.getByTestId('supplier-comparison-badge-explainer')).toHaveCount(0);
    await expect(dialog.locator('[data-testid="cheapest-supplier-row"]')).toContainText('Last check is too old to compare');
    // The notice is not conditional on anything being badged.
    const measured = await measureNotice(page);
    expect(measured.text).toBe(NOTICE);
    expect(measured.scrollTop).toBe(0);
    await expectNoHorizontalOverflow(page);

    await capture(page, 'supplier-comparison-no-badge-1440px');
  });

  test('5 the badge explainer is readable on a phone, after the dialog scrolls its own content', async ({ page }) => {
    const { dialog } = await openComparison(page, 390, FOUR_OFFERS, 4);

    const explainerElement = dialog.getByTestId('supplier-comparison-badge-explainer');
    await explainerElement.scrollIntoViewIfNeeded();
    await expect(explainerElement).toBeVisible();

    const explainer = await measureExplainer(page);
    expect(explainer.text).toBe(EXPLAINER);
    expectReadable(explainer, explainer.viewport);
    // Scrolling the dialog's own content never scrolls the page sideways.
    await expectNoHorizontalOverflow(page);

    await capture(page, 'supplier-comparison-explainer-390px');
  });
});

/**
 * The related lowest-unit-price help: the Products page's own **Cheapest supplier** column
 * toggletip. Issue #1183 left this column's name and help text alone - the badge is explained in
 * visible helper text rather than on hover, so the dialog has no tooltip of its own - but the
 * issue's keyboard criterion still has to hold for the help an operator reaches from the same
 * surface, so it is asserted here rather than assumed.
 */
test.describe('The related Cheapest supplier help stays reachable from the keyboard (issue #1183)', () => {
  for (const width of [1440, 390]) {
    test(`6 the Cheapest supplier help opens and closes from the keyboard at ${width}px`, async ({ page }) => {
      await openProducts(page, width);
      const trigger = page.locator('button[aria-label="About Cheapest supplier"]:visible').first();
      await trigger.scrollIntoViewIfNeeded();
      await trigger.focus();
      await page.keyboard.press('Enter');

      const panel = page.getByTestId('help-toggletip-panel');
      await expect(panel).toBeVisible();
      await expect(trigger).toHaveAttribute('aria-expanded', 'true');

      // The panel wraps inside its own width: one that scrolls sideways is wider than its client box.
      const size = await panel.evaluate((element) => {
        const box = element.getBoundingClientRect();
        return { scrollWidth: element.scrollWidth, clientWidth: element.clientWidth, left: box.left, right: box.right, viewport: window.innerWidth };
      });
      expect(size.scrollWidth).toBeLessThanOrEqual(size.clientWidth + 1);
      expect(size.left).toBeGreaterThanOrEqual(0);
      expect(size.right).toBeLessThanOrEqual(size.viewport);
      await expectNoHorizontalOverflow(page);

      await capture(page, `cheapest-supplier-help-keyboard-${width}px`);

      // Escape closes it and hands the keyboard back to the control it was opened from.
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('help-toggletip-panel')).toHaveCount(0);
      await expect(trigger).toBeFocused();
    });
  }
});
