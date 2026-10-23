import { actors, expect, seeded, test } from '../fixtures/harness';

/**
 * The rendered geometry of the Needs Ordering search/filter toolbar (issue #832): Mark as ordered
 * sits at the right edge of that toolbar on a desktop screen, and wraps onto a following line
 * instead of widening the toolbar past the viewport on a narrow one.
 *
 * This is a browser test because jsdom cannot answer the question. The Jest specs beside the
 * component assert containment, DOM order and the flex classes, but jsdom computes no flex
 * geometry and no scroll width, so "aligned right" and "wraps without horizontal overflow" are
 * only checkable against real laid-out boxes with the application's compiled Tailwind CSS - which
 * is what the issue's "verify layout on desktop and narrow screens" criterion asks for.
 *
 * It **writes nothing**: it checks a row's selection checkbox to observe the action's enabled
 * state, and never opens or submits the order form. The two checks that need a selectable row
 * read the one alerting product the fixture seeds, so they have to run before the reorder workflow
 * test turns that alert into a supplier order and leaves the page with no rows; the suite runs its
 * files in alphabetical order with a single worker, and this file's name keeps it ahead of
 * `reorder-to-supplier-order-to-purchase.e2e.ts`. If the alert row is ever missing here, that
 * ordering is what to check first. The narrow-screen check needs no row at all - the toolbar
 * renders above the list either way - so it runs as the other business, whose list is empty, and
 * is independent of all of that.
 */
const desktopViewport = { width: 1440, height: 900 };
const narrowViewport = { width: 390, height: 844 };

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

test('on a desktop screen Mark as ordered is laid out at the right edge of the toolbar', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(desktopViewport);
  await page.goto('/products/needs-ordering');

  const alertRow = page.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
  await expect(alertRow, 'the fixture seeds one alerting product; this file must run before the reorder workflow test').toBeVisible();

  const toolbar = page.locator('.card-body').filter({ has: page.getByPlaceholder('Search name or SKU') });
  const search = page.getByPlaceholder('Search name or SKU');
  const action = page.getByRole('button', { name: 'Mark as ordered' });

  const toolbarBox = (await toolbar.boundingBox())!;
  const searchBox = (await search.boundingBox())!;
  const actionBox = (await action.boundingBox())!;

  // Right-aligned: the action's right edge is the toolbar's own content right edge, so nothing
  // sits between it and the padding - which is what `sm:ml-auto` is there to produce.
  const toolbarPadding = await toolbar.evaluate((element) => Number.parseFloat(getComputedStyle(element).paddingRight));
  expect(actionBox.x + actionBox.width).toBeCloseTo(toolbarBox.x + toolbarBox.width - toolbarPadding, 0);

  // Same row as the filters, not a second line: the action's vertical span overlaps the search
  // box's, and search stays on the left.
  expect(actionBox.y).toBeLessThan(searchBox.y + searchBox.height);
  expect(searchBox.y).toBeLessThan(actionBox.y + actionBox.height);
  expect(searchBox.x).toBeLessThan(actionBox.x);

  // And the toolbar itself does not scroll sideways.
  const overflow = await toolbar.evaluate((element) => element.scrollWidth - element.clientWidth);
  expect(overflow).toBeLessThanOrEqual(tolerance);
});

test('on a narrow screen the toolbar wraps instead of overflowing sideways', async ({ pageAs }) => {
  const page = await pageAs(actors.businessBOwner);
  await page.setViewportSize(narrowViewport);
  await page.goto('/products/needs-ordering');

  const toolbar = page.locator('.card-body').filter({ has: page.getByPlaceholder('Search name or SKU') });
  const search = page.getByPlaceholder('Search name or SKU');
  const action = page.getByRole('button', { name: 'Mark as ordered' });
  await expect(action).toBeVisible();

  const searchBox = (await search.boundingBox())!;
  const actionBox = (await action.boundingBox())!;

  // Wrapped: the action is on a line below the search box, and entirely inside the viewport.
  expect(actionBox.y).toBeGreaterThanOrEqual(searchBox.y + searchBox.height);
  expect(actionBox.x).toBeGreaterThanOrEqual(-tolerance);
  expect(actionBox.x + actionBox.width).toBeLessThanOrEqual(narrowViewport.width + tolerance);

  // Not overflowing: neither the toolbar nor the page scrolls sideways at this width.
  const toolbarOverflow = await toolbar.evaluate((element) => element.scrollWidth - element.clientWidth);
  expect(toolbarOverflow).toBeLessThanOrEqual(tolerance);
  const pageOverflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(pageOverflow).toBeLessThanOrEqual(tolerance);
});

test('the toolbar action is keyboard-reachable after the filters, and enabled only with a selection', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(desktopViewport);
  await page.goto('/products/needs-ordering');

  const alertRow = page.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
  await expect(alertRow).toBeVisible();

  const toolbar = page.locator('.card-body').filter({ has: page.getByPlaceholder('Search name or SKU') });
  const action = page.getByRole('button', { name: 'Mark as ordered' });

  // Nothing is selected yet, so there is nothing to order - and a disabled button takes no focus,
  // so the keyboard skips it rather than landing on a control that cannot act.
  await expect(action).toBeDisabled();

  // Selecting a row enables it, in the toolbar, without moving it.
  await alertRow.getByRole('checkbox').check();
  await expect(action).toBeEnabled();

  // Tab order then follows the toolbar: search, Category, Reset, then the action.
  await page.getByPlaceholder('Search name or SKU').focus();
  await page.keyboard.press('Tab');
  await expect(toolbar.getByRole('combobox')).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(toolbar.getByRole('button', { name: 'Reset' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(action).toBeFocused();
});
