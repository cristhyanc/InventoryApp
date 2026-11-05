import type { Locator, Page } from '@playwright/test';
import { actors, expect, test } from '../fixtures/harness';

/**
 * The rendered geometry of the New Product form (issue #892, re-laid-out by issue #1111): paired
 * fields sit beside each other with their inputs on the row's top edge, the settings column sits
 * beside the main column on a wide screen and below it on a narrow one, the sticky action bar is the
 * last thing reached by Tab, and nothing overflows sideways at 1440px or 375px.
 *
 * These are browser tests because jsdom computes no layout. The Jest spec beside the component
 * (`product-form.component.spec.ts`) asserts the section order, the field order, the external
 * submit wiring and the grid classes; a class is not a measurement, so "the two inputs start at the
 * same height" and "the page does not scroll sideways at 375px" are only answerable against real
 * laid-out boxes with the application's compiled Tailwind CSS.
 *
 * It **writes nothing**: it only opens `/products/new`, measures boxes, and moves focus. It never
 * submits the form, never chooses a category and never creates a product or a GST rule, so it needs
 * no seeded row and is independent of every other file's ordering.
 */
const desktopViewport = { width: 1440, height: 900 };
const narrowViewport = { width: 375, height: 667 };

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

/**
 * Two CSS pixels of slack for "the same height". The regression this guards against - a field's
 * help text pushing the control beside it down - moves a control by a whole line of text, far
 * outside a sub-pixel row-baseline difference between two differently composed labels.
 */
const alignmentTolerance = 2;

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

async function boxOf(locator: Locator): Promise<Box> {
  const box = await locator.boundingBox();
  expect(box, 'the element has a laid-out box').not.toBeNull();
  return box!;
}

/** The two controls are on one row: same top edge, and the first one is to the left of the second. */
function expectBesideAndAligned(first: Box, second: Box, label: string) {
  expect(Math.abs(first.y - second.y), `${label}: input tops aligned`).toBeLessThanOrEqual(alignmentTolerance);
  expect(first.x + first.width, `${label}: the first control is left of the second`).toBeLessThanOrEqual(
    second.x + tolerance
  );
  // Same row rather than two lines that happen to start level: their vertical spans overlap.
  expect(first.y, `${label}: same row`).toBeLessThan(second.y + second.height);
  expect(second.y, `${label}: same row`).toBeLessThan(first.y + first.height);
}

/** The second control wrapped onto its own line directly below the first, at the same left edge. */
function expectStackedBelow(first: Box, second: Box, label: string) {
  expect(second.y, `${label}: stacked below`).toBeGreaterThanOrEqual(first.y + first.height - tolerance);
  expect(Math.abs(first.x - second.x), `${label}: same left edge`).toBeLessThanOrEqual(alignmentTolerance);
}

async function expectNoHorizontalOverflow(page: Page, label: string) {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `${label}: page overflow`).toBeLessThanOrEqual(tolerance);
}

/** The New Product page, opened at one viewport and confirmed to be the creation form. */
async function openNewProduct(page: Page, viewport: { width: number; height: number }) {
  await page.setViewportSize(viewport);
  await page.goto('/products/new');
  await expect(page.getByRole('heading', { name: 'New product', level: 1 })).toBeVisible();
}

/** The two controls of one row, in the order the issue requires them to be read in. */
const pairedRows = [
  // Name is alone on the first row since issue #1111; the Category picker carries a help line under
  // its box, and SKU's input must still start at the same height rather than at that line's bottom.
  {
    label: 'Category / SKU',
    section: 'product-details',
    first: 'app-product-category-picker input[role="combobox"]',
    second: 'input[name="sku"]'
  },
  {
    label: 'Opening Stock / Initial Unit Cost',
    section: 'opening-inventory',
    first: 'input[name="quantityInStock"]',
    second: 'input[name="initialUnitCost"]'
  },
  {
    label: 'Low Stock Threshold / Restock To',
    section: 'reordering',
    first: 'input[name="lowStockThreshold"]',
    second: 'input[name="restockTo"]'
  }
] as const;

function sectionOf(page: Page, name: string): Locator {
  return page.locator(`[data-testid="product-section-${name}"]`);
}

