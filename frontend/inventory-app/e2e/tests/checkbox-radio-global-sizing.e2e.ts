import type { Locator, Page } from '@playwright/test';
import { actors, expect, productByName, seeded, test } from '../fixtures/harness';

/**
 * The rendered geometry of checkboxes the global `input, select, textarea` rule in `styles.scss`
 * used to stretch to the full input width, drawing the tick far from its own label (issue #872,
 * fixed globally for every checkbox and radio by issue #874). This covers the screens issue #874
 * lists that the E2E fixture can reach without writing data and without a seeded Nayax machine
 * fleet, and covers each of them at both viewports the issue names, 1440x900 and 390x844: Nayax
 * sale timestamp repair's two always-visible checkboxes, the Pick List "Products" multi-select
 * dropdown, the Needs Ordering row-selection checkbox and the product form's Active checkbox.
 * `avco-transition-checkbox-layout.e2e.ts` already covers the AVCO baseline screen at both.
 *
 * The last test here is about the other half of the same change: that excluding checkboxes did not
 * change what any ordinary input gets. It is in this file because it is the same cascade, measured
 * the same way - Take Inventory's counted-stock input carries the single-class `w-24` utility,
 * which has always overridden the global rule's `width: 100%` and must keep doing so.
 *
 * Four checkbox states the issue names are not covered here, for reasons this suite cannot change:
 * - The supplier preset dialog lives behind `/super-admin/supplier-presets`, gated by
 *   `platformAdminGuard`'s own `GET /api/admin/diagnostics/access` check against a configured
 *   Entra `(tid, oid)` pair. None of the three synthetic E2E actors (`business-a-owner`,
 *   `business-b-owner`, `no-membership`) is that pair, so the page is unreachable - not refused by
 *   business role, which an actor could be granted, but by an identity the fixture has no way to
 *   be.
 * - Machine restock sync (its "Show reconciled", select-all and per-event checkboxes) and the Pick
 *   List "Machines" dropdown both need a seeded Nayax machine. `E2ETestNayaxLynxClient`
 *   (`backend/InventoryApi/Adapters/Nayax/E2ETestNayaxLynxClient.cs`) always answers "no machines"
 *   for every business, by design, so no E2E business ever has one to open a machine detail page
 *   for or to list in that dropdown.
 * - The third checkbox on Nayax sale timestamp repair, `#repair-acknowledge`, renders only inside
 *   the `repair-confirmation` block, which needs a server preview that found at least one
 *   repairable sale. A preview reads the live Nayax last-sales window or an uploaded transaction
 *   export; the fixture has neither, and seeding mis-timestamped sales plus an evidence source is
 *   beyond what a read-only suite can arrange. Its two siblings in the same page, measured below,
 *   are the same bare `<input type="checkbox" class="mt-1">` inside the same
 *   `flex items-start gap-2` label, so nothing about it is styled differently - but it was not
 *   measured, and this comment is not a claim that it was.
 *
 * These are browser tests because jsdom computes no layout, and because jsdom resolves the cascade
 * by source order and ignores specificity altogether. The Jest spec
 * `src/app/design-system/checkbox-radio-global-sizing.spec.ts` covers the two things that are
 * decided in the compiled stylesheet rather than on screen - that no width, padding or border rule
 * matches a checkbox or radio at all, and that the exclusion kept the global rule's selector
 * weight, so single-class utilities still override it. Neither of those is "how many pixels wide is
 * it on screen", which is what this file answers.
 */
const desktopViewport = { width: 1440, height: 900 };
const narrowViewport = { width: 390, height: 844 };
const viewports = [desktopViewport, narrowViewport];

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

/** The issue's own ceiling: a native checkbox/radio box is at most 24px either way. */
function expectNativeSize(box: { width: number; height: number }, label: string) {
  expect(box.width, `${label}: width`).toBeLessThanOrEqual(24);
  expect(box.height, `${label}: height`).toBeLessThanOrEqual(24);
  // A box the narrow viewport squeezed to nothing would also pass the ceiling above.
  expect(box.width, `${label}: width`).toBeGreaterThanOrEqual(10);
}

/** The checkbox is on the same line as its label text, and the text starts after its right edge. */
function expectTextBeside(
  checkboxBox: { x: number; y: number; width: number; height: number },
  textBox: { x: number; y: number; width: number; height: number },
  label: string
) {
  expect(checkboxBox.y, `${label}: same line`).toBeLessThan(textBox.y + textBox.height);
  expect(textBox.y, `${label}: same line`).toBeLessThan(checkboxBox.y + checkboxBox.height);
  expect(textBox.x, `${label}: text beside the box`).toBeGreaterThanOrEqual(
    checkboxBox.x + checkboxBox.width - tolerance
  );
}

async function expectNoHorizontalOverflow(page: Page, label: string) {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `${label}: page overflow`).toBeLessThanOrEqual(tolerance);
}

async function boxOf(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const box = await locator.boundingBox();
  expect(box, 'the element has a laid-out box').not.toBeNull();
  return box!;
}

