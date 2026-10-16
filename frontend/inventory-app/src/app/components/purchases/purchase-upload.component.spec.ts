import { ComponentFixture, TestBed } from '@angular/core/testing';
import { convertToParamMap, ActivatedRoute, provideRouter, Router } from '@angular/router';
import { Observable, of } from 'rxjs';
import { PurchaseUploadComponent } from './purchase-upload.component';
import { PurchaseService } from '../../services/purchase.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { PurchaseItemPayload, PurchaseUploadPayload } from '../../services/purchase.service';
import { GstClassification, Purchase, SupplierOrder } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';

function supplierOrder(): SupplierOrder {
  return {
    id: 9,
    supplierId: 1,
    supplier: { id: 1, name: 'Acme Supplies' },
    orderDate: '2026-01-01T00:00:00Z',
    expectedDate: null,
    reference: 'PO-100',
    notes: null,
    status: 0,
    lines: [
      {
        id: 1,
        productId: 5,
        quantityOrdered: 10,
        quantityReceived: 0,
        outstandingQuantity: 10,
        unitPrice: 2
      }
    ]
  };
}

function createHarness(supplierOrderId: string | null, sessionActive: Observable<boolean> = of(false)) {
  const route = {
    queryParamMap: of(convertToParamMap(supplierOrderId ? { supplierOrderId } : {}))
  } as unknown as ActivatedRoute;
  const navigate = jest.fn();
  const upload = jest.fn(() => of({} as Purchase));
  const purchaseService = { upload } as unknown as PurchaseService;
  const supplierService = { getAll: jest.fn(() => of([])) } as unknown as SupplierService;
  const supplierOrderService = { getById: jest.fn(() => of(supplierOrder())) } as unknown as SupplierOrderService;
  const productService = { getAll: jest.fn(() => of([])) } as unknown as ProductService;
  const toastService = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } as unknown as ToastService;
  const supportSession = { active$: sessionActive } as unknown as SupportSessionService;

  const component = new PurchaseUploadComponent(
    purchaseService,
    supplierService,
    supplierOrderService,
    productService,
    route,
    { navigate } as unknown as Router,
    toastService,
    supportSession
  );
  component.ngOnInit();

  return { component, navigate, purchaseService, toastService, upload };
}

/** The payload the component handed to `PurchaseService.upload`. */
function uploadedPayload(upload: jest.Mock): PurchaseUploadPayload {
  expect(upload).toHaveBeenCalledTimes(1);
  return upload.mock.calls[0][0] as PurchaseUploadPayload;
}

/** The line payloads as the server will actually read them, after JSON drops omitted fields. */
function uploadedItems(upload: jest.Mock): PurchaseItemPayload[] {
  return JSON.parse(JSON.stringify(uploadedPayload(upload).items ?? [])) as PurchaseItemPayload[];
}

/**
 * A ready-to-submit manual purchase form with one line: 3 units for the $6.00 paid for the line,
 * which is the $2.00 unit cost the request carries. The line is filled through the component's own
 * change handlers, the way the template does it (issue #707).
 */
function readyToUpload(supplierOrderId: string | null = null, sessionActive: Observable<boolean> = of(false)) {
  const harness = createHarness(supplierOrderId, sessionActive);
  harness.component.selectedFile = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
  harness.component.form.title = 'Weekly restock';
  harness.component.addItem();
  const item = harness.component.items[0];
  item.productId = 5;
  harness.component.onLineQuantityChange(item, 3);
  harness.component.onLineAmountChange(item, 6);
  return harness;
}

describe('PurchaseUploadComponent supplier-order receipt navigation (issue #387)', () => {
  it('returns to the Supplier Orders page after a receipt that started from a supplier order', () => {
    const { component, navigate } = createHarness('9');
    component.selectedFile = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
    component.form.title = 'Weekly restock';

    component.upload();

    expect(navigate).toHaveBeenCalledWith(['/purchases/orders']);
  });

  it('goes to the plain purchase list for a manually created purchase', () => {
    const { component, navigate } = createHarness(null);
    component.selectedFile = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
    component.form.title = 'Weekly restock';

    component.upload();

    expect(navigate).toHaveBeenCalledWith(['/purchases']);
  });
});

