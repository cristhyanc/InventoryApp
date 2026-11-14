import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The browser evidence for issue #1209: the paged Purchases list at 390 and 1440px. It checks that
 * the pager's per-page select, Previous and Next are reached with Tab and show a visible focus
 * indicator, and that the page has no horizontal overflow while the table scrolls inside its card.
 * Since issue #1234 it also checks that the list renders a plain attachment icon from metadata
 * alone, rather than a downloaded receipt thumbnail, for every row that has a document.
 *
 * Like the other visual runs this starts no API and no database. Every `/api/` request is answered
 * in the browser with neutral synthetic data. It runs once, in the `desktop-1440` project, and sets
 * each width itself.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1209');
mkdirSync(screenshotDirectory, { recursive: true });

const OWNER_CAPABILITIES = [
  'Dashboard.View',
  'Dashboard.ViewFinancials',
  'PickList.View',
  'Inventory.Operate',
  'Purchasing.Operate',
  'Suppliers.ManageGstDefaults',
  'Expenses.Manage',
  'Reports.View',
  'Imports.Run',
  'SiteCommissions.Manage',
  'Integration.Manage',
  'Data.Repair',
  'Members.Manage',
  'Roles.View',
  'Business.Manage'
];

// An 8x8 slate-grey PNG: what an image document's file request would answer with, if the list
// made one. It never does (issue #1234); the route exists only so a stray request fails loudly.
const PIXEL_PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4nGM4ffUhVsQwtCQAl+qgQfQVZUwAAAAASUVORK5CYII=',
  'base64'
);

/** 25 synthetic purchases, newest first; every third one has an image receipt, every fifth none. */
const purchases = Array.from({ length: 25 }, (_, index) => {
  const id = index + 1;
  const day = String(28 - index).padStart(2, '0');
  const hasNone = id % 5 === 0;
  const isImage = id % 3 === 0;
  let fileName: string | null = null;
  let contentType: string | null = null;
  if (!hasNone) {
    fileName = isImage ? `receipt-${id}.jpg` : `invoice-${id}.pdf`;
    contentType = isImage ? 'image/png' : 'application/pdf';
  }
  return {
    purchase: {
      id,
      title: `Sample Wholesale — ${day} Oct 2026`,
      invoiceNumber: id % 2 ? `INV-${1000 + id}` : null,
      notes: null,
      totalAmount: 100 + id,
      deliveryCost: null,
      deliveryGstClassification: 0,
      packageCost: null,
      packageGstClassification: 0,
      purchaseDate: `2026-10-${day}T00:00:00Z`,
      supplierId: 1,
      supplier: { id: 1, name: 'Sample Wholesale' },
      fileName,
      storedFileName: hasNone ? null : `stored-${id}`,
      contentType,
      fileSizeBytes: hasNone ? null : 20480,
      createdAt: `2026-10-${day}T01:00:00Z`,
      items: [
        { id: id * 10, productId: 1, quantity: 24, unitCost: 1.25, gstClassification: 1, product: { id: 1, name: 'Sample Cola' } }
      ]
    },
    validation: { hasTotalMismatch: false, calculatedItemSubtotal: 30, calculatedTotal: 30, totalDifference: null },
    gst: { inputGst: 2.73, unresolvedComponentCount: 0, unresolvedAmount: 0 }
  };
});

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

/** Tracks every purchase file request the page makes, so a test can assert it made none. */
function trackFileRequests(page: Page): number[] {
  const requestedIds: number[] = [];
  page.on('request', (request) => {
    const match = /\/purchases\/(\d+)\/file$/.exec(new URL(request.url()).pathname);
    if (match) requestedIds.push(Number(match[1]));
  });
  return requestedIds;
}

async function answerApi(page: Page): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/me/access')) {
      return json(route, { role: 'Owner', capabilities: OWNER_CAPABILITIES, businessName: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    }
    if (path.endsWith('/me/account-state')) return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (/\/purchases\/\d+\/file$/.test(path)) return route.fulfill({ status: 200, contentType: 'image/png', body: PIXEL_PNG });
    if (path.endsWith('/purchases')) return json(route, purchases);
    return json(route, []);
  });
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

/** Presses Tab until the element with `testId` has focus, and asserts it shows a focus indicator. */
async function tabTo(page: Page, testId: string): Promise<void> {
  for (let press = 0; press < 200; press++) {
    await page.keyboard.press('Tab');
    const focused = await page.evaluate(() => (document.activeElement as HTMLElement | null)?.dataset['testid'] ?? null);
    if (focused === testId) {
      const indicator = await page.evaluate(() => {
        const style = getComputedStyle(document.activeElement as Element);
        return { outline: style.outlineStyle !== 'none' && style.outlineWidth !== '0px', ring: style.boxShadow !== 'none' };
      });
      expect(indicator.outline || indicator.ring, `${testId} shows a visible focus indicator`).toBe(true);
      return;
    }
  }
  throw new Error(`Tab never reached ${testId}`);
}

test.use({ timezoneId: 'Australia/Sydney', locale: 'en-AU' });

test.describe('Purchases list pagination (issue #1209)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of [390, 1440]) {
    test(`pager reached by Tab with visible focus and no page overflow at ${width}px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      const fileRequests = trackFileRequests(page);
      await answerApi(page);
      await page.goto('/purchases');
      await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
      await expect(page.getByRole('heading', { name: 'Purchases', level: 1 })).toBeVisible();
      await expect(page.getByTestId('list-pagination-range')).toHaveText(/Showing\s+1–10\s+of\s+25\s+purchases/);
      await expect(page.locator('tbody > tr')).toHaveCount(10);
      await expectNoHorizontalOverflow(page);

      // Every row with a document shows the plain attachment icon from metadata, never a
      // downloaded thumbnail image (issue #1234).
      await expect(page.locator('tbody img')).toHaveCount(0);
      expect(fileRequests).toEqual([]);

      // The table may scroll sideways inside its own card at narrow widths; the page itself must not.
      const tableScrolls = await page.evaluate(() => {
        const box = document.querySelector('table')?.parentElement;
        return box ? box.scrollWidth > box.clientWidth : false;
      });
      if (width === 390) {
        expect(tableScrolls).toBe(true);
      }

      await page.screenshot({ path: join(screenshotDirectory, `purchases-page-1-${width}px.png`), fullPage: true });

      await page.locator('body').focus();
      await tabTo(page, 'list-pagination-size');
      await tabTo(page, 'list-pagination-next');
      await page.screenshot({ path: join(screenshotDirectory, `purchases-next-focused-${width}px.png`) });
      await page.keyboard.press('Enter');
      await expect(page.getByTestId('list-pagination-range')).toHaveText(/Showing\s+11–20\s+of\s+25\s+purchases/);
      await expect(page.locator('tbody img')).toHaveCount(0);
      expect(fileRequests).toEqual([]);

      await page.keyboard.press('Shift+Tab');
      const previousFocused = await page.evaluate(() => (document.activeElement as HTMLElement | null)?.dataset['testid']);
      expect(previousFocused).toBe('list-pagination-previous');
      await expectNoHorizontalOverflow(page);
    });
  }
});
