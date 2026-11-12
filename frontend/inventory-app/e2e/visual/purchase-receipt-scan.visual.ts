import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual and layout evidence for issue #1166: Scan receipt on Add Purchase at 1440px and 390px -
 * the button before anything is scanned, the filled form with the receipt beside the lines, a line
 * whose pack size the receipt did not print, a line that is not stock shown as "Not stock, skip",
 * the unresolved container levy in its own row, and the plan lock a Starter business sees.
 *
 * Like the other visual runs it starts no API and no database: every `/api/` request is answered in
 * the browser with neutral synthetic data, so no business data, supplier, price, account name or
 * credential can appear in a capture.
 *
 * **It writes nothing and scans nothing.** The one non-`GET` it answers is `POST /api/purchases/scan`,
 * which stores nothing by design and is answered here with a fixed synthetic draft, so no receipt
 * leaves the browser, no outside reader is called and nothing is charged. Every other non-`GET` -
 * the purchase create above all - is answered with a refusal, so a run that ever drifted into
 * saving would fail rather than quietly photograph a save.
 *
 * The receipt in the capture is **drawn by this run and photographed**, so the picture beside the
 * lines is a real image of a plausible docket while no real receipt enters the repository.
 *
 * Each capture also asserts what jsdom cannot, because jsdom computes no layout: that the page does
 * not scroll sideways at 1440px, 390px or 360px, and that the document preview sits beside the lines
 * at 1440px and above them at 390px.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1166');
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

function product(id: number, name: string, unit: string | null) {
  return {
    id,
    name,
    unit,
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    quantityInStock: 40,
    maxStockInMachine: 10,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 40,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true,
    isLowStock: false,
    isReorderAlert: false
  };
}

const products = [
  product(1, 'Sample Energy Drink', '250mL'),
  product(3, 'Sample Cola', '375mL'),
  product(4, 'Sample Salt and Vinegar Crisps', '45g')
];

const suppliers = [
  { id: 1, name: 'Sample Wholesale' },
  { id: 2, name: 'Sample Cash and Carry' }
];

function read<T>(value: T) {
  return { value, provenance: 'read' };
}

function suggested<T>(value: T) {
  return { value, provenance: 'suggested' };
}

const gstHint = { priceIncludesGst: 'yes', taxable: 'yes', marker: '*' };

/**
 * The synthetic draft `POST /api/purchases/scan` is answered with. Its figures add up on purpose:
 * $60.00 + $45.00 + $73.60 of stock, $9.50 delivery, $8.50 of office paper this purchase skips and
 * a $1.10 levy no product carries make the $197.70 the receipt total says.
 */
const scanDraft = {
  supplier: read('Sample Wholesale'),
  supplierId: suggested(1),
  purchaseDate: read('2026-11-11'),
  invoiceNumber: read('SW-20261111'),
  receiptTotal: read(197.7),
  gstTotal: read(17.97),
  deliveryCost: read(9.5),
  packageCost: null,
  lines: [
    {
      kind: 'stock',
      text: read('SAMPLE COLA 375ML 24PK'),
      packs: read(2),
      unitsPerPack: read(24),
      units: suggested(48),
      lineTotal: read(60),
      unitCost: suggested(1.25),
      productId: suggested(3),
      gst: gstHint,
      levyUnresolved: false,
      missing: []
    },
    {
      // The pack size came from the business's own purchase history rather than the docket, which is
      // what issue #1165 suggests and what the line marks as "from your last purchase".
      kind: 'stock',
      text: read('SAMPLE ENERGY DRINK 250ML'),
      packs: read(1),
      unitsPerPack: suggested(12),
      units: suggested(12),
      lineTotal: read(45),
      unitCost: suggested(3.75),
      productId: suggested(1),
      gst: gstHint,
      levyUnresolved: false,
      missing: []
    },
    {
      // No pack size printed and none suggested: the units are unknown, never 1, so the line is
      // highlighted and Save is blocked until somebody enters them.
      kind: 'stock',
      text: read('SAMPLE CRISPS CARTON'),
      packs: read(1),
      unitsPerPack: null,
      units: null,
      lineTotal: read(73.6),
      unitCost: null,
      productId: suggested(4),
      gst: gstHint,
      levyUnresolved: false,
      missing: ['unitsPerPack']
    },
    {
      kind: 'other',
      text: read('A4 COPY PAPER REAM'),
      packs: read(1),
      unitsPerPack: read(1),
      units: suggested(1),
      lineTotal: read(8.5),
      unitCost: suggested(8.5),
      productId: null,
      gst: gstHint,
      levyUnresolved: false,
      missing: []
    },
    {
      kind: 'levy',
      text: read('CONTAINER DEPOSIT'),
      packs: null,
      unitsPerPack: null,
      units: null,
      lineTotal: read(1.1),
      unitCost: null,
      productId: null,
      gst: { priceIncludesGst: 'unknown', taxable: 'no', marker: null },
      levyUnresolved: true,
      missing: []
    }
  ],
  usage: { month: '2026-11', used: 7, remaining: 93, limit: 100 }
};

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

