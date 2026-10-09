import { actors, expect, productByName, seeded, test } from '../fixtures/harness';

/**
 * The stock-correction workflow and the one piece of UI behaviour behind it that is easy to break
 * silently (issue #46): a correction is entered as a positive magnitude - "remove 4" - and must be
 * persisted as a negative movement of 4.
 *
 * A component test can prove the form sends a negative number. Only this test proves the whole
 * chain agrees: the browser form, the adjustment endpoint, the persisted movement, the resulting
 * quantity, and the history the operator reads back afterwards.
 */
test('a positive correction quantity is persisted as a negative stock movement', async ({ pageAs, apiAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const api = await apiAs(actors.businessAOwner);

  const before = await productByName(api, seeded.correctionProduct);
  expect(before.quantityInStock).toBe(20);

  await page.goto(`/products/${before.id}/stock`);
  await expect(page.getByTestId('selected-product-summary')).toContainText(seeded.correctionProduct);

  // The operator chooses Correction and types the quantity to remove as a positive number; the
  // form says so, and must not require a minus sign.
  await page.locator('#adjustment-reason').selectOption({ label: 'Correction' });
  await expect(page.getByText('It will be recorded as a negative stock correction.')).toBeVisible();
  await page.locator('#adjustment-quantity-change').fill('4');
  await page.locator('#adjustment-notes').fill('E2E stock count correction');
  await page.getByTestId('apply-adjustment').click();

  // The history is the operator's record of what actually happened: a movement of -4, not +4.
  const historyRow = page.getByTestId('history-rows').locator('tr').first();
  await expect(historyRow).toContainText(seeded.correctionProduct);
  await expect(historyRow).toContainText('-4');
  await expect(historyRow).toContainText('Correction');
  await expect(historyRow).toContainText('E2E stock count correction');
  await expect(page.getByTestId('selected-product-summary')).toContainText('16');

  // The authoritative stock, read back from the API the page itself calls.
  const after = await productByName(api, seeded.correctionProduct);
  expect(after.quantityInStock).toBe(16);

  const history = await api.get(`/api/products/${before.id}/stock`);
  expect(history.status()).toBe(200);
  const movements = (await history.json()) as { quantityChange: number; quantityAfter: number; reason: number }[];
  const correction = movements.find((movement) => movement.quantityChange < 0);
  expect(correction, 'the correction was persisted as a negative movement').toBeTruthy();
  expect(correction!.quantityChange).toBe(-4);
  expect(correction!.quantityAfter).toBe(16);
});