test('on a desktop screen each paired row sits side by side, beside the settings column', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await openNewProduct(page, desktopViewport);

  for (const row of pairedRows) {
    const section = sectionOf(page, row.section);
    const first = section.locator(row.first);
    const second = section.locator(row.second);
    await expect(first, `${row.label}: the first control is rendered`).toBeVisible();
    await expect(second, `${row.label}: the second control is rendered`).toBeVisible();

    expectBesideAndAligned(await boxOf(first), await boxOf(second), `${row.label} at ${desktopViewport.width}`);
  }

  // Name and Description span the whole Product details grid.
  const details = sectionOf(page, 'product-details');
  const gridBox = await boxOf(details.locator('div.grid').first());
  const nameBox = await boxOf(details.locator('app-product-master-product-picker input[role="combobox"]'));
  const descriptionBox = await boxOf(details.locator('textarea[name="description"]'));
  expect(nameBox.width, 'Name spans the section').toBeGreaterThanOrEqual(gridBox.width - 2 * alignmentTolerance);
  expect(descriptionBox.width, 'Description spans the section').toBeGreaterThanOrEqual(
    gridBox.width - 2 * alignmentTolerance
  );

  // The settings column is to the right of the main column, and narrower.
  const main = await boxOf(page.locator('[data-testid="product-form-main"]'));
  const settings = await boxOf(page.locator('[data-testid="product-form-settings"]'));
  expect(settings.x, 'settings beside main').toBeGreaterThanOrEqual(main.x + main.width - tolerance);
  expect(settings.width, 'settings narrower than main').toBeLessThan(main.width);

  await expectNoHorizontalOverflow(page, `products/new at ${desktopViewport.width}`);
});

test('the action bar follows the settings column by Tab, with Cancel before Create product', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await openNewProduct(page, desktopViewport);

  await expect(page.locator('main h2, .page h2')).toHaveText([
    'Product details',
    'Opening inventory',
    'Reordering',
    'Product status',
    'GST rule',
    'Supplier price tracking'
  ]);

  const gstRule = page.locator('#product-gst-rule');
  const actions = page.locator('[data-testid="product-form-actions"]');
  const create = actions.getByRole('button', { name: 'Create product' });
  const cancel = actions.getByRole('link', { name: 'Cancel' });
  await expect(create).toBeVisible();
  await expect(cancel).toBeVisible();
  expectBesideAndAligned(await boxOf(cancel), await boxOf(create), `Cancel / Create at ${desktopViewport.width}`);

  // By keyboard: Tab off the GST select reaches Cancel, then Create product, even though the button
  // is outside the form it submits (wired by `form="product-form"`). With no catalogue selection the
  // tracking card offers no control in between. Unit Label is required (issue #895), so it is filled
  // first: an untouched form keeps Create product disabled, and Tab steps past a disabled button.
  await page.locator('input[name="unit"]').fill('375 mL');
  await expect(create).toBeEnabled();
  await gstRule.focus();
  await page.keyboard.press('Tab');
  await expect(cancel).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(create).toBeFocused();
});

test('at 375px every section stacks into one column and nothing scrolls sideways', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await openNewProduct(page, narrowViewport);

  for (const row of pairedRows) {
    const section = sectionOf(page, row.section);
    const first = await boxOf(section.locator(row.first));
    const second = await boxOf(section.locator(row.second));

    expectStackedBelow(first, second, `${row.label} at ${narrowViewport.width}`);
    expect(second.x, `${row.label}: inside the left viewport edge`).toBeGreaterThanOrEqual(-tolerance);
    expect(second.x + second.width, `${row.label}: inside the right viewport edge`).toBeLessThanOrEqual(
      narrowViewport.width + tolerance
    );
  }

  const main = await boxOf(page.locator('[data-testid="product-form-main"]'));
  const settings = await boxOf(page.locator('[data-testid="product-form-settings"]'));
  expect(settings.y, 'settings below main').toBeGreaterThanOrEqual(main.y + main.height - tolerance);

  for (const name of ['product-details', 'opening-inventory', 'reordering', 'status', 'price-tracking']) {
    const overflow = await sectionOf(page, name).evaluate((element) => element.scrollWidth - element.clientWidth);
    expect(overflow, `${name}: section overflow`).toBeLessThanOrEqual(tolerance);
  }

  await expectNoHorizontalOverflow(page, `products/new at ${narrowViewport.width}`);
});
