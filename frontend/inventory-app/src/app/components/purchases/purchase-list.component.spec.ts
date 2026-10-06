import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { PurchaseListComponent } from './purchase-list.component';
import { PurchaseService, PurchaseItemPayload, PurchaseUpdatePayload } from '../../services/purchase.service';
import { SupplierService } from '../../services/supplier.service';
import { ProductService } from '../../services/product.service';
import {
  GstClassification,
  GstClassificationSource,
  Product,
  Purchase,
  PurchaseGstSummary,
  PurchaseItem
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

function product(id: number, name: string): Product {
  return {
    id,
    name,
    unitPrice: 2,
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 10,
    maxStockInMachine: 10,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 2,
    restockTo: 10,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true,
    isLowStock: false,
    isReorderAlert: false
  };
}

function createHarness(
  purchases: Purchase[],
  gst: PurchaseGstSummary | null = null,
  update: jest.Mock = jest.fn(() => of(purchases[0]))
) {
  const purchaseService = {
    getAll: jest.fn(() => of(purchases)),
    update,
    delete: jest.fn(() => of(undefined)),
    getFile: jest.fn(() => of(new Blob())),
    getValidationFor: jest.fn(() => null),
    getGstSummaryFor: jest.fn(() => gst)
  } as unknown as PurchaseService;
  const supplierService = { getAll: jest.fn(() => of([])) } as unknown as SupplierService;
  const productService = {
    getAll: jest.fn(() => of([product(10, 'Coke'), product(20, 'Chips')]))
  } as unknown as ProductService;

  const component = new PurchaseListComponent(purchaseService, supplierService, productService);
  component.ngOnInit();

  return { component, update };
}

/** The payload the component handed to `PurchaseService.update`. */
function savedPayload(update: jest.Mock): PurchaseUpdatePayload {
  expect(update).toHaveBeenCalledTimes(1);
  return update.mock.calls[0][1] as PurchaseUpdatePayload;
}

/** The line payloads as the server will actually read them, after JSON drops omitted fields. */
function savedItems(update: jest.Mock): PurchaseItemPayload[] {
  return JSON.parse(JSON.stringify(savedPayload(update).items ?? [])) as PurchaseItemPayload[];
}

describe('PurchaseListComponent GST edit mapping (issue #431)', () => {
  it('shows each line\'s and each charge\'s stored classification when the edit starts', () => {
    const stored = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      deliveryGstClassificationSource: GstClassificationSource.SupplierFeeDefault,
      packageCost: 2,
      packageGstClassification: GstClassification.GstFree,
      items: [item({ id: 11, gstClassification: GstClassification.Taxable })]
    });
    const { component } = createHarness([stored]);

    component.startEdit(stored);

    expect(component.editForm.deliveryGstClassification).toBe(GstClassification.Taxable);
    expect(component.editForm.packageGstClassification).toBe(GstClassification.GstFree);
    expect(component.editItems[0].gstClassification).toBe(GstClassification.Taxable);
    expect(component.editItems[0].id).toBe(11);
  });

  it('omits every classification the person did not change, so rule-derived provenance survives an unrelated edit', () => {
    const stored = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      deliveryGstClassificationSource: GstClassificationSource.SupplierFeeDefault,
      packageCost: 2,
      packageGstClassification: GstClassification.GstFree,
      packageGstClassificationSource: GstClassificationSource.SupplierFeeDefault,
      items: [item({ id: 11, gstClassification: GstClassification.Taxable })]
    });
    const { component, update } = createHarness([stored]);

    component.startEdit(stored);
    component.editItems[0].quantity = 7;
    component.saveEdit(stored);

    const payload = savedPayload(update);
    expect(payload.deliveryGstClassification).toBeUndefined();
    expect(payload.packageGstClassification).toBeUndefined();
    expect(savedItems(update)).toEqual([{ id: 11, productId: 10, quantity: 7, unitCost: 1.1 }]);
  });

  it('sends only the classifications the person changed', () => {
    const stored = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Unknown,
      packageCost: 2,
      packageGstClassification: GstClassification.GstFree,
      items: [
        item({ id: 11, productId: 10, gstClassification: GstClassification.Unknown }),
        item({ id: 12, productId: 20, gstClassification: GstClassification.Taxable })
      ]
    });
    const { component, update } = createHarness([stored]);

    component.startEdit(stored);
    component.editForm.deliveryGstClassification = GstClassification.Taxable;
    component.editItems[1].gstClassification = GstClassification.GstFree;
    component.saveEdit(stored);

    const payload = savedPayload(update);
    expect(payload.deliveryGstClassification).toBe(GstClassification.Taxable);
    expect(payload.packageGstClassification).toBeUndefined();
    expect(savedItems(update)).toEqual([
      { id: 11, productId: 10, quantity: 2, unitCost: 1.1 },
      { id: 12, productId: 20, quantity: 2, unitCost: 1.1, gstClassification: GstClassification.GstFree }
    ]);
  });

  it('submits an explicit return to Not classified rather than omitting it', () => {
    const stored = purchase({ items: [item({ id: 11, gstClassification: GstClassification.Taxable })] });
    const { component, update } = createHarness([stored]);

    component.startEdit(stored);
    component.editItems[0].gstClassification = GstClassification.Unknown;
    component.saveEdit(stored);

    expect(savedItems(update)).toEqual([
      { id: 11, productId: 10, quantity: 2, unitCost: 1.1, gstClassification: GstClassification.Unknown }
    ]);
  });

  it('keeps each line id when duplicate-product lines are reordered, and never substitutes the product or the position', () => {
    const stored = purchase({
      items: [
        item({ id: 11, productId: 10, gstClassification: GstClassification.Taxable }),
        item({ id: 12, productId: 10, gstClassification: GstClassification.GstFree })
      ]
    });
    const { component, update } = createHarness([stored]);

    component.startEdit(stored);
    component.editItems.reverse();
    component.saveEdit(stored);

    expect(savedItems(update)).toEqual([
      { id: 12, productId: 10, quantity: 2, unitCost: 1.1 },
      { id: 11, productId: 10, quantity: 2, unitCost: 1.1 }
    ]);
  });

  it('keeps the surviving line\'s own id when one of two duplicate-product lines is removed', () => {
    const stored = purchase({
      items: [
        item({ id: 11, productId: 10, gstClassification: GstClassification.Taxable }),
        item({ id: 12, productId: 10, gstClassification: GstClassification.GstFree })
      ]
    });
    const { component, update } = createHarness([stored]);

    component.startEdit(stored);
    component.removeEditItem(0);
    component.saveEdit(stored);

    expect(savedItems(update)).toEqual([{ id: 12, productId: 10, quantity: 2, unitCost: 1.1 }]);
  });

  it('omits the id of a line added during the edit and leaves it unclassified by default', () => {
    const stored = purchase({ items: [item({ id: 11 })] });
    const { component, update } = createHarness([stored]);

    component.startEdit(stored);
    component.addEditItem();
    component.saveEdit(stored);

    const items = savedItems(update);
    expect(items[1].id).toBeUndefined();
    expect(items[1].gstClassification).toBeUndefined();
    expect(component.editItems[1].gstClassification).toBe(GstClassification.Unknown);
  });

  it('sends a new line\'s classification once the person picks one', () => {
    const stored = purchase({ items: [item({ id: 11 })] });
    const { component, update } = createHarness([stored]);

    component.startEdit(stored);
    component.addEditItem();
    component.editItems[1].gstClassification = GstClassification.Taxable;
    component.saveEdit(stored);

    expect(savedItems(update)[1].gstClassification).toBe(GstClassification.Taxable);
  });

  it('never submits a classification for a charge the person cleared', () => {
    const stored = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      packageCost: 2,
      packageGstClassification: GstClassification.Taxable
    });
    const { component, update } = createHarness([stored]);

    component.startEdit(stored);
    component.editForm.deliveryCost = null;
    component.editForm.packageCost = 0;
    component.saveEdit(stored);

    const payload = savedPayload(update);
    expect(payload.deliveryGstClassification).toBeUndefined();
    expect(payload.packageGstClassification).toBeUndefined();
  });

  it('hides a charge picker while the charge has no value', () => {
    const { component } = createHarness([purchase()]);

    expect(component.hasCharge(null)).toBe(false);
    expect(component.hasCharge(0)).toBe(false);
    expect(component.hasCharge(5)).toBe(true);
  });

  it('keeps the edit open and reports the API\'s message when the save is refused', () => {
    const stored = purchase({ items: [item({ id: 11 })] });
    const refused = jest.fn(() => throwError(() => ({ error: 'A GST classification must be one of Unknown, Taxable, GstFree.' })));
    const { component } = createHarness([stored], null, refused);
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);

    component.startEdit(stored);
    component.saveEdit(stored);
    logged.mockRestore();

    expect(component.editingPurchaseId).toBe(stored.id);
    expect(component.editError).toContain('A GST classification must be one of');
  });

  it('clears a previous refusal when the edit is reopened', () => {
    const stored = purchase({ items: [item({ id: 11 })] });
    const refused = jest.fn(() => throwError(() => ({ error: 'Rejected' })));
    const { component } = createHarness([stored], null, refused);
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);

    component.startEdit(stored);
    component.saveEdit(stored);
    component.cancelEdit();
    component.startEdit(stored);
    logged.mockRestore();

    expect(component.editError).toBe('');
  });
});

