import { actors, expect, productByName, seeded, test } from '../fixtures/harness';

/**
 * The rendered geometry of checkboxes the global `input, select, textarea` rule in `styles.scss`
 * used to stretch to the full input width, drawing the tick far from its own label (issue #872,
 * fixed globally for every checkbox and radio by issue #874). This covers the screens issue #874
 * lists that the E2E fixture can reach without writing data and without a seeded Nayax machine
 * fleet: Nayax sale timestamp repair's two always-visible checkboxes, the Pick List "Products"
 * multi-select dropdown, the Needs Ordering row-selection checkbox and the product form's Active
 * checkbox. `avco-transition-checkbox-layout.e2e.ts` already covers the AVCO baseline screen.
 *
 * Three of the issue's seven listed screens are not covered here, for reasons this suite cannot
 * change:
 * - The supplier preset dialog lives behind `/super-admin/supplier-presets`, gated by
 *   `platformAdminGuard`'s own `GET /api/admin/diagnostics/access` check against a configured
 *   Entra `(tid, oid)` pair. None of the three synthetic E2E actors (`business-a-owner`,
 *   `business-b-owner`, `no-membership`) is that pair, so the page is unreachable - not refused by
 *   business role, which an actor could be granted, but by an identity the fixture has no way to
 *   be.
 * - Machine restock sync and the Pick List "Machines" dropdown both need a seeded Nayax machine.
 *   `E2ETestNayaxLynxClient` (`backend/InventoryApi/Adapters/Nayax/E2ETestNayaxLynxClient.cs`)
 *   always answers "no machines" for every business, by design, so no E2E business ever has one
 *   to open a machine detail page for or to list in that dropdown.
 *
 * This is a browser test because jsdom computes no layout; the Jest spec beside `styles.scss`
 * (`src/app/probe.spec.ts`) asserts the underlying CSS cascade instead - that no width, padding or
 * border rule matches a checkbox or radio at all - which is the fix, but not "how many pixels wide
 * is it on screen".
 */
const desktopViewport = { width: 1440, height: 900 };
const narrowViewport = { width: 390, height: 844 };

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

/** The issue's own ceiling: a native checkbox/radio box is at most 24px either way. */
function expectNativeSize(box: { width: number; height: number }) {
  expect(box.width).toBeLessThanOrEqual(24);
  expect(box.height).toBeLessThanOrEqual(24);
}

test('Nayax sale timestamp repair: both always-visible checkboxes sit immediately before their label text', async ({
  pageAs
}) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(desktopViewport);
  await page.goto('/admin/nayax-sale-timestamp-repair');

  for (const [id, labelText] of [
    ['#repair-api-source', 'Read the live Nayax last-sales window'],
    ['#repair-reconcile', 'Reconcile a business-date window against a fixed cutoff']
  ] as const) {
    const checkbox = page.locator(id);
    await expect(checkbox).toBeVisible();

    const checkboxBox = (await checkbox.boundingBox())!;
    expectNativeSize(checkboxBox);

    const label = page.locator('label').filter({ has: checkbox });
    const textBox = (await label.getByText(labelText).boundingBox())!;

    // Same line, text starting at or just after the checkbox's right edge.
    expect(checkboxBox.y).toBeLessThan(textBox.y + textBox.height);
    expect(textBox.y).toBeLessThan(checkboxBox.y + checkboxBox.height);
    expect(textBox.x).toBeGreaterThanOrEqual(checkboxBox.x + checkboxBox.width - tolerance);
  }

  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(tolerance);
});

test('Pick List Products dropdown: "Select all" and every option checkbox sit immediately before their label text', async ({
  pageAs
}) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(desktopViewport);
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
    const checkbox = label.getByRole('checkbox');
    const checkboxBox = (await checkbox.boundingBox())!;
    expectNativeSize(checkboxBox);

    const textBox = (await label.getByText(text).boundingBox())!;
    expect(checkboxBox.y).toBeLessThan(textBox.y + textBox.height);
    expect(textBox.y).toBeLessThan(checkboxBox.y + checkboxBox.height);
    expect(textBox.x).toBeGreaterThanOrEqual(checkboxBox.x + checkboxBox.width - tolerance);
  }
});

test('Needs Ordering row-selection checkbox stays its native size', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(desktopViewport);
  await page.goto('/products/needs-ordering');

  const alertRow = page.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
  await expect(alertRow, 'the fixture seeds one alerting product for business A').toBeVisible();

  const checkbox = alertRow.getByRole('checkbox');
  const checkboxBox = (await checkbox.boundingBox())!;
  expectNativeSize(checkboxBox);
});

test('Product form Active checkbox sits immediately before its label text, on desktop and narrow screens', async ({
  pageAs,
  apiAs
}) => {
  const page = await pageAs(actors.businessAOwner);
  const api = await apiAs(actors.businessAOwner);
  const product = await productByName(api, seeded.reorderProduct);

  for (const viewport of [desktopViewport, narrowViewport]) {
    await page.setViewportSize(viewport);
    await page.goto(`/products/${product.id}/edit`);

    const checkbox = page.locator('input[name="isActive"]');
    await expect(checkbox).toBeVisible();

    const checkboxBox = (await checkbox.boundingBox())!;
    expectNativeSize(checkboxBox);

    const label = page.locator('label').filter({ has: checkbox });
    const textBox = (await label.getByText('Active product').boundingBox())!;
    expect(checkboxBox.y).toBeLessThan(textBox.y + textBox.height);
    expect(textBox.y).toBeLessThan(checkboxBox.y + checkboxBox.height);
    expect(textBox.x).toBeGreaterThanOrEqual(checkboxBox.x + checkboxBox.width - tolerance);
  }
});
