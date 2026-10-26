import type { Locator, Page } from '@playwright/test';
import { actors, expect, test } from '../fixtures/harness';

/**
 * The rendered geometry of the New Product form's sections (issue #892): paired fields sit beside
 * each other with their inputs on the row's top edge, the Active checkbox stays as wide as it
 * reads, the GST choice is laid out above the single Create Product / Cancel row, and nothing
 * overflows sideways at either width the issue names - 1440px and 375px.
 *
 * These are browser tests because jsdom computes no layout. The Jest spec beside the component
 * (`product-form.component.spec.ts`) asserts the section order, the cell order, the external
 * submit wiring and the grid classes - that `items-start` and `md:grid-cols-2` are present and
 * `xl:grid-cols-3` is gone - but a class is not a measurement: jsdom resolves no grid tracks, no
 * `items-start`, no `scrollWidth` and no `clientWidth`, so "the two inputs start at the same
 * height", "the checkbox is compact" and "the page does not scroll sideways at 375px" are only
 * answerable against real laid-out boxes with the application's compiled Tailwind CSS. That is
 * what the issue's "render/check at 375px and 1440px: aligned input tops, compact Active checkbox,
 * adjacent opening-stock/cost fields, no overflow" criterion asks for.
 *
 * It **writes nothing**: it only opens `/products/new`, measures boxes, and focuses the GST select
 * to read Tab order. It never submits the form, never chooses a category and never creates a
 * product or a GST rule, so it needs no seeded row and is independent of every other file's
 * ordering.
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
  await expect(page.getByRole('heading', { name: 'New Product', level: 1 })).toBeVisible();
}

/** The two controls of one row, in the order the issue requires them to be read in. */
const pairedRows = [
  { label: 'Name / SKU', section: 'product-details', first: 'input[name="name"]', second: 'input[name="sku"]' },
  // The row the issue was raised about: the Category picker carries a help line under its box, and
  // Unit Label's input must still start at the same height rather than at that line's bottom.
  { label: 'Category / Unit Label', section: 'product-details', first: '[role="combobox"]', second: 'input[name="unit"]' },
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

/** The one card this page renders, which holds the form, the GST panel and the action row. */
function formCard(page: Page): Locator {
  return page.locator('.card');
}

test('on a desktop screen each paired row sits side by side with its inputs aligned', async ({ pageAs }) => {
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

  // Description is the full-width field below those two rows, not a third column beside them.
  const details = sectionOf(page, 'product-details');
  const grid = details.locator('div.grid');
  const gridBox = await boxOf(grid);
  const descriptionBox = await boxOf(details.locator('textarea[name="description"]'));
  const unitBox = await boxOf(details.locator('input[name="unit"]'));

  expect(descriptionBox.y, 'Description is below the Category / Unit Label row').toBeGreaterThan(
    unitBox.y + unitBox.height
  );
  expect(descriptionBox.width, 'Description spans the whole section width').toBeGreaterThanOrEqual(
    gridBox.width - 2 * alignmentTolerance
  );

  await expectNoHorizontalOverflow(page, `products/new at ${desktopViewport.width}`);
});

test('the Active checkbox stays compact and left-aligned under the field grid at both widths', async ({
  pageAs
}) => {
  const page = await pageAs(actors.businessAOwner);

  for (const viewport of [desktopViewport, narrowViewport]) {
    await openNewProduct(page, viewport);
    const where = `Active product at ${viewport.width}x${viewport.height}`;

    const details = sectionOf(page, 'product-details');
    const checkbox = details.locator('input[name="isActive"]');
    await expect(checkbox).toBeVisible();
    await expect(checkbox, 'the default is an active product, and this test changes nothing').toBeChecked();

    // The native box, not one the global input rule stretched (issues #872/#874).
    const checkboxBox = await boxOf(checkbox);
    expect(checkboxBox.width, `${where}: checkbox width`).toBeGreaterThanOrEqual(10);
    expect(checkboxBox.width, `${where}: checkbox width`).toBeLessThanOrEqual(24);

    // Compact: the label's own box is as wide as it reads, not as wide as the field beside it used
    // to make it. A stretched box is a grid column wide - the whole section at 375px, half of it
    // from `md` - so both bounds below fail on the layout this issue removed, and the text-beside
    // check is what says the box did not collapse instead.
    const label = details.locator('label').filter({ has: checkbox });
    const labelBox = await boxOf(label);
    const gridBox = await boxOf(details.locator('div.grid'));
    expect(labelBox.width, `${where}: the label is as wide as it reads`).toBeLessThanOrEqual(260);
    expect(labelBox.width, `${where}: the label is narrower than the grid above it`).toBeLessThanOrEqual(
      gridBox.width - 40
    );

    const textBox = await boxOf(label.getByText('Active product'));
    expect(textBox.x, `${where}: the label text starts beside the checkbox`).toBeGreaterThanOrEqual(
      checkboxBox.x + checkboxBox.width - tolerance
    );
    expect(checkboxBox.y, `${where}: the checkbox is on the label's line`).toBeLessThan(textBox.y + textBox.height);
    expect(textBox.y, `${where}: the checkbox is on the label's line`).toBeLessThan(
      checkboxBox.y + checkboxBox.height
    );

    // Left-aligned with the fields above it, and below the whole grid rather than inside a cell of
    // it - which is what bottom-aligning it against the Category help text used to look like.
    expect(Math.abs(labelBox.x - gridBox.x), `${where}: left-aligned with the grid`).toBeLessThanOrEqual(
      alignmentTolerance
    );
    expect(labelBox.y, `${where}: below the field grid`).toBeGreaterThanOrEqual(
      gridBox.y + gridBox.height - tolerance
    );

    await expectNoHorizontalOverflow(page, `products/new at ${viewport.width}`);
  }
});

test('GST rule is laid out and tab-ordered above the single Create Product / Cancel row', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await openNewProduct(page, desktopViewport);

  // The sections are read in the issue's order, with the GST panel's own heading last. Scoped to
  // the form's card: the sidebar carries level-2 headings of its own for its navigation groups.
  await expect(formCard(page).getByRole('heading', { level: 2 })).toHaveText([
    'Product details',
    'Opening inventory',
    'Reordering',
    'GST rule'
  ]);

  const gstRule = page.locator('#product-gst-rule');
  const actions = page.locator('[data-testid="product-form-actions"]');
  const create = actions.getByRole('button', { name: 'Create Product' });
  const cancel = actions.getByRole('link', { name: 'Cancel' });
  await expect(gstRule).toBeVisible();
  await expect(create).toBeVisible();
  await expect(cancel).toBeVisible();

  // Visually above: the whole action row starts below the GST select.
  const gstBox = await boxOf(gstRule);
  const actionsBox = await boxOf(actions);
  expect(actionsBox.y, 'the action row is below the GST choice').toBeGreaterThan(gstBox.y + gstBox.height);

  // One row, with Create before Cancel on it.
  const createBox = await boxOf(create);
  const cancelBox = await boxOf(cancel);
  expectBesideAndAligned(createBox, cancelBox, `Create / Cancel at ${desktopViewport.width}`);

  // And by keyboard: Tab off the GST select reaches Create Product, even though the button is
  // outside the form it submits (the external footer wired by `form="new-product-form"`).
  await gstRule.focus();
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

  // Neither the card nor any section scrolls sideways at this width, and neither does the page.
  const cardOverflow = await formCard(page).evaluate((element) => element.scrollWidth - element.clientWidth);
  expect(cardOverflow, 'card overflow').toBeLessThanOrEqual(tolerance);

  for (const name of ['product-details', 'opening-inventory', 'reordering']) {
    const overflow = await sectionOf(page, name).evaluate(
      (element) => element.scrollWidth - element.clientWidth
    );
    expect(overflow, `${name}: section overflow`).toBeLessThanOrEqual(tolerance);
  }

  await expectNoHorizontalOverflow(page, `products/new at ${narrowViewport.width}`);
});
