import { Page } from '@playwright/test';
import { actors, expect, test } from '../fixtures/harness';

/**
 * Business A (no Nayax) on Dashboard, Machines and one provider report (issue #1276, audit #1225
 * finding FE-05/FE-12).
 *
 * Neither seeded E2E business stores Nayax credentials, so this is the state production itself
 * gives a business that never connected a provider - not a stub, and not a harness artefact. Before
 * this issue the E2E host could not answer `GET /api/settings/nayax-connection/status` at all (it
 * registered no `INayaxConnectionCheckRunner`, so `NayaxConnectionController` could not be
 * constructed), which every page's status read turned into a `500`; the frontend then read that
 * failure as `unknown`, which fails **open** (`providerFeatureOffered`) - so a page that must lock
 * without Nayax opened anyway, and the Machines page went on to show the provider machine review
 * panel and call `POST /api/machine-link-review`, which 500s for a business with no connection at
 * all (a state production never reaches; see `machine-list.component.ts` and
 * `avco-transition-checkbox-layout.e2e.ts`, which documents the old fail-open behaviour this issue
 * retires). This file is the replacement: the real `NotConfigured` answer, and the real locked
 * states it produces.
 *
 * Every request every page makes is recorded, so "no 5xx" and "no Nayax host" are proved over the
 * whole run rather than over the one status read.
 */

const NAYAX_HOST_PATTERN = /nayax/i;

function recordRequests(page: Page): { statuses: number[]; hosts: string[] } {
  const record = { statuses: [] as number[], hosts: [] as string[] };

  page.on('response', (response) => {
    record.statuses.push(response.status());
    record.hosts.push(new URL(response.url()).hostname);
  });

  return record;
}

function assertNoRefusalsAndNoNayaxHost(record: { statuses: number[]; hosts: string[] }): void {
  expect(record.statuses.some((status) => status >= 500), 'no request answered 5xx').toBe(false);
  expect(
    record.hosts.some((host) => NAYAX_HOST_PATTERN.test(host)),
    'no request reached a Nayax host'
  ).toBe(false);
}

test('Dashboard shows the core inventory figures with its provider panels locked', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const requests = recordRequests(page);

  await page.goto('/');

  // Core inventory figures: no sales provider needed, so these are always served.
  await expect(page.getByText('Products needing ordering')).toBeVisible();
  await expect(page.getByText('Business-owned inventory at cost')).toBeVisible();
  // The storage-only note, read from this business's own stored stock rather than a live fleet.
  await expect(page.getByText(/does not include the machines' current replenishment need/)).toBeVisible();

  // The two provider panels: no card for either, and no Site and machine performance section.
  await expect(page.getByText('Sales this week')).toBeHidden();
  await expect(page.getByText('Machines needing refill')).toBeHidden();
  await expect(page.getByText('Site and machine performance')).toBeHidden();

  assertNoRefusalsAndNoNayaxHost(requests);
});

test('Machines shows this business\'s own machines with no provider machine review panel', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const requests = recordRequests(page);

  await page.goto('/machines');

  // The business's own machine records are always shown, with no provider.
  await expect(page.getByRole('heading', { name: 'Machines', exact: true })).toBeVisible();
  // The live Nayax fleet review has no connection to read, so it is not rendered at all.
  await expect(page.locator('app-provider-machine-review')).toHaveCount(0);

  assertNoRefusalsAndNoNayaxHost(requests);
});

test('The Daily report shows the connection-required state', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const requests = recordRequests(page);

  await page.goto('/reports/daily');

  await expect(page).toHaveURL(/\/needs-provider\?/);
  await expect(page.getByRole('heading', { name: 'Daily needs Nayax' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Connect Nayax' })).toBeVisible();

  assertNoRefusalsAndNoNayaxHost(requests);
});