type Plan = 'Pro' | 'Starter';

async function answerApi(page: Page, plan: Plan, holdScan = false): Promise<void> {
  await page.route('**/api/**', (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;

    if (request.method() !== 'GET') {
      // The scan stores nothing; everything else is refused rather than photographed as a save.
      if (path.endsWith('/purchases/scan') && holdScan) {
        // Never answered, so the page stays on the reading progress for the photograph.
        return new Promise<void>(() => undefined);
      }
      return path.endsWith('/purchases/scan')
        ? json(route, scanDraft)
        : json(route, { title: 'This visual run makes no writes.' }, 500);
    }

    if (path.endsWith('/me/access')) {
      return json(route, {
        role: 'Owner',
        capabilities: OWNER_CAPABILITIES,
        businessName: 'Sample Vending',
        timeZoneId: 'Australia/Sydney',
        entitlement: { enforced: true, plan, source: 'BillingSubscription', endsAtUtc: null }
      });
    }
    if (path.endsWith('/me/account-state')) return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: 'Ready', machineChecksPending: false });
    if (path.endsWith('/suppliers')) return json(route, suppliers);
    if (path.endsWith('/products')) return json(route, products);
    return json(route, []);
  });
}

async function open(page: Page, width: number, plan: Plan = 'Pro', holdScan = false): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  await answerApi(page, plan, holdScan);
  await page.goto('/purchases/new');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }'
  });
  await expect(page.getByRole('heading', { name: 'Add Purchase', level: 1 })).toBeVisible();
  await expect(page.locator('[data-testid="purchase-receipt-scan"]')).toBeVisible();
}

/**
 * A plausible docket, drawn here and photographed, so the preview beside the lines shows a real
 * image while no real receipt, supplier or price is committed to this repository.
 */
async function drawnReceipt(page: Page): Promise<Buffer> {
  const scratch = await page.context().newPage();
  await scratch.setViewportSize({ width: 460, height: 660 });
  await scratch.setContent(`
    <body style="margin:0;background:#e5e7eb;font-family:'Courier New',monospace">
      <div style="margin:20px;padding:24px;background:#fff;color:#111">
        <h1 style="font-size:20px;letter-spacing:2px;text-align:center;margin:0 0 4px">SAMPLE WHOLESALE</h1>
        <p style="text-align:center;margin:0 0 16px;font-size:12px">TAX INVOICE SW-20261111 &middot; 11/11/2026</p>
        <hr/>
        <table style="width:100%;font-size:13px;border-collapse:collapse">
          <tr><td>SAMPLE COLA 375ML 24PK</td><td align="right">2 x 24</td><td align="right">60.00*</td></tr>
          <tr><td>SAMPLE ENERGY DRINK 250ML</td><td align="right">1</td><td align="right">45.00*</td></tr>
          <tr><td>SAMPLE CRISPS CARTON</td><td align="right">1</td><td align="right">73.60*</td></tr>
          <tr><td>A4 COPY PAPER REAM</td><td align="right">1</td><td align="right">8.50*</td></tr>
          <tr><td>CONTAINER DEPOSIT</td><td align="right">&nbsp;</td><td align="right">1.10</td></tr>
          <tr><td>DELIVERY</td><td align="right">&nbsp;</td><td align="right">9.50*</td></tr>
        </table>
        <hr/>
        <table style="width:100%;font-size:14px;font-weight:bold">
          <tr><td>TOTAL</td><td align="right">197.70</td></tr>
          <tr><td>GST INCLUDED</td><td align="right">17.97</td></tr>
        </table>
        <p style="font-size:11px;margin-top:16px">* INCLUDES GST &middot; SAMPLE DATA ONLY</p>
      </div>
    </body>
  `);
  const image = await scratch.locator('div').first().screenshot();
  await scratch.close();
  return image;
}

/** Scans the drawn receipt exactly as choosing a file does, and waits for the form to be filled. */
async function scan(page: Page): Promise<void> {
  await page.locator('[data-testid="purchase-receipt-scan-input"]').setInputFiles({
    name: 'sample-receipt.png',
    mimeType: 'image/png',
    buffer: await drawnReceipt(page)
  });
  await expect(page.locator('[data-testid="purchase-scanned-receipt"]')).toBeVisible();
  await expect(page.locator('[data-testid="purchase-item"]')).toHaveCount(4);
}

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

async function expectNoHorizontalOverflow(page: Page, width: number): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `no horizontal overflow at ${width}px`).toBeLessThanOrEqual(tolerance);
}

/** The preview is beside the lines from `xl` (1280px) up and above them below it. */
async function expectPreviewPlacement(page: Page, width: number): Promise<void> {
  const preview = (await page.locator('[data-testid="purchase-scanned-receipt"]').boundingBox())!;
  const firstLine = (await page.locator('[data-testid="purchase-item"]').first().boundingBox())!;

  if (width >= 1280) {
    expect(preview.x, `the preview sits beside the lines at ${width}px`).toBeGreaterThanOrEqual(
      firstLine.x + firstLine.width - tolerance
    );
  } else {
    expect(preview.y + preview.height, `the preview sits above the lines at ${width}px`).toBeLessThanOrEqual(
      firstLine.y + tolerance
    );
  }
}