describe('PurchaseUploadComponent GST classification mapping (issue #431)', () => {
  it('starts a new line and both charges as Not classified', () => {
    const { component } = readyToUpload();

    expect(component.items[0].gstClassification).toBe(GstClassification.Unknown);
    expect(component.form.deliveryGstClassification).toBe(GstClassification.Unknown);
    expect(component.form.packageGstClassification).toBe(GstClassification.Unknown);
  });

  it('submits no classification for an unclassified new purchase, so nothing is recorded as a person\'s choice', () => {
    const { component, upload } = readyToUpload();

    component.upload();

    const payload = uploadedPayload(upload);
    expect(payload.deliveryGstClassification).toBeUndefined();
    expect(payload.packageGstClassification).toBeUndefined();
    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 3, unitCost: 2 }]);
  });

  it('submits each classification the person picked', () => {
    const { component, upload } = readyToUpload();
    component.items[0].gstClassification = GstClassification.Taxable;
    component.form.deliveryCost = 5;
    component.form.deliveryGstClassification = GstClassification.Taxable;
    component.form.packageCost = 2;
    component.form.packageGstClassification = GstClassification.GstFree;

    component.upload();

    const payload = uploadedPayload(upload);
    expect(payload.deliveryGstClassification).toBe(GstClassification.Taxable);
    expect(payload.packageGstClassification).toBe(GstClassification.GstFree);
    expect(uploadedItems(upload)[0].gstClassification).toBe(GstClassification.Taxable);
  });

  it('never submits a classification for a charge with no value', () => {
    const { component, upload } = readyToUpload();
    component.form.deliveryCost = null;
    component.form.deliveryGstClassification = GstClassification.Taxable;
    component.form.packageCost = 0;
    component.form.packageGstClassification = GstClassification.Taxable;

    component.upload();

    const payload = uploadedPayload(upload);
    expect(payload.deliveryGstClassification).toBeUndefined();
    expect(payload.packageGstClassification).toBeUndefined();
  });

  it('hides a charge picker until the charge has a value', () => {
    const { component } = readyToUpload();

    expect(component.hasCharge(null)).toBe(false);
    expect(component.hasCharge(0)).toBe(false);
    expect(component.hasCharge(5)).toBe(true);
  });

  it('leaves lines prefilled from a supplier order unclassified rather than inferring a classification', () => {
    const { component } = createHarness('9');

    expect(component.items).toHaveLength(1);
    expect(component.items[0].gstClassification).toBe(GstClassification.Unknown);
  });
});

describe('PurchaseUploadComponent per-line price entry (issue #707)', () => {
  it('starts every new line on the total paid for the line, with nothing entered yet', () => {
    const { component } = createHarness(null);

    component.addItem();

    expect(component.items[0].priceEntryMode).toBe('total');
    expect(component.items[0].lineTotal).toBeNull();
    expect(component.items[0].unitCost).toBeNull();
  });

  it('derives the unit cost of a box price and saves the line at exactly the entered total', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineQuantityChange(item, 20);
    component.onLineAmountChange(item, 32.5);

    expect(item.unitCost).toBe(1.625);
    expect(component.savedLineAmount(item)).toBe(32.5);

    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 20, unitCost: 1.625 }]);
  });

  it('derives the line total from an entered unit price', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineQuantityChange(item, 24);
    component.onLinePriceEntryModeChange(item, 'unit');
    component.onLineAmountChange(item, 1.6625);

    expect(item.lineTotal).toBe(39.9);
    expect(component.derivedLineTotal(item)).toBe(39.9);

    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 24, unitCost: 1.6625 }]);
  });

  it('submits the unrounded quotient when a line total does not divide evenly', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineQuantityChange(item, 300);
    component.onLineAmountChange(item, 100);
    component.upload();

    const submitted = uploadedItems(upload)[0];
    expect(submitted.unitCost).toBe(100 / 300);
    expect(submitted.unitCost).not.toBe(0.3333);
    // The saved line amount - the server's round(quantity * unitCost, 2) - is the entered $100.00.
    expect(Math.round(submitted.quantity * submitted.unitCost * 100) / 100).toBe(100);
  });

  it('recalculates when the quantity changes, so no figure is left stale', () => {
    const { component } = readyToUpload();
    const item = component.items[0];

    component.onLineQuantityChange(item, 20);
    component.onLineAmountChange(item, 32.5);
    component.onLineQuantityChange(item, 10);

    expect(item.lineTotal).toBe(32.5);
    expect(item.unitCost).toBe(3.25);
    expect(component.derivedUnitCost(item)).toBe(3.25);
  });

  it('keeps the unit cost a supplier order prefilled and shows its matching line total', () => {
    const { component, upload } = createHarness('9');
    component.selectedFile = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
    component.form.title = 'Receiving order';

    expect(component.items[0].priceEntryMode).toBe('total');
    expect(component.items[0].quantity).toBe(10);
    expect(component.items[0].lineTotal).toBe(20);
    expect(component.items[0].unitCost).toBe(2);

    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 10, unitCost: 2 }]);
  });

  it('refuses a line with no amount entered, in either mode', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineAmountChange(item, null);
    component.upload();

    expect(upload).not.toHaveBeenCalled();
    expect(component.error).toContain('1 item(s)');

    component.onLinePriceEntryModeChange(item, 'unit');
    component.onLineAmountChange(item, null);
    component.upload();

    expect(upload).not.toHaveBeenCalled();
  });

  it('refuses a quantity that is not a whole number above zero, and never divides by it', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineAmountChange(item, 32.5);
    component.onLineQuantityChange(item, 0);

    expect(item.unitCost).toBeNull();
    expect(component.lineProblem(item)).toBe('Enter a whole number of units above zero.');

    component.upload();
    expect(upload).not.toHaveBeenCalled();

    component.onLineQuantityChange(item, 2.5);
    component.upload();
    expect(upload).not.toHaveBeenCalled();
  });

  it('never submits a non-finite price', () => {
    const { component, upload } = readyToUpload();

    component.onLineAmountChange(component.items[0], Number.NaN);
    component.upload();

    expect(upload).not.toHaveBeenCalled();
  });
});

