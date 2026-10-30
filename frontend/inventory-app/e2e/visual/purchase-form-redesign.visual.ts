import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence for issue #1029: the shared Add/Edit purchase layout at 360, 390, 768 and
 * 1440px - Add with no receipt and with one attached, the product search and a help tip open, the
 * validation shown after Save is pressed, and an expanded Edit with long product names.
 *
 * Like the other visual runs this starts no API and no database. Every `/api/` request is answered
 * in the browser with neutral synthetic data, so no business data, account name or credential can
 * appear in a capture, and nothing is ever saved. Each width also asserts the page has no
 * horizontal overflow. It runs once, in the `desktop-1440` project, and sets each width itself.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1029');
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
  product(2, 'Sample Energy Drink Sugarfree', '250mL'),
  product(3, 'Sample Cola', '375mL'),
  product(4, 'Sample Salt and Vinegar Crisps Family Size Multipack With An Unusually Long Name', '45g'),
  product(5, 'Sample Orange Juice', '250mL')
];

const suppliers = [
  { id: 1, name: 'Sample Wholesale' },
  { id: 2, name: 'Sample Cash and Carry With A Long Trading Name' }
];

const editedPurchase = {
  purchase: {
    id: 42,
    title: 'Sample Wholesale — 24 Oct 2026',
    notes: 'Weekly scheduled restock for snacks and drinks.',
    totalAmount: 303.31,
    deliveryCost: 15,
    deliveryGstClassification: 1,
    packageCost: 5,
    packageGstClassification: 1,
    purchaseDate: '2026-10-23T13:00:00Z',
    supplierId: 1,
    supplier: suppliers[0],
    fileName: 'sample-tax-invoice.pdf',
    storedFileName: 'stored.pdf',
    contentType: 'application/pdf',
    fileSizeBytes: 1468006,
    createdAt: '2026-10-24T01:00:00Z',
    items: [
      { id: 11, productId: 4, quantity: 80, unitCost: 0.91, gstClassification: 1 },
      { id: 12, productId: 3, quantity: 150, unitCost: 0.8167, gstClassification: 1 },
      { id: 13, productId: 5, quantity: 24, unitCost: 1.0417, gstClassification: 2 },
      { id: 14, productId: 1, quantity: 60, unitCost: 1.05, gstClassification: 0 }
    ]
  },
  validation: { hasTotalMismatch: false, calculatedItemSubtotal: 283.3, calculatedTotal: 303.3, totalDifference: null },
  gst: { inputGst: 21.65, unresolvedComponentCount: 1, unresolvedAmount: 63 }
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
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
    if (path.endsWith('/suppliers')) return json(route, suppliers);
    if (path.endsWith('/products')) return json(route, products);
    if (path.endsWith('/purchases/42')) return json(route, editedPurchase);
    return json(route, []);
  });
}

async function open(page: Page, width: number, url: string, heading: string): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  await answerApi(page);
  await page.goto(url);
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }'
  });
  await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

