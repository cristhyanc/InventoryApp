import { ComponentFixture, TestBed } from '@angular/core/testing';
import { convertToParamMap, ActivatedRoute, provideRouter, Router } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { PurchaseUploadComponent } from './purchase-upload.component';
import { PurchaseService } from '../../services/purchase.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { PurchaseItemPayload, PurchaseUploadPayload } from '../../services/purchase.service';
import {
  GstClassification,
  Purchase,
  SupplierOrder,
  SupplierOrderCompletionPreview,
  SupplierOrderStatus
} from '../../models/models';
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

/**
 * The server's own projection of what the order would still be owed (issue #974). The default is
 * the headline case: 10 ordered, nothing received, 3 on this purchase, 7 left undelivered.
 */
function completionPreview(
  overrides: Partial<SupplierOrderCompletionPreview> = {}
): SupplierOrderCompletionPreview {
  return {
    supplierOrderId: 9,
    status: SupplierOrderStatus.Ordered,
    eligibleForCompletion: true,
    hasOutstandingBalance: true,
    decisionFingerprint: 'fingerprint-from-the-server',
    outstandingLines: [
      {
        supplierOrderLineId: 1,
        productId: 5,
        productName: 'Coke 375mL',
        unit: 'can',
        quantityOrdered: 10,
        quantityAlreadyReceived: 0,
        quantityOnThisPurchase: 3,
        outstandingQuantity: 7
      }
    ],
    ...overrides
  };
}

