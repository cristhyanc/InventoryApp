import { actors, expect, productByName, receiptFile, seeded, test } from '../fixtures/harness';

/**
 * Purchase create, edit and delete, at smoke-test level, judged by their inventory and costing
 * effects rather than by the page reloading (issue #46).
 *
 * The product this runs against is seeded with no stock, no costing quantity and no cost, so every
 * expected number below is exact: a purchase of 10 at $2.00 is 10 units worth $20.00 at an average
 * unit cost of $2.00, editing it down to 4 units is $8.00, and deleting it must leave nothing
 * behind - not a stale quantity, and not a stale inventory value.
 */
test('creating, editing and deleting a purchase moves inventory and costing with it', async ({
  pageAs,
  apiAs
}) => {
  const page = await pageAs(actors.businessAOwner);
  const api = await apiAs(actors.businessAOwner);
  const title = 'E2E purchase effects';

  const product = await productByName(api, seeded.purchaseProduct);
  expect([product.quantityInStock, product.costingQuantity, product.inventoryValue]).toEqual([0, 0, 0]);

  // Create.
  await page.goto('/purchases/new');
  await page.locator('input[type=file]').setInputFiles(receiptFile);
  await page.locator('input[name="title"]').fill(title);
  await page.locator('select[name="supplierId"]').selectOption({ label: seeded.supplierA });
  await page.getByRole('button', { name: 'Add Product' }).click();
  await page.getByTestId('purchase-item-product').selectOption({ label: seeded.purchaseProduct });
  await page.getByTestId('purchase-item-quantity').fill('10');
  await page.getByTestId('purchase-item-cost').fill('2');
  await page.getByRole('button', { name: 'Add Purchase' }).click();

  await expect(page).toHaveURL(/\/purchases$/);
  const purchaseRow = page.getByRole('row', { name: new RegExp(title) });
  await expect(purchaseRow).toBeVisible();
  await expect(purchaseRow).toContainText('10 × $2.00');

  const created = await productByName(api, seeded.purchaseProduct);
  expect([created.quantityInStock, created.costingQuantity, created.inventoryValue, created.averageUnitCost])
    .toEqual([10, 10, 20, 2]);

  // Edit: the same purchase, six units fewer.
  await purchaseRow.getByRole('button', { name: 'Edit' }).click();
  const editForm = page.locator('form').filter({ hasText: 'Edit Purchase' });
  await expect(editForm).toBeVisible();
  await expect(editForm.getByTestId('edit-purchase-item-quantity')).toHaveValue('10');
  await editForm.getByTestId('edit-purchase-item-quantity').fill('4');
  await editForm.getByRole('button', { name: 'Save' }).click();
  await expect(editForm).toBeHidden();

  const edited = await productByName(api, seeded.purchaseProduct);
  expect([edited.quantityInStock, edited.costingQuantity, edited.inventoryValue, edited.averageUnitCost])
    .toEqual([4, 4, 8, 2]);

  // Delete: the purchase and its inventory effect both have to go.
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('row', { name: new RegExp(title) }).getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByRole('row', { name: new RegExp(title) })).toBeHidden();

  const deleted = await productByName(api, seeded.purchaseProduct);
  expect([deleted.quantityInStock, deleted.costingQuantity, deleted.inventoryValue]).toEqual([0, 0, 0]);
});