/** A capture of the whole page, with the viewport grown to it so the sticky bar sits at its foot. */
async function capture(page: Page, width: number, name: string): Promise<void> {
  await expectNoHorizontalOverflow(page);
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  await page.setViewportSize({ width, height });
  await page.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`) });
}

/** Fills one Add line through the real controls: a searched product, a quantity and a total. */
async function fillLine(page: Page, index: number, search: string, quantity: string, total: string): Promise<void> {
  await page.getByRole('button', { name: 'Add product' }).click();
  const box = page.getByTestId('purchase-item-product').nth(index);
  await box.fill(search);
  await page.getByTestId('purchase-product-option').first().click();
  await page.getByTestId('purchase-item-quantity').nth(index).fill(quantity);
  await page.getByTestId('purchase-item-line-total').nth(index).fill(total);
}

async function fillAdd(page: Page): Promise<void> {
  await page.locator('select[name="supplierId"]').selectOption({ label: 'Sample Wholesale' });
  await fillLine(page, 0, 'energy drink', '72', '108');
  await page.getByTestId('purchase-item-gst').nth(0).selectOption({ label: 'Taxable' });
  await fillLine(page, 1, 'cola', '84', '84');
  await fillLine(page, 2, 'juice', '40', '58');
  await page.getByTestId('purchase-item-gst').nth(2).selectOption({ label: 'GST-free' });
  await page.locator('#totalAmount').fill('250');
}

// App dates are Sydney dates, so the browser is put there, with an Australian locale.
test.use({ timezoneId: 'Australia/Sydney', locale: 'en-AU' });

test.describe('Add/Edit purchase redesign visual evidence (issue #1029)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const width of [360, 390, 768, 1440]) {
    test(`Add purchase, empty, at ${width}px`, async ({ page }) => {
      await open(page, width, '/purchases/new', 'Add Purchase');
      await expect(page.locator('#title')).toHaveValue(/^Purchase — /);
      await capture(page, width, 'add-empty');
    });

    test(`Add purchase, filled with a receipt attached, at ${width}px`, async ({ page }) => {
      await open(page, width, '/purchases/new', 'Add Purchase');
      await fillAdd(page);
      await page.getByTestId('purchase-receipt-input').setInputFiles({
        name: 'sample-receipt.pdf',
        mimeType: 'application/pdf',
        buffer: Buffer.from('%PDF-1.4 synthetic')
      });
      await expect(page.getByTestId('purchase-receipt-name')).toHaveText('sample-receipt.pdf');
      await expect(page.locator('#title')).toHaveValue(/^Sample Wholesale — /);
      await expect(page.getByTestId('purchase-receipt-comparison')).toHaveText(/Matches receipt/);
      await capture(page, width, 'add-filled');
    });

    test(`Add purchase validation after Save at ${width}px`, async ({ page }) => {
      await open(page, width, '/purchases/new', 'Add Purchase');
      await page.getByRole('button', { name: 'Add product' }).click();
      await expect(page.locator('.field-error')).toHaveCount(0);
      await page.getByRole('button', { name: 'Save purchase' }).click();
      await expect(page.getByText('Choose a product.', { exact: true })).toBeVisible();
      await capture(page, width, 'add-validation');
    });

    test(`Edit purchase, expanded, at ${width}px`, async ({ page }) => {
      await open(page, width, '/purchases/42/edit', 'Edit Purchase');
      await expect(page.locator('#editDeliveryCost')).toHaveValue('15');
      await expect(page.locator('#editNotes')).toHaveValue(/Weekly scheduled restock/);
      await expect(page.getByTestId('edit-purchase-item-cost').nth(1)).toHaveValue('0.8167');
      await capture(page, width, 'edit-expanded');
    });
  }

  for (const width of [390, 1440]) {
    test(`product search and a help tip open at ${width}px`, async ({ page }) => {
      await open(page, width, '/purchases/new', 'Add Purchase');
      await page.getByRole('button', { name: 'Add product' }).click();
      await page.getByTestId('purchase-item-product').fill('sample');
      await expect(page.getByTestId('purchase-product-option')).toHaveCount(5);
      await page.screenshot({ path: join(screenshotDirectory, `add-product-search-${width}px.png`) });

      await page.keyboard.press('Escape');
      const help = page.getByRole('button', { name: 'About receipt total' });
      await help.scrollIntoViewIfNeeded();
      await help.focus();
      await page.keyboard.press('Enter');
      await expect(page.getByTestId('help-toggletip-panel')).toBeVisible();
      await expect(page.getByTestId('purchase-product-option')).toHaveCount(0);
      await page.screenshot({ path: join(screenshotDirectory, `add-help-tip-${width}px.png`) });

      await page.keyboard.press('Escape');
      await expect(page.getByTestId('help-toggletip-panel')).toHaveCount(0);
      await expect(help).toBeFocused();
    });
  }

  test('the sticky action bar never covers a focused field at 390px', async ({ page }) => {
    await open(page, 390, '/purchases/new', 'Add Purchase');
    await page.setViewportSize({ width: 390, height: 640 });
    await page.getByRole('button', { name: 'Add product' }).click();
    const quantity = page.getByTestId('purchase-item-quantity').first();
    await quantity.focus();
    const field = await quantity.boundingBox();
    const bar = await page.getByRole('button', { name: 'Save purchase' }).locator('xpath=..').boundingBox();
    expect(field && bar).toBeTruthy();
    expect(field!.y + field!.height).toBeLessThanOrEqual(bar!.y);
  });
});