function createHarness(
  supplierOrderId: string | null,
  sessionActive: Observable<boolean> = of(false),
  preview: Observable<SupplierOrderCompletionPreview> = of(completionPreview())
) {
  const route = {
    queryParamMap: of(convertToParamMap(supplierOrderId ? { supplierOrderId } : {}))
  } as unknown as ActivatedRoute;
  const navigate = jest.fn();
  const upload = jest.fn(() => of({} as Purchase));
  const purchaseService = { upload } as unknown as PurchaseService;
  const supplierService = { getAll: jest.fn(() => of([])) } as unknown as SupplierService;
  const completionPreviewRequest = jest.fn(() => preview);
  const supplierOrderService = {
    getById: jest.fn(() => of(supplierOrder())),
    completionPreview: completionPreviewRequest
  } as unknown as SupplierOrderService;
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

  return { component, navigate, purchaseService, toastService, upload, completionPreviewRequest };
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
 * change handlers, the way the template does it (issues #707 and #878).
 */
function readyToUpload(
  supplierOrderId: string | null = null,
  sessionActive: Observable<boolean> = of(false),
  preview: Observable<SupplierOrderCompletionPreview> = of(completionPreview())
) {
  const harness = createHarness(supplierOrderId, sessionActive, preview);
  harness.component.selectedFile = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
  harness.component.form.title = 'Weekly restock';
  harness.component.addItem();
  const item = harness.component.items[0];
  item.productId = 5;
  harness.component.onLineQuantityChange(item, 3);
  harness.component.onLineTotalChange(item, 6);
  return harness;
}

describe('PurchaseUploadComponent supplier-order receipt navigation (issue #387)', () => {
  it('returns to the Supplier Orders page after a receipt that started from a supplier order', () => {
    const { component, navigate } = createHarness('9');
    component.selectedFile = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
    component.form.title = 'Weekly restock';

    component.upload();
    // The linked purchase asks about the order's outstanding balance first (issue #974); keeping
    // the order open is the save that has always happened, and it still lands on this page.
    component.keepSupplierOrderOpen();

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

describe('PurchaseUploadComponent per-line price entry (issues #707, #878)', () => {
  it('starts every new line with the total paid as the winning field, nothing entered yet', () => {
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
    component.onLineTotalChange(item, 32.5);

    expect(item.unitCost).toBe(1.625);
    expect(component.savedLineAmount(item)).toBe(32.5);

    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 20, unitCost: 1.625 }]);
  });

  it('derives the line total from a unit price typed into the Unit price field', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineQuantityChange(item, 24);
    component.onLineUnitCostChange(item, 1.6625);

    expect(item.priceEntryMode).toBe('unit');
    expect(item.lineTotal).toBe(39.9);
    expect(component.totalPriceValue(item)).toBe('39.90');

    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 24, unitCost: 1.6625 }]);
  });

  it('submits the unrounded quotient when a line total does not divide evenly', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineQuantityChange(item, 300);
    component.onLineTotalChange(item, 100);

    // The Unit price field shows four decimals; what the request carries is the exact quotient.
    expect(component.unitPriceValue(item)).toBe('0.3333');

    component.upload();

    const submitted = uploadedItems(upload)[0];
    expect(submitted.unitCost).toBe(100 / 300);
    expect(submitted.unitCost).not.toBe(0.3333);
    // The saved line amount - the server's round(quantity * unitCost, 2) - is the entered $100.00.
    expect(Math.round(submitted.quantity * submitted.unitCost * 100) / 100).toBe(100);
  });

  it('hands the win to the field typed into last, in either direction', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineQuantityChange(item, 28);
    component.onLineTotalChange(item, 100);

    expect(item.unitCost).toBe(100 / 28);

    component.onLineUnitCostChange(item, 1.5);

    expect([item.priceEntryMode, item.unitCost, item.lineTotal]).toEqual(['unit', 1.5, 42]);

    component.onLineTotalChange(item, 56);

    expect([item.priceEntryMode, item.unitCost, item.lineTotal]).toEqual(['total', 2, 56]);

    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 28, unitCost: 2 }]);
  });

  it('recalculates from the field that won last when the quantity changes', () => {
    const { component } = readyToUpload();
    const item = component.items[0];

    component.onLineQuantityChange(item, 20);
    component.onLineTotalChange(item, 32.5);
    component.onLineQuantityChange(item, 10);

    // A typed total is spread over the new quantity; the total itself stays as it was typed.
    expect(item.lineTotal).toBe(32.5);
    expect(item.unitCost).toBe(3.25);

    component.onLineUnitCostChange(item, 1.5);
    component.onLineQuantityChange(item, 20);

    // A typed unit price is multiplied by the new quantity and is itself left alone.
    expect(item.unitCost).toBe(1.5);
    expect(item.lineTotal).toBe(30);
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
    component.keepSupplierOrderOpen();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 10, unitCost: 2 }]);
  });

  it('refuses a line whose winning field was cleared, and clears the calculated one', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineTotalChange(item, null);

    expect(item.unitCost).toBeNull();
    expect(component.unitPriceValue(item)).toBeNull();

    component.upload();

    expect(upload).not.toHaveBeenCalled();
    expect(component.error).toContain('1 item(s)');

    component.onLineUnitCostChange(item, null);

    expect(item.lineTotal).toBeNull();
    expect(component.totalPriceValue(item)).toBeNull();

    component.upload();

    expect(upload).not.toHaveBeenCalled();
  });

  it('refuses a quantity that is not a whole number above zero, and never divides by it', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineTotalChange(item, 32.5);
    component.onLineQuantityChange(item, 0);

    expect(item.unitCost).toBeNull();
    expect(component.unitPriceValue(item)).toBeNull();
    expect(component.lineProblem(item)).toBe('Enter a whole number of units above zero.');

    component.upload();
    expect(upload).not.toHaveBeenCalled();

    component.onLineQuantityChange(item, 2.5);
    component.upload();
    expect(upload).not.toHaveBeenCalled();
  });

  it('clears the total rather than leaving a stale one under an unusable quantity', () => {
    const { component, upload } = readyToUpload();
    const item = component.items[0];

    component.onLineUnitCostChange(item, 1.5);
    component.onLineQuantityChange(item, 2.5);

    expect(item.lineTotal).toBeNull();
    expect(component.totalPriceValue(item)).toBeNull();

    component.upload();
    expect(upload).not.toHaveBeenCalled();
  });

  it('never submits a non-finite price', () => {
    const { component, upload } = readyToUpload();

    component.onLineTotalChange(component.items[0], Number.NaN);
    component.upload();

    expect(upload).not.toHaveBeenCalled();
  });
});

