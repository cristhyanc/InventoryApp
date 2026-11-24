import { BrowserContext, Page, Response } from '@playwright/test';
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
 * states it produces. One of the three pages does not decide from that answer at all - see the
 * comment on the Dashboard test, which records a predicted failure and the decision it needs.
 *
 * **The observation window.** Every request is recorded on the *browser context*, installed before
 * the navigation that triggers the first one, and what a request can do is recorded in three
 * separate records, because they answer three different questions:
 *
 *  - `request` - every URL the browser **attempted**. This, not the responses, is what "no request
 *    goes to any Nayax host" has to be asserted over: an outbound call to a Nayax address from a
 *    host that has no Nayax client dies in DNS or at the socket, so it never becomes a response at
 *    all, and a recorder listening only to `response` cannot see it. The last test in this file
 *    makes exactly that happen, on purpose, and proves this record catches it.
 *  - `response` - the answered requests and their statuses, which is what "no request answers 5xx"
 *    is asserted over. Only an answer can carry a status.
 *  - `requestfailed` - an attempt that got no answer, with the browser's own reason for it.
 *    Recorded and asserted rather than dropped, so a request that never completed is *visible*
 *    instead of looking exactly like a request that was never made.
 *
 * **Nothing is judged before the answer it depends on has arrived.** The core Dashboard labels and
 * the Machines heading render while the provider state is still being read, so a hidden panel, a
 * `toHaveCount(0)` or an empty request record proves nothing on its own: all three are equally true
 * of a page that has not decided yet, and a `500` or a fail-open panel can still arrive afterwards.
 * Each test therefore waits for the authoritative status answer and asserts `200`/`NotConfigured`
 * first, waits for that page's own data response, and asserts a **positive** mark of the settled
 * locked state - the storage-only reason on the Dashboard, the "not fully connected" banner on
 * Machines, the connection-required page for the report - before asserting what is absent and
 * before reading the recorder, which itself refuses to pass over an observation that did not even
 * capture the status read. Nothing here sleeps, mocks a route or stubs a response: every answer
 * asserted on is the real E2E host's.
 */

const NAYAX_HOST_PATTERN = /nayax/i;

/** The authoritative provider answer every page in this file depends on. */
const STATUS_PATH = '/api/settings/nayax-connection/status';

/** The Dashboard's own provider statement: the live-fleet section of its one summary read. */
const DASHBOARD_SUMMARY_PATH = '/api/dashboard/summary';

/** The business's own machine records, which need no provider at all. */
const LOCAL_MACHINES_PATH = '/api/local-machines';

/** The live Nayax fleet read, which must not even be attempted without a connection. */
const PROVIDER_MACHINES_PATH = '/api/machines';

/** One attempted request: the URL the browser asked for, and the host it asked it of. */
interface AttemptedRequest {
  readonly url: string;
  readonly host: string;
}

/** One answered request. Kept apart from the attempts: only an answer can carry a status. */
interface AnsweredRequest {
  readonly url: string;
  readonly status: number;
}

/** One attempt that got no answer: DNS, TLS, a refused connection, an abort. */
interface FailedRequest {
  readonly url: string;
  readonly host: string;
  readonly error: string;
}

/** What the browser did, as three separate records. See the file doc comment. */
interface Observation {
  readonly attempted: AttemptedRequest[];
  readonly answered: AnsweredRequest[];
  readonly failed: FailedRequest[];
}

/**
 * A request URL's host, and below it its path. Both answer `''` for a URL a browser can produce that
 * the URL parser cannot read: such a request is still an attempt and must still be recorded, rather
 * than throwing inside an event handler where nothing would report it.
 */
function hostOf(url: string): string {
  try {
    return new URL(url).hostname;
  } catch {
    return '';
  }
}

function pathOf(url: string): string {
  try {
    return new URL(url).pathname;
  } catch {
    return '';
  }
}

/**
 * Starts recording every request of this actor's session. Installed on the browser context rather
 * than the page, so a request made by anything in that context is seen, and called before the
 * navigation that triggers the first request.
 */
function observeRequests(page: Page): Observation {
  const observation: Observation = { attempted: [], answered: [], failed: [] };
  const context: BrowserContext = page.context();

  context.on('request', (request) =>
    observation.attempted.push({ url: request.url(), host: hostOf(request.url()) })
  );
  context.on('response', (response) =>
    observation.answered.push({ url: response.url(), status: response.status() })
  );
  context.on('requestfailed', (request) =>
    observation.failed.push({
      url: request.url(),
      host: hostOf(request.url()),
      error: request.failure()?.errorText ?? 'no reason reported'
    })
  );

  return observation;
}

/** Every attempt against a Nayax host, whether it was answered, failed, or never resolved. */
function nayaxAttempts(observation: Observation): string[] {
  return observation.attempted
    .filter((attempt) => NAYAX_HOST_PATTERN.test(attempt.host))
    .map((attempt) => attempt.url);
}

/** Every answer that was a server error. */
function serverErrors(observation: Observation): string[] {
  return observation.answered
    .filter((answer) => answer.status >= 500)
    .map((answer) => `${answer.status} ${answer.url}`);
}

