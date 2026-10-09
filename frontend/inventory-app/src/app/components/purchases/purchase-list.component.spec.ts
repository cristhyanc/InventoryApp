import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { PurchaseListComponent } from './purchase-list.component';
import { PurchaseService } from '../../services/purchase.service';
import {
  GstClassification,
  GstClassificationSource,
  Purchase,
  PurchaseGstSummary,
  PurchaseItem,
  PurchaseValidation
} from '../../models/models';

function item(overrides: Partial<PurchaseItem> = {}): PurchaseItem {
  return {
    id: 1,
    receiptId: 7,
    productId: 10,
    quantity: 2,
    unitCost: 1.1,
    gstClassification: GstClassification.Unknown,
    gstClassificationSource: GstClassificationSource.Unknown,
    ...overrides
  };
}

function purchase(overrides: Partial<Purchase> = {}): Purchase {
  return {
    id: 7,
    title: 'Weekly restock',
    notes: null,
    totalAmount: 10,
    deliveryCost: null,
    deliveryGstClassification: GstClassification.Unknown,
    deliveryGstClassificationSource: GstClassificationSource.Unknown,
    packageCost: null,
    packageGstClassification: GstClassification.Unknown,
    packageGstClassificationSource: GstClassificationSource.Unknown,
    purchaseDate: '2026-04-01T00:00:00Z',
    supplierId: null,
    supplier: null,
    fileName: 'invoice.pdf',
    storedFileName: 'abc.pdf',
    contentType: 'application/pdf',
    fileSizeBytes: 1024,
    createdAt: '2026-04-01T00:00:00Z',
    items: [item()],
    ...overrides
  };
}