describe('PurchaseUploadComponent price entry controls (issue #878)', () => {
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

  /** One of the line's two price inputs. */
  function priceInput(host: HTMLElement, testId: string): HTMLInputElement {
    const input = host.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement | null;
    expect(input).not.toBeNull();
    return input!;
  }

  it('offers both prices as editable inputs with their own labels, and no selector or hint', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="purchase-item-price-mode"]')).toBeNull();
    expect(host.querySelector('[data-testid="purchase-item-derived-unit-cost"]')).toBeNull();
    expect(host.querySelector('[data-testid="purchase-item-derived-line-total"]')).toBeNull();
    expect(host.textContent).not.toContain('Line total:');
    expect(host.textContent).not.toContain('Unit cost:');

    const unitPrice = priceInput(host, 'purchase-item-cost');
    const totalPrice = priceInput(host, 'purchase-item-line-total');

    expect(unitPrice.readOnly).toBe(false);
    expect(totalPrice.readOnly).toBe(false);
    expect(unitPrice.getAttribute('aria-label')).toBe('Unit price for item 1');
    expect(totalPrice.getAttribute('aria-label')).toBe('Total price for item 1');
  });

  it('fills the Unit price field to four decimals from a typed total, unrounded in the request', async () => {
    const { fixture, host, component } = await render();

    component.onLineQuantityChange(component.items[0], 300);
    await typeInto(fixture, 'purchase-item-line-total', '100');

    expect(component.items[0].unitCost).toBe(100 / 300);
    expect(priceInput(host, 'purchase-item-cost').value).toBe('0.3333');
  });

  it('fills the Total price field to the cent from a typed unit price', async () => {
    const { fixture, host, component } = await render();

    component.onLineQuantityChange(component.items[0], 28);
    await typeInto(fixture, 'purchase-item-cost', '1.5');

    expect(component.items[0].priceEntryMode).toBe('unit');
    expect(component.items[0].unitCost).toBe(1.5);
    expect(priceInput(host, 'purchase-item-line-total').value).toBe('42.00');
  });

  it('keeps both fields editable as the win passes between them', async () => {
    const { fixture, host, component } = await render();

    await typeInto(fixture, 'purchase-item-line-total', '32.50');

    expect(component.items[0].unitCost).toBe(1.625);
    expect(priceInput(host, 'purchase-item-cost').value).toBe('1.6250');

    await typeInto(fixture, 'purchase-item-cost', '1.6625');

    expect(component.items[0].priceEntryMode).toBe('unit');
    expect(component.items[0].unitCost).toBe(1.6625);
    expect(priceInput(host, 'purchase-item-line-total').value).toBe('33.25');
  });

  it('leaves an unrounded calculated unit cost alone when its field is only focused and blurred', async () => {
    const { fixture, host, component } = await render();

    component.onLineQuantityChange(component.items[0], 300);
    await typeInto(fixture, 'purchase-item-line-total', '100');

    const unitPrice = priceInput(host, 'purchase-item-cost');
    unitPrice.dispatchEvent(new Event('focus'));
    unitPrice.dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    // The displayed 0.3333 must not become the submitted value: 300 x 0.3333 is $99.99, not $100.
    expect(component.items[0].priceEntryMode).toBe('total');
    expect(component.items[0].unitCost).toBe(100 / 300);
    expect(component.savedLineAmount(component.items[0])).toBe(100);
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

/**
 * Offering the keep-open / complete choice before a linked purchase is saved (issue #974).
 *
 * The projected outstanding balance is always the server's: this page asks
 * `SupplierOrderService.completionPreview` and formats what comes back. What is asserted here is the
 * decision flow around it - when the choice is offered at all, what each answer submits, and that
 * cancelling and failing both leave the entered purchase on screen.
 */
describe('PurchaseUploadComponent supplier-order completion choice (issue #974)', () => {
  /**
   * A ready-to-save receipt for supplier order 9: its one outstanding line prefilled at the 10
   * units ordered and the $2.00 the order agreed, with a document and a title. The order is still
   * owed 10, so 10 received would complete it and anything less leaves a balance.
   */
  function readyToReceive(preview: Observable<SupplierOrderCompletionPreview> = of(completionPreview())) {
    const harness = createHarness('9', of(false), preview);
    harness.component.selectedFile = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
    harness.component.form.title = 'Weekly restock';
    // The final delivery is short: 3 of the 10 units ordered, at the $2.00 unit price the order
    // agreed (entered as a unit price, so the line total follows rather than the other way round).
    harness.component.onLineQuantityChange(harness.component.items[0], 3);
    harness.component.onLineUnitCostChange(harness.component.items[0], 2);
    return harness;
  }

  it('asks the server for the projected outstanding balance before saving a linked purchase', () => {
    const { component, completionPreviewRequest, upload } = readyToReceive();

    component.upload();

    expect(completionPreviewRequest).toHaveBeenCalledTimes(1);
    expect(completionPreviewRequest.mock.calls[0]).toEqual([
      9,
      expect.objectContaining({ supplierId: 1, lines: [{ productId: 5, quantity: 3 }] })
    ]);
    // Nothing is saved while the choice is open: the operator has not answered it yet.
    expect(upload).not.toHaveBeenCalled();
    expect(component.completionChoice).not.toBeNull();
    expect(component.saving).toBe(false);
  });

  it('keeps the order open by submitting no completion decision at all', () => {
    const { component, upload, navigate } = readyToReceive();
    component.upload();

    component.keepSupplierOrderOpen();

    const payload = uploadedPayload(upload);
    expect(payload.completeSupplierOrderId ?? null).toBeNull();
    expect(payload.completeSupplierOrderFingerprint ?? null).toBeNull();
    expect(component.completionChoice).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/purchases/orders']);
  });

  it('completes the order with the order id and the fingerprint the server issued, and nothing else', () => {
    const { component, upload } = readyToReceive();
    component.upload();

    component.completeSupplierOrder();

    const payload = uploadedPayload(upload);
    expect(payload.completeSupplierOrderId).toBe(9);
    expect(payload.completeSupplierOrderFingerprint).toBe('fingerprint-from-the-server');
    // The quantities saved are the ones that were entered: completing invents no receipt.
    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 3, unitCost: 2 }]);
  });

  it('returns to the form without saving, and without losing what was entered, when the choice is cancelled', () => {
    const { component, upload, navigate } = readyToReceive();
    component.upload();

    component.cancelCompletionChoice();

    expect(upload).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(component.completionChoice).toBeNull();
    expect(component.saving).toBe(false);
    expect(component.form.title).toBe('Weekly restock');
    expect(component.items).toHaveLength(1);
    expect(component.items[0]).toMatchObject({ productId: 5, quantity: 3, unitCost: 2 });
    expect(component.selectedFile).not.toBeNull();
  });

  it('saves straight through when the purchase leaves nothing outstanding', () => {
    const { component, upload } = readyToReceive(
      of(completionPreview({ hasOutstandingBalance: false, outstandingLines: [] }))
    );

    component.upload();

    expect(component.completionChoice).toBeNull();
    expect(uploadedPayload(upload).completeSupplierOrderId ?? null).toBeNull();
  });

  it('saves straight through when the order is no longer one that can be completed', () => {
    const { component, upload } = readyToReceive(
      of(completionPreview({ eligibleForCompletion: false, status: SupplierOrderStatus.Received }))
    );

    component.upload();

    expect(component.completionChoice).toBeNull();
    expect(uploadedPayload(upload).completeSupplierOrderId ?? null).toBeNull();
  });

  it('never asks about an order for a purchase that is not linked to one', () => {
    const { component, completionPreviewRequest, upload } = readyToUpload();

    component.upload();

    expect(completionPreviewRequest).not.toHaveBeenCalled();
    expect(component.completionChoice).toBeNull();
    expect(upload).toHaveBeenCalledTimes(1);
  });

  it('keeps the entered purchase and asks again when the save is refused as stale', () => {
    const { component, upload, toastService } = readyToReceive();
    (upload as jest.Mock).mockImplementation(() =>
      throwError(() => ({ error: 'This supplier order changed after the completion choice was shown.' })));
    component.upload();
    component.completeSupplierOrder();

    expect(component.error).toBe('This supplier order changed after the completion choice was shown.');
    expect(component.saving).toBe(false);
    expect(component.items).toHaveLength(1);
    // Nothing claims the order was completed, because nothing was stored.
    expect(toastService.success).not.toHaveBeenCalled();

    // The next attempt re-previews rather than reusing a fingerprint the server already refused.
    (upload as jest.Mock).mockImplementation(() => of({} as Purchase));
    component.upload();

    expect(component.completionChoice).not.toBeNull();
  });

  it('reports a failed preview and saves nothing', () => {
    const { component, upload } = readyToReceive(throwError(() => new Error('offline')));

    component.upload();

    expect(upload).not.toHaveBeenCalled();
    expect(component.completionChoice).toBeNull();
    expect(component.error).not.toBe('');
    expect(component.saving).toBe(false);
  });
});

