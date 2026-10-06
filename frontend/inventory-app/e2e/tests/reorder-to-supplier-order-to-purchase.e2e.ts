import { actors, expect, productByName, receiptFile, reorderAlertNames, seeded, test } from '../fixtures/harness';

/**
 * The highest-value cross-layer workflow in the application (issue #46): a product falls below its
 * reorder point, an operator orders it from the supplier, and the delivery is received as a
 * purchase - which is what gives the stock a historical cost.
 *
 * Every step runs in the browser against the real API: reorder arithmetic, the supplier order, the
 * automatic allocation of purchase lines to outstanding order lines, the order's fulfillment
 * status, and the costing effect of the receipt. The API is only read at the end, to assert the
 * authoritative AVCO values that no page displays.
 */
test('a reorder alert becomes a supplier order and then a costed purchase', async ({ pageAs, apiAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const api = await apiAs(actors.businessAOwner);

  const before = await productByName(api, seeded.reorderProduct);
  expect(before.quantityInStock, 'the fixture seeds this product below its reorder point').toBe(2);
  expect(before.needToOrder, 'restock target 30 minus 2 on hand').toBe(28);

  // 1. The reorder alert.
  await page.goto('/products/needs-ordering');
  const alertRow = page.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
  await expect(alertRow).toBeVisible();
  await expect(alertRow).toContainText('28');

  // 2. Order the shortfall from the supplier.
  await alertRow.getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Mark as ordered' }).click();

  const orderForm = page.locator('form').filter({ hasText: 'Mark products as ordered' });
  await expect(orderForm).toBeVisible();
  await orderForm.getByLabel('Supplier').selectOption({ label: seeded.supplierA });
  const orderQuantity = orderForm
    .getByRole('row', { name: new RegExp(seeded.reorderProduct) })
    .getByRole('spinbutton');
  await expect(orderQuantity, 'the order is prefilled with the quantity the product needs').toHaveValue('28');
  await orderForm.getByRole('button', { name: 'Create order' }).click();
  await expect(orderForm).toBeHidden();

  // The product is now on order, so it must stop asking to be ordered again - the outstanding
  // order quantity counts towards the restock target.
  await expect(page.getByRole('row', { name: new RegExp(seeded.reorderProduct) })).toBeHidden();
  expect(await reorderAlertNames(api)).toEqual([]);

  // 3. The open supplier order.
  await page.goto('/purchases/orders');
  const orderHeading = page.getByRole('heading', {
    name: new RegExp(String.raw`Order #\d+ - ${seeded.supplierA}`)
  });
  await expect(orderHeading).toBeVisible();
  const orderId = Number(/Order #(\d+)/.exec((await orderHeading.textContent()) ?? '')![1]);
  const orderLine = page.getByRole('row', { name: new RegExp(seeded.reorderProduct) });
  await expect(orderLine, 'ordered 28, received 0, outstanding 28').toContainText('28');

  // 4. Receive it, which creates the purchase.
  await page.getByRole('button', { name: 'Receive / Create Purchase' }).click();
  await expect(page).toHaveURL(new RegExp(String.raw`/purchases/new\?supplierOrderId=${orderId}$`));
  await expect(page.getByRole('heading', { name: `Receiving Supplier Order #${orderId}` })).toBeVisible();

  await page.locator('input[type=file]').setInputFiles(receiptFile);
  await page.locator('input[name="title"]').fill('E2E received supplier order');
  await expect(
    page.getByTestId('purchase-item-quantity'),
    'the outstanding quantity is prefilled from the order'
  ).toHaveValue('28');
  // The supplier order carried no unit price, so the operator supplies the purchase cost - this is
  // the value the received stock is costed at.
  await page.getByTestId('purchase-item-cost').fill('1.5');
  await page.getByRole('button', { name: 'Add Purchase' }).click();

  // 5. The order is fully received, so it leaves the open list.
  await expect(page).toHaveURL(/\/purchases\/orders$/);
  await expect(page.getByText('No active supplier orders.')).toBeVisible();

  // 6. The purchase is recorded with the line that was received.
  await page.goto('/purchases');
  const purchaseRow = page.getByRole('row', { name: /E2E received supplier order/ });
  await expect(purchaseRow).toBeVisible();
  await expect(purchaseRow).toContainText(seeded.reorderProduct);
  await expect(purchaseRow).toContainText('28 × $1.50');

  // 7. The inventory and costing effect, which is the part no page shows: the received stock is on
  // hand and carries a historical cost, blended from its $1.20 opening stock and this $1.50
  // receipt - 2 x 1.20 + 28 x 1.50 = 44.40 over 30 units.
  const after = await productByName(api, seeded.reorderProduct);
  expect(after.quantityInStock).toBe(30);
  expect(after.costingQuantity).toBe(30);
  expect(after.inventoryValue).toBeCloseTo(44.4, 2);
  expect(await reorderAlertNames(api), 'the alert has cleared').toEqual([]);
});
