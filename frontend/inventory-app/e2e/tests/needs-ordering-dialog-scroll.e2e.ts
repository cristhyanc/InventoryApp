import { actors, expect, seeded, test } from '../fixtures/harness';

/**
 * The "Mark products as ordered" dialog's layout on a short viewport (issue #906): the panel
 * bounds itself to the viewport instead of letting the fixed backdrop clip it, and its heading,
 * fields, product rows and Cancel/Create order buttons all stay reachable - the heading and the
 * buttons because they sit outside the scrolling body, everything else because the body between
 * them scrolls.
 *
 * `E2ETestFixture` seeds exactly one alerting product per business on purpose ("the reorder page
 * has exactly one alerting product on a fresh database"), and this issue excludes any backend or
 * fixture change, so this test cannot reach overflow by selecting "enough products" the way the
 * toolbar-layout test's narrow-viewport check does not need to. It reaches the same overflow
 * instead with a viewport short enough that even this one product's row does not fit - the panel
 * still has to bound itself and the body still has to scroll, which is the behaviour the issue's
 * acceptance criteria are about.
 *
 * This is a browser test because jsdom computes no scroll height and no viewport clipping; the
 * Jest specs beside the component assert the panel's and body's classes and DOM structure, not
 * laid-out geometry.
 *
 * It **writes nothing**: it opens the dialog and cancels it, never submitting an order, so the
 * fixture's one seeded alert survives for `needs-ordering-toolbar-layout.e2e.ts` and
 * `reorder-to-supplier-order-to-purchase.e2e.ts`, both of which also depend on it. The suite runs
 * its files in alphabetical order with a single worker, and this file's name keeps it ahead of
 * both.
 */
const shortViewport = { width: 390, height: 320 };

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

test('the order dialog panel stays within a short viewport, with the heading, the product row and Create order all reachable', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(shortViewport);
  await page.goto('/products/needs-ordering');

  const alertRow = page.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
  await expect(alertRow, 'the fixture seeds one alerting product; this file must run before the other two that depend on it').toBeVisible();
  await alertRow.getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Mark as ordered' }).click();

  const orderForm = page.locator('form').filter({ hasText: 'Mark products as ordered' });
  await expect(orderForm).toBeVisible();

  // The panel never exceeds the viewport, top or bottom.
  const panelBox = (await orderForm.boundingBox())!;
  expect(panelBox.y).toBeGreaterThanOrEqual(-tolerance);
  expect(panelBox.y + panelBox.height).toBeLessThanOrEqual(shortViewport.height + tolerance);

  // The content is actually taller than the panel can show at once - otherwise this test would
  // not be exercising the scroll path it claims to.
  const bodyOverflow = await orderForm.evaluate((form) => {
    const body = form.querySelector(':scope > div:nth-child(2)') as HTMLElement;
    return body.scrollHeight - body.clientHeight;
  });
  expect(bodyOverflow).toBeGreaterThan(0);

  // The heading stays visible at the top...
  await expect(orderForm.getByRole('heading', { name: 'Mark products as ordered' })).toBeInViewport();

  // ...the single product row can be scrolled into view...
  const productRow = orderForm.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
  await productRow.scrollIntoViewIfNeeded();
  await expect(productRow).toBeInViewport();

  // ...and Create order stays visible at the bottom throughout, since it sits outside the
  // scrolling body rather than being pushed off by it.
  await expect(orderForm.getByRole('button', { name: 'Create order' })).toBeInViewport();

  // No order is created: Cancel leaves the fixture's seeded alert for the tests that depend on it.
  await orderForm.getByRole('button', { name: 'Cancel' }).click();
  await expect(orderForm).toBeHidden();
  await expect(alertRow).toBeVisible();
});