/**
 * Saving a purchase with no supporting document (issue #976, child of #965). A business with no
 * receipt to attach - or no storage left to put one in - still has to be able to record what it
 * bought, and the document can be added to the saved purchase afterwards.
 */
describe('PurchaseUploadComponent saving without a document (issue #976)', () => {
  /** The same ready-to-submit form `readyToUpload` builds, with no file chosen. */
  function readyWithoutDocument() {
    const harness = createHarness(null);
    harness.component.form.title = 'Weekly restock';
    harness.component.addItem();
    const item = harness.component.items[0];
    item.productId = 5;
    harness.component.onLineQuantityChange(item, 3);
    harness.component.onLineTotalChange(item, 6);
    return harness;
  }

  it('saves the purchase with no file and reports no error', () => {
    const { component, upload, navigate } = readyWithoutDocument();

    component.upload();

    expect(component.error).toBe('');
    expect(uploadedPayload(upload).file).toBeNull();
    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 3, unitCost: 2 }]);
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
  });

  it('still sends the chosen document when there is one', () => {
    const { component, upload } = readyToUpload();

    component.upload();

    expect(uploadedPayload(upload).file).toBeInstanceOf(File);
  });

  it('still requires a title, which no file name can stand in for', () => {
    const { component, upload } = readyWithoutDocument();
    component.form.title = '   ';

    component.upload();

    expect(upload).not.toHaveBeenCalled();
    expect(component.error).toBe('Please enter a purchase title.');
  });

  it('asks about the supplier order it is receiving even with no document', () => {
    const harness = createHarness('9');
    harness.component.form.title = 'Weekly restock';

    harness.component.upload();

    expect(harness.component.completionChoice).not.toBeNull();
    expect(harness.upload).not.toHaveBeenCalled();
  });
});
