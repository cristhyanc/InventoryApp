import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence issue #409's series rules require for #460: the Dashboard's four headline
 * cards (Sales this week, Needs refill, Needs ordering, Inventory), captured at 1440px and 390px
 * from the real shell in a real browser.
 *
 * Like the other visual runs this starts no API and no database. Every `/api/` request the
 * Dashboard makes is answered in the browser with neutral synthetic figures: one complete summary,
 * one with incomplete costing and no prior-week baseline, and one where the summary request fails,
 * so the "unavailable, not zero" state is visible too. Sites, Machines and low-stock lists are
 * answered empty. No business data, account name or credential can appear in any capture.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-460');
mkdirSync(screenshotDirectory, { recursive: true });

function widthSuffix(projectName: string): string {
  return `${projectName.split('-').pop()}px`;
}

const period = {
  startUtc: '2026-01-04T13:00:00Z',
  endUtc: '2026-01-07T13:00:00Z',
  firstBusinessDate: '2026-01-05T00:00:00Z',
  lastBusinessDate: '2026-01-07T00:00:00Z'
};

const priorPeriod = {
  startUtc: '2025-12-28T13:00:00Z',
  endUtc: '2025-12-31T13:00:00Z',
  firstBusinessDate: '2025-12-29T00:00:00Z',
  lastBusinessDate: '2025-12-31T00:00:00Z'
};

const completeSummary = {
  asOfUtc: '2026-01-07T01:00:00Z',
  businessDate: '2026-01-07T00:00:00Z',
  salesThisWeek: {
    sales: 1250,
    transactionCount: 300,
    period,
    comparisonPeriod: priorPeriod,
    isComparisonAvailable: true,
    comparisonSales: 1000,
    comparisonTransactionCount: 250,
    changeAmount: 250,
    changePercent: 25,
    comparisonNote: null
  },
  needsRefill: {
    machinesNeedingRefill: 3,
    machinesWithEmptySelections: 1,
    machinesWithLowSelections: 3,
    emptySelectionCount: 2,
    lowSelectionCount: 7,
    machinesEvaluated: 10,
    selectionsEvaluated: 400
  },
  needsOrdering: { productsNeedingOrdering: 4, productsEvaluated: 50 },
  inventory: {
    inventoryValueAtCost: 5000,
    isInventoryValueComplete: true,
    productsWithUnknownCost: 0,
    productCount: 50,
    unitsInStorage: 1200
  }
};

const partialSummary = {
  ...completeSummary,
  salesThisWeek: {
    ...completeSummary.salesThisWeek,
    comparisonSales: 0,
    comparisonTransactionCount: 0,
    changeAmount: 1250,
    changePercent: null,
    comparisonNote: 'No sales in the same period last week to compare'
  },
  needsRefill: { ...completeSummary.needsRefill, machinesNeedingRefill: 0, emptySelectionCount: 0, lowSelectionCount: 0 },
  needsOrdering: { productsNeedingOrdering: 0, productsEvaluated: 50 },
  inventory: { ...completeSummary.inventory, inventoryValueAtCost: null, isInventoryValueComplete: false, productsWithUnknownCost: 5 }
};

async function openDashboard(page: Page, summary: object | 'error'): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const url = route.request().url();
    if (url.includes('/dashboard/summary')) {
      return summary === 'error'
        ? route.fulfill({ status: 500, contentType: 'application/json', body: '{}' })
        : route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(summary) });
    }
    if (route.request().method() === 'POST') {
      return route.fulfill({ status: 204, body: '' });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });

  await page.goto('/');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
  });
  await expect(page.getByRole('heading', { name: 'Dashboard', level: 1 })).toBeVisible();
}

async function capture(page: Page, name: string): Promise<void> {
  const cards = page.locator('section').filter({ has: page.getByText('Sales this week') }).first();
  await expect(cards).toBeVisible();
  await page.screenshot({
    path: join(screenshotDirectory, `${name}-${widthSuffix(test.info().project.name)}.png`),
    fullPage: false
  });
}

test.describe('Dashboard headline cards visual evidence', () => {
  test('complete summary', async ({ page }) => {
    await openDashboard(page, completeSummary);
    await expect(page.getByText('$1,250.00')).toBeVisible();
    await expect(page.getByText('↑ 25.0% vs same time last week')).toBeVisible();
    await capture(page, 'dashboard-cards-complete');
  });

  test('known zero, no prior-week baseline and incomplete costing', async ({ page }) => {
    await openDashboard(page, partialSummary);
    await expect(page.getByText('No sales in the same period last week to compare')).toBeVisible();
    await expect(page.getByText('5 of 50 products missing cost data')).toBeVisible();
    await capture(page, 'dashboard-cards-partial');
  });

  test('summary request failed', async ({ page }) => {
    await openDashboard(page, 'error');
    await expect(page.getByText('The Dashboard summary could not be loaded.', { exact: false })).toBeVisible();
    await capture(page, 'dashboard-cards-error');
  });
});