describe('PurchaseUploadComponent price entry controls (issue #707)', () => {
  async function render() {
    const route = { queryParamMap: of(convertToParamMap({})) } as unknown as ActivatedRoute;
    const upload = jest.fn(() => of({} as Purchase));

    await TestBed.configureTestingModule({
      imports: [PurchaseUploadComponent],
      providers: [
        { provide: PurchaseService, useValue: { upload } },
        { provide: SupplierService, useValue: { getAll: () => of([]) } },
        { provide: SupplierOrderService, useValue: { getById: jest.fn() } },
        { provide: ProductService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: ActivatedRoute, useValue: route },
        provideRouter([])
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(PurchaseUploadComponent);
    fixture.detectChanges();
    fixture.componentInstance.addItem();
    fixture.componentInstance.onLineQuantityChange(fixture.componentInstance.items[0], 20);
    fixture.detectChanges();

    return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
  }

  /**
   * Types into one of the line's amount inputs the way the operator does. The pending `ngModel`
   * write of the freshly rendered input is awaited first, so the value under test is not overwritten
   * by it mid-event.
   */
  async function typeInto(
    fixture: ComponentFixture<PurchaseUploadComponent>, testId: string, value: string): Promise<void> {
    await fixture.whenStable();
    const input = (fixture.nativeElement as HTMLElement)
      .querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('shows the total paid as the editable amount and the unit cost as a calculated figure', async () => {
    const { fixture, host, component } = await render();

    expect(host.querySelector('[data-testid="purchase-item-price-mode"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="purchase-item-line-total"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="purchase-item-cost"]')).toBeNull();

    await typeInto(fixture, 'purchase-item-line-total', '32.50');

    expect(component.items[0].unitCost).toBe(1.625);
    expect(host.querySelector('[data-testid="purchase-item-derived-unit-cost"]')!.textContent)
      .toContain('$1.6250');
  });

  it('swaps which amount is editable when the operator picks Unit price', async () => {
    const { fixture, host, component } = await render();

    component.onLineQuantityChange(component.items[0], 24);
    component.onLinePriceEntryModeChange(component.items[0], 'unit');
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="purchase-item-line-total"]')).toBeNull();
    expect(host.querySelector('[data-testid="purchase-item-cost"]')).not.toBeNull();

    await typeInto(fixture, 'purchase-item-cost', '1.6625');

    expect(component.items[0].unitCost).toBe(1.6625);
    expect(host.querySelector('[data-testid="purchase-item-derived-line-total"]')!.textContent)
      .toContain('$39.90');
  });
});

/**
 * Add Purchase while a support session is active (issue #657, extending #554). A session grants
 * `PurchasingOperate`, so this write would otherwise still be offered - disabling it is this page's
 * own job, not the capability check's.
 */
describe('PurchaseUploadComponent write controls during a support session (issue #657)', () => {
  it('writes nothing while a session is active, even if the write method is invoked directly', () => {
    const { component, upload } = readyToUpload(null, of(true));

    component.upload();

    expect(upload).not.toHaveBeenCalled();
  });

  it('still uploads while no session is active', () => {
    const { component, upload } = readyToUpload(null, of(false));

    component.upload();

    expect(upload).toHaveBeenCalledTimes(1);
  });

  it('disables the Add Purchase button while a session is active, and leaves it enabled while none is', async () => {
    const route = { queryParamMap: of(convertToParamMap({})) } as unknown as ActivatedRoute;
    const sessionActive$ = of(true);

    await TestBed.configureTestingModule({
      imports: [PurchaseUploadComponent],
      providers: [
        { provide: PurchaseService, useValue: { upload: jest.fn(() => of({} as Purchase)) } },
        { provide: SupplierService, useValue: { getAll: () => of([]) } },
        { provide: SupplierOrderService, useValue: { getById: jest.fn() } },
        { provide: ProductService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: sessionActive$ } },
        { provide: ActivatedRoute, useValue: route },
        provideRouter([])
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(PurchaseUploadComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const submit = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Add Purchase') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
  });
});