/** Every attempt that produced no answer at all, with the reason the browser gave. */
function unanswered(observation: Observation): string[] {
  return observation.failed.map((failure) => `${failure.error} ${failure.url}`);
}

/**
 * The issue's two whole-run assertions, over everything the browser attempted and everything it was
 * answered - plus the one that keeps them from passing vacuously: an observation that did not even
 * capture the status read was not open when the page made its requests, and "nothing was recorded"
 * is not evidence that nothing went wrong.
 */
function assertNoRefusalsAndNoNayaxHost(observation: Observation): void {
  expect(
    observation.attempted.map((attempt) => pathOf(attempt.url)),
    'the observation window was open before the page made its requests'
  ).toContain(STATUS_PATH);
  expect(serverErrors(observation), 'no request answered 5xx').toEqual([]);
  expect(nayaxAttempts(observation), 'no request was attempted against a Nayax host').toEqual([]);
  expect(unanswered(observation), 'every request the browser attempted was answered').toEqual([]);
}

/**
 * The response to one API path. Called **before** the navigation that triggers the request, so the
 * answer cannot be missed, and awaited before anything that depends on it is judged.
 */
function apiResponse(page: Page, path: string): Promise<Response> {
  return page.waitForResponse((response) => pathOf(response.url()) === path);
}

/**
 * The authoritative provider answer this whole file rests on: `200` with the `NotConfigured` status
 * production gives a business that never connected Nayax. Asserted before any locked-state
 * assertion, so a label that rendered while the probe was still in flight is never read as the
 * locked state, and a `500` from this endpoint fails the test here rather than silently failing
 * open into a page that looks connected.
 */
async function readNotConfigured(statusResponse: Promise<Response>): Promise<void> {
  const response = await statusResponse;
  expect(response.status(), `GET ${STATUS_PATH} answered`).toBe(200);

  const state = (await response.json()) as { status?: string };
  expect(state.status, 'this business has no stored Nayax connection').toBe('NotConfigured');
}

/**
 * **This test is expected to fail on the current E2E host, for a reason outside issue #1276, and
 * its assertions are deliberately left stating what the issue asks for.**
 *
 * The Dashboard does not take its provider answer from the status read this issue fixed. It takes it
 * from its own summary: "Needs refill" is the one section computed from the live fleet, so a
 * withheld section is how `GET /api/dashboard/summary` states that there is no fleet to report on,
 * and `dashboard.component.ts` (`providerAvailabilityOf`) reads a *present* section as a usable
 * provider. In production that section is withheld for a business with no credentials, because
 * `CalculateReorderNeeds` reads the fleet through `INayaxLynxClient`, which is the
 * `CachingNayaxLynxClient` decorator, which resolves credentials through
 * `NayaxRequestCredentialProvider` - and that is what throws `NayaxNotConnectedException` for a
 * `NotConfigured` connection, giving `ReorderMode.StorageOnly` and the `NayaxNotConnected` reason.
 *
 * In the E2E host `INayaxLynxClient` is `E2ETestNayaxLynxClient`, which answers an **empty fleet**
 * instead of refusing and resolves no credential at all (deliberately: the seeded businesses own no
 * machines, so every product's machine replenishment need is genuinely zero, and the existing
 * reorder journeys depend on that machine-aware answer). The summary is therefore machine-aware with
 * zeros, the provider reads as usable, and this page renders the two provider cards and the Site and
 * machine performance section that the assertions below say are absent.
 *
 * Making this page lock would mean giving the E2E host's fleet read the production credential gate,
 * which changes the state **every other** browser journey runs in. That is a human decision and is
 * outside both this issue's acceptance criteria and the repair request that produced this file; it
 * is reported in the pull request instead. The Machines page and the provider report below are
 * unaffected: both decide from the shared status answer this issue fixed, not from the summary.
 */
test('Dashboard shows the core inventory figures with its provider panels locked', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const observed = observeRequests(page);
  const statusRead = apiResponse(page, STATUS_PATH);
  const summaryRead = apiResponse(page, DASHBOARD_SUMMARY_PATH);

  await page.goto('/');

  await readNotConfigured(statusRead);

  // The Dashboard's own provider decision is its summary's, not the status probe's: "Needs refill"
  // is the one section of that response computed from the live fleet, so a withheld section
  // carrying `NayaxNotConnected` is the server stating this page has no fleet to report on. Awaited
  // before any card below is judged, so nothing is asserted over a page that is still loading.
  const summary = await summaryRead;
  expect(summary.status(), `GET ${DASHBOARD_SUMMARY_PATH} answered`).toBe(200);
  const sections = (await summary.json()) as { needsRefill?: { available?: boolean; reason?: string } };
  expect(sections.needsRefill, 'the live-fleet section is withheld, naming the reason').toMatchObject({
    available: false,
    reason: 'NayaxNotConnected'
  });

  // Core inventory figures: no sales provider needed, so these are always served. Each card is
  // located by its own accessible name, which is unique on the page - the label text alone is not,
  // because a card's footer may repeat it.
  const orderingCard = page.getByRole('link', { name: 'Products needing ordering. View products that need ordering.' });
  await expect(orderingCard).toBeVisible();
  await expect(orderingCard).toContainText('Products needing ordering');

  const inventoryCard = page.getByRole('link', { name: 'Business-owned inventory at cost. View Products.' });
  await expect(inventoryCard).toBeVisible();
  await expect(inventoryCard).toContainText('Business-owned inventory at cost');

  // The rendered form of the answer asserted above, and the positive mark that the summary has been
  // applied: the storage-only count says what it does not account for *and why*. It is what makes
  // the absences below a settled state rather than a page that has not decided yet.
  await expect(orderingCard).toContainText(
    "Storage stock only: these figures do not include the machines' current replenishment need, because Nayax is not connected."
  );

  // The two provider panels: no card for either, and no Site and machine performance section.
  await expect(page.getByRole('link', { name: 'Sales this week. View Bookkeeping.' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Machines needing refill. Open the Pick List.' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Site and machine performance' })).toHaveCount(0);

  assertNoRefusalsAndNoNayaxHost(observed);
});

