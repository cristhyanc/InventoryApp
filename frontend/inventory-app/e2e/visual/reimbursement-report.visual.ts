import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence issue #1152 asks for: the Reimbursement Report page
 * (`/admin/imports/reimbursement-report`) at 1440px and at 390px, captured from the real shell in a
 * real browser, with the browser itself asked the one question jsdom cannot answer - whether the
 * page lays out at phone width without scrolling sideways.
 *
 * <p>Like the other visual runs this starts no API and no database: every `/api/` request is
 * answered in the browser with neutral synthetic uploads (`sample-reimbursements-*.zip`, invented
 * file names, no business name, no figure, no credential), so nothing real can appear in a capture.
 * The jobs cover every state the API reports - queued, processing, completed, completed with
 * failures and failed - because the phone layout has to hold for the longest row too.</p>
 *
 * <p>Each test asserts, before it captures: the page has no horizontal overflow, the 390px gutter is
 * the 16px `.page` padding, and at 390px each upload's summary stacks (its file name sits above its
 * state and counts) while at 1440px the same summary is laid out as a row. It runs once, in the
 * `desktop-1440` project, and sets each width itself.</p>
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1152');
mkdirSync(screenshotDirectory, { recursive: true });

const MOBILE_GUTTER = '16px';

const OWNER_CAPABILITIES = [
  'Dashboard.View',
  'Dashboard.ViewFinancials',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate',
  'Expenses.Manage',
  'Reports.View',
  'Imports.Run',
  'SiteCommissions.Manage',
  'Integration.Manage',
  'Members.Manage',
  'Roles.View',
  'Business.Manage'
];

// state: Queued 0, Processing 1, Completed 2, CompletedWithFailures 3, Failed 4.
const jobs = [
  {
    id: 94,
    fileName: 'sample-reimbursements-week-45.zip',
    uploadedAtUtc: '2026-11-09T01:32:00Z',
    state: 0,
    attempts: 0,
    nextAttemptAtUtc: null,
    lastError: null,
    finishedAtUtc: null,
    importedFiles: 0,
    skippedFiles: 0,
    failedFiles: 0
  },
  {
    id: 93,
    fileName: 'sample-reimbursements-week-44.zip',
    uploadedAtUtc: '2026-11-02T01:18:00Z',
    state: 1,
    attempts: 1,
    nextAttemptAtUtc: null,
    lastError: null,
    finishedAtUtc: null,
    importedFiles: 2,
    skippedFiles: 0,
    failedFiles: 0
  },
  {
    id: 92,
    fileName: 'sample-reimbursements-week-43.zip',
    uploadedAtUtc: '2026-10-26T01:21:00Z',
    state: 3,
    attempts: 1,
    nextAttemptAtUtc: null,
    lastError: null,
    finishedAtUtc: '2026-10-26T01:22:00Z',
    importedFiles: 4,
    skippedFiles: 1,
    failedFiles: 2
  },
  {
    id: 91,
    fileName: 'sample-reimbursements-week-42.zip',
    uploadedAtUtc: '2026-10-19T01:11:00Z',
    state: 2,
    attempts: 1,
    nextAttemptAtUtc: null,
    lastError: null,
    finishedAtUtc: '2026-10-19T01:12:00Z',
    importedFiles: 6,
    skippedFiles: 0,
    failedFiles: 0
  },
  {
    id: 90,
    // A deliberately long name, because an unbreakable file name is how a phone layout overflows.
    fileName: 'sample-reimbursements-october-reprocessed-after-a-very-long-upload-name.zip',
    uploadedAtUtc: '2026-10-12T01:09:00Z',
    state: 4,
    attempts: 3,
    nextAttemptAtUtc: null,
    lastError: 'The archive could not be opened.',
    finishedAtUtc: '2026-10-12T01:10:00Z',
    importedFiles: 0,
    skippedFiles: 0,
    failedFiles: 0
  }
];

// outcome: Imported 0, Skipped 1, Failed 2.
const failedJobDetail = {
  job: jobs[2],
  files: [
    { file: '10-2026/sample_report_1.xml', outcome: 0, reason: null },
    { file: '10-2026/sample_report_2.xml', outcome: 1, reason: 'Already imported.' },
    { file: '10-2026/sample_report_3.xml', outcome: 2, reason: 'The XML could not be read.' },
    { file: '10-2026/sample_report_4.xml', outcome: 2, reason: 'The report has no machine this business owns.' }
  ]
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function answerApi(page: Page, uploads: unknown[] = jobs): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/me/access')) {
      return json(route, {
        role: 'Owner',
        capabilities: OWNER_CAPABILITIES,
        businessName: 'Sample Vending',
        timeZoneId: 'Australia/Sydney'
      });
    }
    if (path.endsWith('/me/account-state')) {
      return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    }
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/imports/reimbursement-report/jobs')) return json(route, uploads);
    if (/\/imports\/reimbursement-report\/jobs\/\d+$/.test(path)) return json(route, failedJobDetail);
    if (route.request().method() !== 'GET') return route.fulfill({ status: 204, body: '' });
    return json(route, []);
  });
}