/** A capture of the whole page, with the viewport grown to it so the sticky bar sits at its foot. */
async function capture(page: Page, width: number, name: string): Promise<void> {
  await expectNoHorizontalOverflow(page, width);
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  await page.setViewportSize({ width, height });
  await page.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`) });
  await page.setViewportSize({ width, height: 900 });
}

test.use({ timezoneId: 'Australia/Sydney', locale: 'en-AU' });

test.describe('Scan receipt on Add Purchase (issue #1166)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of [1440, 390]) {
    test(`the Scan receipt button before anything is scanned, at ${width}px`, async ({ page }) => {
      await open(page, width);

      await expect(page.locator('[data-testid="purchase-receipt-scan-start"]')).toHaveText(/Scan receipt/);
      await expect(page.locator('[data-testid="purchase-scanned-receipt"]')).toHaveCount(0);
      await capture(page, width, 'add-purchase-scan-ready');
    });

    test(`the estimated progress bar while the receipt is read, at ${width}px`, async ({ page }) => {
      await open(page, width, 'Pro', true);
      await page.locator('[data-testid="purchase-receipt-scan-input"]').setInputFiles({
        name: 'sample-receipt.png',
        mimeType: 'image/png',
        buffer: await drawnReceipt(page)
      });

      const bar = page.locator('[data-testid="purchase-receipt-scan-progress-bar"]');
      await expect(bar).toBeVisible();
      await expect(page.locator('[data-testid="purchase-receipt-scan-cancel"]')).toBeVisible();
      await expect
        .poll(() => bar.evaluate((element) => (element as HTMLProgressElement).value), { timeout: 15_000 })
        .toBeGreaterThan(30);
      await capture(page, width, 'add-purchase-scan-progress');
    });

    test(`the filled form, with the receipt beside the lines, at ${width}px`, async ({ page }) => {
      await open(page, width);
      await scan(page);

      // The purchase-level fields the draft read.
      await expect(page.locator('#invoiceNumber')).toHaveValue('SW-20261111');
      await expect(page.locator('#purchaseDate')).toHaveValue('2026-11-11');
      await expect(page.locator('#totalAmount')).toHaveValue('197.7');

      // The pack maths, the suggested pack size, the skipped line and the levy row.
      await expect(page.locator('[data-testid="purchase-item-scan-pack-maths"]').first()).toHaveText('2 x 24-pack = 48');
      await expect(page.locator('[data-testid="purchase-item-scan-pack-source"]')).toContainText('from your last purchase');
      await expect(page.locator('[data-testid="purchase-item-scan-missing"]')).toContainText('pack size is not printed');
      await expect(page.locator('[data-testid="purchase-item-skip"]')).toBeChecked();
      const levy = page.locator('[data-testid="purchase-scan-levy"]');
      await expect(levy).toContainText('Container levy not added to any product: check the receipt');
      await expect(levy.locator('input')).toHaveCount(0);
      await expect(page.locator('[data-testid="purchase-scan-levy-amount"]')).toContainText('1.10');

      await expectPreviewPlacement(page, width);
      await capture(page, width, 'add-purchase-scanned');
    });
  }

  test('the receipt reconciles once the missing pack size is entered, at 1440px', async ({ page }) => {
    await open(page, 1440);
    await scan(page);

    const quantities = page.locator('[data-testid="purchase-item-quantity"]');
    await quantities.nth(2).fill('80');
    await quantities.nth(2).blur();

    // $60.00 + $45.00 + $73.60 of stock, $9.50 delivery, and the $8.50 skipped item and $1.10 levy
    // the check counts because the receipt printed them: exactly the $197.70 total.
    await expect(page.locator('[data-testid="purchase-summary-total"]')).toContainText('188.10');
    await expect(page.locator('[data-testid="purchase-receipt-outside-lines"]')).toContainText('9.60');
    await expect(page.locator('[data-testid="purchase-receipt-comparison"]')).toContainText('Matches receipt');
    await expect(page.locator('[data-testid="purchase-item-scan-missing"]')).toHaveCount(0);

    await capture(page, 1440, 'add-purchase-scanned-complete');
  });

  test('a Starter business sees the button plan-locked, at 1440px', async ({ page }) => {
    await open(page, 1440, 'Starter');

    await expect(page.locator('[data-testid="purchase-receipt-scan-plan-lock"]')).toContainText(
      'Scanning a receipt needs the Pro plan.'
    );
    await expect(page.locator('[data-testid="purchase-receipt-scan-start"]')).toHaveCount(0);
    await capture(page, 1440, 'add-purchase-scan-plan-locked');
  });

  // 360px is a measurement rather than a capture: the 390px screenshot is the phone-width evidence.
  test('the scanned form does not scroll sideways at 360px', async ({ page }) => {
    await open(page, 360);
    await scan(page);

    await expectNoHorizontalOverflow(page, 360);
    await expectPreviewPlacement(page, 360);
  });
});