test('Machines shows this business\'s own machines with no provider machine review panel', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const observed = observeRequests(page);
  const statusRead = apiResponse(page, STATUS_PATH);
  const localMachinesRead = apiResponse(page, LOCAL_MACHINES_PATH);

  await page.goto('/machines');

  await readNotConfigured(statusRead);

  // The business's own machine records are always shown, with no provider, and this page has
  // finished loading them before anything below is judged.
  const localMachines = await localMachinesRead;
  expect(localMachines.status(), `GET ${LOCAL_MACHINES_PATH} answered`).toBe(200);
  await expect(page.getByRole('heading', { name: 'Machines', exact: true })).toBeVisible();

  // The positive mark of the settled answer on this page: the shared banner is shown only once the
  // status has been read and is not `Ready`, so it cannot be the fail-open state and cannot be a
  // status still in flight.
  await expect(page.getByTestId('nayax-status-banner')).toBeVisible();
  await expect(page.getByTestId('nayax-status-banner')).toContainText(
    "Nayax isn't fully connected for this business."
  );

  // The live Nayax fleet has no connection to read, so neither it nor the review panel over it is
  // rendered at all - and the fleet read is not even sent.
  await expect(page.locator('app-provider-machine-review')).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Live from Nayax' })).toHaveCount(0);
  expect(
    observed.attempted.filter((attempt) => pathOf(attempt.url) === PROVIDER_MACHINES_PATH),
    'the live fleet read is not attempted for a business with no connection'
  ).toEqual([]);

  assertNoRefusalsAndNoNayaxHost(observed);
});

test('The Daily report shows the connection-required state', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const observed = observeRequests(page);
  const statusRead = apiResponse(page, STATUS_PATH);

  await page.goto('/reports/daily');

  await readNotConfigured(statusRead);

  // The redirect, the destination's own title and the one action offered are all decided from that
  // settled answer: the page starts with a generic title and no Connect button, so both are a
  // positive mark that the connection lock - not an unread status - is what this page is in.
  await expect(page).toHaveURL(/\/needs-provider\?/);
  await expect(page.getByRole('heading', { name: 'Daily needs Nayax' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Connect Nayax' })).toBeVisible();

  assertNoRefusalsAndNoNayaxHost(observed);
});

test('an outbound Nayax attempt that never answers is still detected', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  const observed = observeRequests(page);
  const statusRead = apiResponse(page, STATUS_PATH);

  await page.goto('/');
  await readNotConfigured(statusRead);

  // What the three journeys above assert the absence of, made to happen once on purpose, so that
  // their absence is a result the recorder can actually produce rather than an assumption.
  //
  // `.invalid` is reserved by RFC 2606 and resolves nowhere, so this is an attempt against a Nayax
  // host that dies in DNS without ever producing a response: no Nayax service is contacted, and
  // none could be. That is the exact shape a genuine outbound Nayax call takes from a host with no
  // Nayax client - and the shape a recorder listening only to `response` cannot see at all.
  const probeUrl = 'https://lynx.nayax.invalid/e2e-outbound-attempt-probe';
  const probeFailed = page.waitForEvent('requestfailed', (request) => request.url() === probeUrl);
  await page.evaluate((url) => void fetch(url, { mode: 'no-cors' }).catch(() => undefined), probeUrl);
  await probeFailed;

  // The record the three journeys' Nayax-host assertion reads sees it...
  expect(nayaxAttempts(observed), 'the attempted Nayax host is recorded').toEqual([probeUrl]);
  // ...the failure is visible, with the browser's own reason...
  expect(unanswered(observed).join('\n'), 'the failed attempt is recorded as a failure').toContain(probeUrl);
  // ...and the response record - all this file used to have - never saw it, which is the defect
  // this test exists to keep fixed.
  expect(
    observed.answered.filter((answer) => answer.url === probeUrl),
    'a request that fails before a response produces no response to record'
  ).toEqual([]);
});