async function render(
  purchases: Purchase[],
  gst: PurchaseGstSummary | null = null,
  validation: PurchaseValidation | null = null
) {
  await TestBed.configureTestingModule({
    imports: [PurchaseListComponent],
    providers: [
      provideRouter([]),
      {
        provide: PurchaseService,
        useValue: {
          getAll: jest.fn(() => of(purchases)),
          delete: jest.fn(() => of(undefined)),
          getFile: jest.fn(() => of(new Blob())),
          getValidationFor: jest.fn(() => validation),
          getGstSummaryFor: jest.fn(() => gst)
        }
      }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(PurchaseListComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('PurchaseListComponent edit navigation (issue #475)', () => {
  it('links Edit to the dedicated edit page for that purchase instead of expanding an editor', async () => {
    const { host } = await render([purchase({ id: 7 }), purchase({ id: 9, title: 'Second' })]);

    const links = Array.from(host.querySelectorAll('[data-testid="purchase-edit-link"]'));
    expect(links.map((link) => link.getAttribute('href'))).toEqual(['/purchases/7/edit', '/purchases/9/edit']);
  });

  it('expands no editor in the table at all: no edit form, no edit row, no edit controls', async () => {
    const { host } = await render([purchase({ deliveryCost: 5, packageCost: 2 })]);

    expect(host.querySelectorAll('form')).toHaveLength(0);
    expect(host.querySelectorAll('[data-testid="edit-purchase-item"]')).toHaveLength(0);
    expect(host.querySelector('[data-testid="edit-delivery-gst"]')).toBeNull();
    expect(host.querySelector('[data-testid="edit-package-gst"]')).toBeNull();
    expect(host.querySelector('[data-testid="edit-purchase-error"]')).toBeNull();
    expect(host.querySelectorAll('tbody > tr')).toHaveLength(1);
  });

  it('gives the Edit action an accessible name naming the purchase it opens', async () => {
    const { host } = await render([purchase({ id: 7, title: 'Weekly restock' })]);

    expect(host.querySelector('[data-testid="purchase-edit-link"]')?.getAttribute('aria-label')).toBe(
      'Edit Weekly restock'
    );
  });
});

describe('PurchaseListComponent GST display (issue #431)', () => {
  it("shows the API's input GST, every component's classification and the unresolved state together", async () => {
    const mixed = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      packageCost: 2,
      packageGstClassification: GstClassification.Unknown,
      items: [
        item({ id: 11, productId: 10, gstClassification: GstClassification.Taxable }),
        item({ id: 12, productId: 20, gstClassification: GstClassification.GstFree })
      ]
    });
    const { host } = await render([mixed], { inputGst: 0.65, unresolvedComponentCount: 1, unresolvedAmount: 2 });

    const summary = host.querySelector('[data-testid="purchase-gst-summary"]');
    expect(summary?.textContent).toContain('$0.65');
    expect(summary?.textContent).toContain('Delivery: Taxable');
    expect(summary?.textContent).toContain('Package: Not classified');
    expect(host.querySelector('[data-testid="purchase-gst-unresolved"]')?.textContent).toContain('$2.00');

    const lines = host.querySelectorAll('[data-testid="purchase-item-line"]');
    expect(lines[0].textContent).toContain('Taxable');
    expect(lines[1].textContent).toContain('GST-free');
  });

  it('names no classification for a charge that was never entered, and reports nothing unresolved for it', async () => {
    const complete = purchase({
      deliveryCost: null,
      packageCost: 0,
      items: [item({ id: 11, gstClassification: GstClassification.GstFree })]
    });
    const { host } = await render([complete], { inputGst: 0, unresolvedComponentCount: 0, unresolvedAmount: 0 });

    const summary = host.querySelector('[data-testid="purchase-gst-summary"]');
    expect(summary?.textContent).not.toContain('Delivery:');
    expect(summary?.textContent).not.toContain('Package:');
    expect(host.querySelector('[data-testid="purchase-gst-unresolved"]')).toBeNull();
  });
});

describe('PurchaseListComponent consolidated row layout (issue #474)', () => {
  it('renders one display row per purchase even when notes, GST and a total mismatch warning are all present', async () => {
    const withEverything = purchase({
      notes: 'Call supplier before reordering - price went up a lot this time.',
      totalAmount: 20
    });
    const { host } = await render(
      [withEverything],
      { inputGst: 0.65, unresolvedComponentCount: 0, unresolvedAmount: 0 },
      { hasTotalMismatch: true, calculatedTotal: 18, totalDifference: 2 }
    );

    const displayRows = host.querySelectorAll('tbody > tr.table-row');
    expect(displayRows).toHaveLength(1);

    const row = displayRows[0];
    expect(row.textContent).toContain('Call supplier before reordering');
    expect(row.textContent).toContain('$0.65');
    expect(row.textContent).toContain('Does not match calculated total');
    expect(row.querySelector('[data-testid="purchase-gst-summary"]')).not.toBeNull();
  });

  it('renders one plain display row for a purchase with no notes, GST summary or mismatch', async () => {
    const { host } = await render([purchase({ notes: null })]);

    expect(host.querySelectorAll('tbody > tr.table-row')).toHaveLength(1);
  });
});

describe('PurchaseListComponent Actions column layout (issue #449)', () => {
  const wide = () =>
    purchase({
      items: [item({ id: 11, productId: 10 }), item({ id: 12, productId: 20 }), item({ id: 13, productId: 10 })]
    });

  it('keeps the Actions header pinned to the right edge of the horizontally scrollable table', async () => {
    const { host } = await render([wide()]);

    const header = host.querySelector('[data-testid="purchases-actions-header"]');
    expect(header).not.toBeNull();
    expect(header?.classList.contains('sticky')).toBe(true);
    expect(header?.classList.contains('right-0')).toBe(true);
  });

  it('keeps both Edit and Delete reachable in a sticky Actions cell, independent of how wide the Items column grows', async () => {
    const { host } = await render([wide()]);

    const actionsCell = host.querySelector('[data-testid="purchases-actions-cell"]');
    expect(actionsCell).not.toBeNull();
    expect(actionsCell?.classList.contains('sticky')).toBe(true);
    expect(actionsCell?.classList.contains('right-0')).toBe(true);

    expect(actionsCell?.querySelector('[data-testid="purchase-edit-link"]')?.textContent).toContain('Edit');
    const deleteButton = Array.from(actionsCell?.querySelectorAll('button') ?? []).find((b) =>
      b.textContent?.includes('Delete')
    );
    expect(deleteButton).toBeDefined();
  });

  it('leaves remove wired to the Delete button unchanged', async () => {
    const stored = purchase({ items: [item({ id: 11, productId: 10 })] });
    const { fixture, host } = await render([stored]);

    const removeSpy = jest.spyOn(fixture.componentInstance, 'remove').mockImplementation(() => undefined);
    const actionsCell = host.querySelector('[data-testid="purchases-actions-cell"]');
    const deleteButton = Array.from(actionsCell?.querySelectorAll('button') ?? []).find((b) =>
      b.textContent?.includes('Delete')
    ) as HTMLButtonElement;

    deleteButton.click();

    expect(removeSpy).toHaveBeenCalledWith(stored);
  });
});