test('Nayax sale timestamp repair: both always-visible checkboxes sit immediately before their label text', async ({
  pageAs
}) => {
  const page = await pageAs(actors.businessAOwner);

  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    await page.goto('/admin/nayax-sale-timestamp-repair');

    for (const [id, labelText] of [
      ['#repair-api-source', 'Read the live Nayax last-sales window'],
      ['#repair-reconcile', 'Reconcile a business-date window against a fixed cutoff']
    ] as const) {
      const where = `${id} at ${viewport.width}x${viewport.height}`;
      const checkbox = page.locator(id);
      await expect(checkbox).toBeVisible();

      const checkboxBox = await boxOf(checkbox);
      expectNativeSize(checkboxBox, where);

      const label = page.locator('label').filter({ has: checkbox });
      expectTextBeside(checkboxBox, await boxOf(label.getByText(labelText)), where);
    }

    await expectNoHorizontalOverflow(page, `nayax-sale-timestamp-repair at ${viewport.width}`);
  }
});

test('Pick List Products dropdown: "Select all" and every option checkbox sit immediately before their label text', async ({
  pageAs
}) => {
  const page = await pageAs(actors.businessAOwner);

  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    await page.goto('/pick-list');

    const trigger = page.getByRole('button', { name: /^Products:/ });
    // Options load asynchronously after navigation; the staged selection seeds to "All products"
    // once the catalogue read finishes, which is this test's signal the dropdown has real options.
    await expect(trigger).toContainText('All products', { timeout: 10000 });
    await trigger.click();

    const selectAllLabel = page.locator('label').filter({ hasText: 'Select all' });
    const optionLabel = page.locator('label').filter({ hasText: seeded.reorderProduct });
    await expect(optionLabel).toBeVisible();

    for (const [label, text] of [
      [selectAllLabel, 'Select all'],
      [optionLabel, seeded.reorderProduct]
    ] as const) {
      const where = `pick list "${text}" at ${viewport.width}x${viewport.height}`;
      const checkboxBox = await boxOf(label.getByRole('checkbox'));
      expectNativeSize(checkboxBox, where);
      expectTextBeside(checkboxBox, await boxOf(label.getByText(text)), where);
    }
  }
});

test('Needs Ordering row-selection checkbox stays its native size and inside its own cell', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);

  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    await page.goto('/products/needs-ordering');

    const where = `needs-ordering row checkbox at ${viewport.width}x${viewport.height}`;
    const alertRow = page.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
    await expect(alertRow, 'the fixture seeds one alerting product for business A').toBeVisible();

    const checkbox = alertRow.getByRole('checkbox');
    const checkboxBox = await boxOf(checkbox);
    expectNativeSize(checkboxBox, where);

    // This checkbox has an `aria-label` and no visible label text, so "the label starts beside it"
    // is measured against the next cell instead: the product name must still start after the
    // checkbox's right edge, which a full-width box in a narrow table would not leave room for.
    const nameCell = alertRow.getByRole('cell').nth(1);
    expect(
      (await boxOf(nameCell)).x,
      `${where}: the product name cell starts after the checkbox`
    ).toBeGreaterThanOrEqual(checkboxBox.x + checkboxBox.width - tolerance);
  }
});

test('Product form Active checkbox sits immediately before its label text', async ({ pageAs, apiAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const api = await apiAs(actors.businessAOwner);
  const product = await productByName(api, seeded.reorderProduct);

  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    await page.goto(`/products/${product.id}/edit`);

    const where = `product form Active at ${viewport.width}x${viewport.height}`;
    const checkbox = page.locator('input[name="isActive"]');
    await expect(checkbox).toBeVisible();

    const checkboxBox = await boxOf(checkbox);
    expectNativeSize(checkboxBox, where);

    const label = page.locator('label').filter({ has: checkbox });
    expectTextBeside(checkboxBox, await boxOf(label.getByText('Active product')), where);
  }
});

test('an ordinary number input still takes its own width utility, not the global full width', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);

  for (const viewport of viewports) {
    await page.setViewportSize(viewport);
    await page.goto('/take-inventory');

    const where = `take-inventory counted stock at ${viewport.width}x${viewport.height}`;
    const countedStock = page
      .getByRole('spinbutton', { name: `Counted stock for ${seeded.reorderProduct}` })
      .first();
    await expect(countedStock).toBeVisible();

    // `class="w-24"` is 6rem. Read the root font size rather than assuming 16px, so the assertion
    // measures the utility and not the browser default.
    const expectedWidth = await page.evaluate(
      () => 6 * Number.parseFloat(getComputedStyle(document.documentElement).fontSize)
    );

    const box = await boxOf(countedStock);
    expect(box.width, `${where}: still 6rem, so the single-class utility still wins`).toBeCloseTo(
      expectedWidth,
      0
    );

    // The global rule's own padding and border are what the exclusion must have left alone.
    const style = await countedStock.evaluate((element) => {
      const computed = getComputedStyle(element);
      return {
        paddingLeft: computed.paddingLeft,
        paddingTop: computed.paddingTop,
        borderLeftWidth: computed.borderLeftWidth
      };
    });
    expect(style, `${where}: global text-input padding and border`).toEqual({
      paddingLeft: '12px',
      paddingTop: '8px',
      borderLeftWidth: '1px'
    });
  }
});
