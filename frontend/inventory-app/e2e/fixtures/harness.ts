import { APIRequestContext, BrowserContext, Page, expect, test as base } from '@playwright/test';

/**
 * The harness every end-to-end test speaks through (issue #46).
 *
 * The suite drives the real Angular application in a real browser against the real API, so the
 * only thing it has to arrange is *who* is calling. That is one request header, set on the browser
 * context, naming one of the synthetic actors the dedicated E2E host knows; the host then applies
 * its ordinary authentication, scope, membership and tenant-isolation rules to every request the
 * application makes. No token, no sign-in, no identity provider.
 *
 * The constants below are the contract with the backend fixture
 * (`backend/InventoryApi/Auth/E2ETesting/E2ETestFixture.cs` and `E2ETestActors.cs`). Changing a
 * name or an actor key means changing both sides; `E2ETestAuthenticationTests` asserts the backend
 * half, so a rename that breaks the contract fails the backend suite too rather than only here.
 */
export const API_PORT = 5199;
export const APP_PORT = 4300;
export const API_BASE_URL = `http://127.0.0.1:${API_PORT}`;
export const APP_BASE_URL = `http://127.0.0.1:${APP_PORT}`;

/** Selects a synthetic actor. It does not, and cannot, enable the E2E scheme. */
export const ACTOR_HEADER = 'X-E2E-Test-Actor';

export const actors = {
  businessAOwner: 'business-a-owner',
  businessBOwner: 'business-b-owner',
  noMembership: 'no-membership'
} as const;

/** Seeded fixture data. Keep in step with E2ETestFixture.cs. */
export const seeded = {
  businessA: 'E2E Business A',
  businessB: 'E2E Business B',
  reorderProduct: 'E2E Reorder Chips',
  correctionProduct: 'E2E Correction Bars',
  purchaseProduct: 'E2E Purchase Water',
  businessBProduct: 'E2E Business B Chocolate',
  supplierA: 'E2E Supplier A',
  /** The business date (Australia/Sydney) of the seeded uncosted completed card sale. */
  uncostedSaleDate: '2026-03-02',
  uncostedSaleAmount: 5.5
} as const;

/** A minimal valid 1x1 PNG, so a purchase can be created without committing a real receipt. */
export const receiptFile = {
  name: 'e2e-receipt.png',
  mimeType: 'image/png',
  buffer: Buffer.from(
    '89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a4944415478' +
      '9c6360000002000154a24f5f0000000049454e44ae426082',
    'hex'
  )
} as const;

export interface SeededProduct {
  id: number;
  name: string;
  quantityInStock: number;
  costingQuantity: number | null;
  inventoryValue: number | null;
  averageUnitCost: number;
  needToOrder: number;
}

interface HarnessFixtures {
  /** A browser page whose every request speaks as one synthetic actor. */
  pageAs: (actor: string) => Promise<Page>;

  /**
   * A direct API client for the same actor. Used to arrange nothing and to verify the
   * authoritative inventory/costing values a page does not display, never to perform the workflow
   * under test - that always goes through the browser.
   */
  apiAs: (actor: string) => Promise<APIRequestContext>;
}

export const test = base.extend<HarnessFixtures>({
  pageAs: async ({ browser }, use) => {
    const contexts: BrowserContext[] = [];

    await use(async (actor: string) => {
      const context = await browser.newContext({ extraHTTPHeaders: { [ACTOR_HEADER]: actor } });
      contexts.push(context);
      return context.newPage();
    });

    for (const context of contexts) {
      await context.close();
    }
  },

  apiAs: async ({ playwright }, use) => {
    const contexts: APIRequestContext[] = [];

    await use(async (actor: string) => {
      const context = await playwright.request.newContext({
        baseURL: API_BASE_URL,
        extraHTTPHeaders: { [ACTOR_HEADER]: actor }
      });
      contexts.push(context);
      return context;
    });

    for (const context of contexts) {
      await context.dispose();
    }
  }
});

export { expect };

/** The authoritative product record, read back from the API the application itself calls. */
export async function productByName(api: APIRequestContext, name: string): Promise<SeededProduct> {
  const response = await api.get('/api/products');
  expect(response.status(), 'the authorised actor can list its own products').toBe(200);

  const products = (await response.json()) as SeededProduct[];
  const product = products.find((candidate) => candidate.name === name);

  expect(product, `seeded product "${name}" is present for this business`).toBeTruthy();
  return product!;
}

/**
 * The products the API currently reports as needing ordering. This is a different endpoint from the
 * catalogue list above on purpose: only the reorder endpoint accounts for outstanding supplier
 * orders and machine replenishment need, so it is the authoritative answer to "does this product
 * still need ordering?".
 */
export async function reorderAlertNames(api: APIRequestContext): Promise<string[]> {
  const response = await api.get('/api/products/alerts/low-stock');
  expect(response.status()).toBe(200);

  return ((await response.json()) as SeededProduct[]).map((product) => product.name);
}

export async function productById(api: APIRequestContext, id: number): Promise<SeededProduct> {
  const response = await api.get(`/api/products/${id}`);
  expect(response.status()).toBe(200);
  return (await response.json()) as SeededProduct;
}

/** Today in the browser's local calendar, which is what the date inputs are prefilled with. */
export function localToday(): string {
  const now = new Date();
  const month = `${now.getMonth() + 1}`.padStart(2, '0');
  const day = `${now.getDate()}`.padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}
