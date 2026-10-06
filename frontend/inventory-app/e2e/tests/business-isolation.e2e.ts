import { actors, expect, productByName, seeded, test } from '../fixtures/harness';

/**
 * Tenant isolation, seen from the browser (issue #46, AGENTS.md § Tenant ownership and data
 * isolation).
 *
 * The synthetic authentication scheme authenticates a caller and changes nothing else, so the two
 * synthetic businesses must be as separated here as two real ones are: each sees its own catalogue
 * and only its own, neither can reach the other's record by id, and an authenticated actor with no
 * membership at all is refused rather than shown an empty - or worse, a populated - page.
 */
test("one business's pages never show the other business's data", async ({ pageAs }) => {
  const businessA = await pageAs(actors.businessAOwner);
  const businessB = await pageAs(actors.businessBOwner);

  await businessA.goto('/products');
  await expect(businessA.getByRole('row', { name: new RegExp(seeded.reorderProduct) })).toBeVisible();
  await expect(businessA.getByRole('row', { name: new RegExp(seeded.businessBProduct) })).toBeHidden();

  await businessB.goto('/products');
  await expect(businessB.getByRole('row', { name: new RegExp(seeded.businessBProduct) })).toBeVisible();
  await expect(businessB.getByRole('row', { name: new RegExp(seeded.reorderProduct) })).toBeHidden();
  await expect(businessB.getByRole('row', { name: new RegExp(seeded.correctionProduct) })).toBeHidden();
});

test("one business cannot reach the other business's product by id", async ({ apiAs }) => {
  const businessA = await apiAs(actors.businessAOwner);
  const businessB = await apiAs(actors.businessBOwner);

  const productOfA = await productByName(businessA, seeded.reorderProduct);
  const productOfB = await productByName(businessB, seeded.businessBProduct);

  // Indistinguishable from an id that does not exist: not 403, which would confirm it is real.
  expect((await businessB.get(`/api/products/${productOfA.id}`)).status()).toBe(404);
  expect((await businessA.get(`/api/products/${productOfB.id}`)).status()).toBe(404);
});

test('an authenticated actor with no business membership is refused', async ({ pageAs, apiAs }) => {
  const api = await apiAs(actors.noMembership);
  const page = await pageAs(actors.noMembership);

  // Authenticated, then refused by the business-scope boundary - not served an empty catalogue.
  expect((await api.get('/api/products')).status()).toBe(403);

  await page.goto('/products');
  await expect(page.getByRole('row', { name: new RegExp(seeded.reorderProduct) })).toBeHidden();
  await expect(page.getByRole('row', { name: new RegExp(seeded.businessBProduct) })).toBeHidden();
});

test('an unauthenticated request is refused by authentication, not by the application', async ({ playwright }) => {
  // No synthetic actor header at all: the E2E host must answer this exactly as every other
  // environment does, with 401 from authentication before any controller runs.
  const anonymous = await playwright.request.newContext({ baseURL: 'http://127.0.0.1:5199' });

  expect((await anonymous.get('/api/products')).status()).toBe(401);

  await anonymous.dispose();
});