describe('PurchaseListComponent GST display (issue #431)', () => {
  const render = async (purchases: Purchase[], gst: PurchaseGstSummary | null) => {
    await TestBed.configureTestingModule({
      imports: [PurchaseListComponent],
      providers: [
        provideRouter([]),
        {
          provide: PurchaseService,
          useValue: {
            getAll: jest.fn(() => of(purchases)),
            update: jest.fn(),
            delete: jest.fn(),
            getFile: jest.fn(() => of(new Blob())),
            getValidationFor: jest.fn(() => null),
            getGstSummaryFor: jest.fn(() => gst)
          }
        },
        { provide: SupplierService, useValue: { getAll: jest.fn(() => of([])) } },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of([product(10, 'Coke'), product(20, 'Chips')])) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(PurchaseListComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  };

  afterEach(() => TestBed.resetTestingModule());

  it('shows the API\'s input GST, every component\'s classification and the unresolved state together', async () => {
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

  it('marks the saved figures as not covering unsaved edits while the edit form is open', async () => {
    const stored = purchase();
    const { fixture, host } = await render([stored], { inputGst: 0.2, unresolvedComponentCount: 0, unresolvedAmount: 0 });

    expect(host.querySelector('[data-testid="purchase-gst-stale"]')).toBeNull();

    fixture.componentInstance.startEdit(fixture.componentInstance.purchases[0]);
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="purchase-gst-stale"]')).not.toBeNull();
  });
});
