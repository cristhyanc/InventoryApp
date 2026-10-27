import { actors, expect, productByName, seeded, test } from '../fixtures/harness';

/**
 * Adding a product to the order from inside the "Mark products as ordered" dialog (issue #907): a
 * catalogue product that never appeared on the Needs ordering list - `E2E Correction Bars` is not
 * low on stock, so it never alerts - joins the order being built without closing the dialog, and
 * the created order carries exactly that product.
 *
 * The fixture seeds only one alerting product for business A (`E2E Reorder Chips`), so opening the
 * dialog at all still needs that product ticked first. This test then uses Remove (issue #906) to
 * drop that row again before submitting, so the order it creates names only the added product -
 * `E2E Reorder Chips`'s own stock and reorder alert are left exactly as the fixture seeded them,
 * for the other specs that depend on it still being there. The created order is then cancelled
 * rather than received: cancelling moves no stock at all (receiving would add to
 * `E2E Correction Bars`'s stock, which `stock-correction.e2e.ts` depends on as the fixture left
 * it), and it is what clears the order from the open list other specs rely on seeing only their
 * own order.
 */
test('a product search can add a catalogue product that never appeared on the Needs ordering list, and the created order carries exactly it', async ({
  pageAs,
  apiAs
}) => {
  const page = await pageAs(actors.businessAOwner);
  const api = await apiAs(actors.businessAOwner);

  const before = await productByName(api, seeded.correctionProduct);
  const reorderBefore = await productByName(api, seeded.reorderProduct);

  await page.goto('/products/needs-ordering');
  const alertRow = page.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
  await expect(alertRow, 'the fixture seeds one alerting product, needed here only to open the dialog').toBeVisible();
  await alertRow.getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Mark as ordered' }).click();

  const orderForm = page.locator('form').filter({ hasText: 'Mark products as ordered' });
  await expect(orderForm).toBeVisible();

  // Search the wider catalogue for a product the Needs ordering list never listed.
  await orderForm.getByLabel('Add product').fill('E2E Correction');
  await orderForm.getByRole('button', { name: `Add ${seeded.correctionProduct} to order` }).click();

  const addedRow = orderForm.getByRole('row', { name: new RegExp(seeded.correctionProduct) });
  await expect(addedRow, 'the added product is a new row, not merely a search result').toBeVisible();
  await expect(addedRow).toContainText(String(before.quantityInStock));
  // It never alerted, so it has no need-to-order: the quantity starts at 1, not 0.
  await expect(addedRow.getByRole('spinbutton')).toHaveValue('1');

  // Drop the originally-ticked product from this order (issue #906's Remove) so the order this
  // test creates names only the added product, leaving the seeded reorder alert untouched for the
  // other specs that depend on it.
  await orderForm.getByRole('button', { name: `Remove ${seeded.reorderProduct} from order` }).click();
  await expect(orderForm.getByRole('row', { name: new RegExp(seeded.reorderProduct) })).toBeHidden();

  await orderForm.getByLabel('Supplier').selectOption({ label: seeded.supplierA });
  await addedRow.getByRole('spinbutton').fill('3');
  await orderForm.getByRole('button', { name: 'Create order' }).click();
  await expect(orderForm).toBeHidden();

  // The product the dialog never listed is now on an order, and the one it replaced is not.
  await page.goto('/purchases/orders');
  const orderHeading = page.getByRole('heading', {
    name: new RegExp(String.raw`Order #\d+ - ${seeded.supplierA}`)
  });
  await expect(orderHeading).toBeVisible();
  await expect(page.getByRole('row', { name: new RegExp(seeded.correctionProduct) })).toContainText('3');
  await expect(page.getByRole('row', { name: new RegExp(seeded.reorderProduct) })).toBeHidden();

  // Cancel it rather than receive it: cancelling moves no stock, so neither product's figures are
  // disturbed for the other specs that depend on them, and it still clears the order from the
  // active list `GET api/supplierorders` answers (`SupplierOrdersComponent.loadOrders`).
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByText('No active supplier orders.')).toBeVisible();

  const after = await productByName(api, seeded.correctionProduct);
  expect(after.quantityInStock).toBe(before.quantityInStock);

  const reorderAfter = await productByName(api, seeded.reorderProduct);
  expect(reorderAfter.quantityInStock).toBe(reorderBefore.quantityInStock);
  expect(reorderAfter.needToOrder).toBe(reorderBefore.needToOrder);
});