async function openPage(page: Page, width: number, uploads: unknown[] = jobs): Promise<void> {
  await page.setViewportSize({ width, height: width >= 1024 ? 900 : 844 });
  await answerApi(page, uploads);
  await page.goto('/admin/imports/reimbursement-report');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
  });
  await expect(page.getByRole('heading', { name: 'Reimbursement Report', level: 1 })).toBeVisible();
  await expect(page.locator('[data-testid="job-row"]')).toHaveCount(uploads.length);
}

/** The one question jsdom cannot answer: does the document scroll sideways at this width? */
async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, 'the page does not scroll sideways').toBeLessThanOrEqual(0);
}

async function capture(page: Page, name: string, fullPage = true): Promise<void> {
  await expectNoHorizontalOverflow(page);
  await page.screenshot({ path: join(screenshotDirectory, `${name}.png`), fullPage });
}

/**
 * The phone shell's fixed bottom navigation, hidden for the one full-page capture it would spoil: a
 * fixed bar is painted at the first viewport's foot in a scrolled capture, so in a list this long it
 * would sit across an upload in the middle of it. Where the bar really sits is evidence of its own,
 * so the empty-state capture below is a viewport capture taken with it in place.
 */
async function hideBottomNav(page: Page): Promise<void> {
  await page.addStyleTag({ content: '[data-testid="bottom-nav"] { display: none !important; }' });
}

function padding(page: Page, selector: string): Promise<{ left: string; right: string }> {
  return page.locator(selector).evaluate((element) => {
    const computed = getComputedStyle(element);
    return { left: computed.paddingLeft, right: computed.paddingRight };
  });
}

/** How an upload's summary is laid out: `stacked` when its two halves sit one above the other. */
async function summaryLayout(page: Page, rowIndex: number): Promise<'stacked' | 'side-by-side'> {
  const row = page.locator('[data-testid="job-row"]').nth(rowIndex);
  const file = (await row.locator('[data-testid="job-file"]').boundingBox())!;
  const state = (await row.locator('[data-testid="job-state"]').boundingBox())!;
  return state.y >= file.y + file.height ? 'stacked' : 'side-by-side';
}

test.describe('Reimbursement Report page visual evidence (issue #1152)', () => {
  test.beforeEach(({}, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop-1440', 'Each test sets its own width.');
  });

  test('1 the page at 1440px, with a failed upload expanded', async ({ page }) => {
    await openPage(page, 1440);

    await expect(page.locator('[data-testid="reimbursement-zip-input"]')).toBeAttached();
    await expect(page.getByRole('heading', { name: 'Recent uploads', level: 2 })).toBeVisible();
    await expect(page.locator('[data-testid="job-state"]')).toHaveText([
      'Queued',
      'Processing',
      'Completed with failures',
      'Completed',
      'Failed'
    ]);
    // 01:21 UTC on 26 October 2026 is 12:21 pm in Sydney (UTC+11): the instants are the business's.
    await expect(page.locator('[data-testid="job-row"]').nth(2).locator('[data-testid="job-uploaded-at"]')).toContainText(
      '26/10/2026'
    );
    expect(await summaryLayout(page, 2)).toBe('side-by-side');

    // The failed file names and their reasons, which is the one thing a row has to be opened for.
    await page.locator('[data-testid="job-row"]').nth(2).locator('[data-testid="job-expand"]').click();
    await expect(page.locator('[data-testid="job-failed-file"]')).toHaveCount(2);
    await expect(page.locator('[data-testid="job-detail"]')).toContainText('The XML could not be read.');
    // No figure and no payment detail is on screen: the status contract carries none.
    expect(await page.locator('.page').innerText()).not.toContain('$');

    await capture(page, 'reimbursement-report-desktop-1440');
  });

  test('2 the page at 390px: a 16px gutter, stacked uploads and no sideways scroll', async ({ page }) => {
    await openPage(page, 390);

    // The 16px mobile gutter is the shared `.page` padding, measured as the browser computed it.
    expect(await padding(page, '.page')).toEqual({ left: MOBILE_GUTTER, right: MOBILE_GUTTER });

    // Every upload stacks: its file name sits above its state and counts, nothing is beside it.
    for (let index = 0; index < jobs.length; index++) {
      expect(await summaryLayout(page, index), `upload ${index} stacks at 390px`).toBe('stacked');
    }

    // Including the long file name, which stays inside the card rather than pushing it wider.
    const longest = page.locator('[data-testid="job-row"]').nth(4).locator('[data-testid="job-file"]');
    const card = page.locator('[data-testid="job-row"]').nth(4);
    expect((await longest.boundingBox())!.width).toBeLessThanOrEqual((await card.boundingBox())!.width);

    await hideBottomNav(page);
    await capture(page, 'reimbursement-report-mobile-390');
  });

  test('3 the upload card and the empty list at 390px', async ({ page }) => {
    await openPage(page, 390, []);

    // The state an operator sees first: nothing uploaded yet, and the picker ready.
    await expect(page.locator('[data-testid="jobs-empty"]')).toBeVisible();
    await expect(page.locator('[data-testid="reimbursement-upload-button"]')).toBeDisabled();
    await expect(page.locator('[data-testid="reimbursement-upload-note"]')).toContainText('exactly as Nayax emails it');
    expect(await padding(page, '.page')).toEqual({ left: MOBILE_GUTTER, right: MOBILE_GUTTER });

    await expect(page.locator('[data-testid="bottom-nav"]')).toBeVisible();
    await capture(page, 'reimbursement-report-mobile-empty-390', false);
  });
});
