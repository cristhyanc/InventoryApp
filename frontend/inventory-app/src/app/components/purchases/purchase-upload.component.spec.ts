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
  Product,
  Purchase,
  SupplierOrder,
  SupplierOrderCompletionPreview,
  SupplierOrderStatus
} from '../../models/models';
import { PurchaseScanDraft, PurchaseScanLine, PurchaseScanValue } from '../../models/purchase-scan';
import { UNRESOLVED_LEVY_MESSAGE } from './purchase-form/purchase-scan-draft';
import { savedLineAmount } from './purchase-line-price';
import { SupportSessionService } from '../../support-session/support-session.service';
import { AccessService } from '../../access/access.service';
import { PurchaseScanService } from '../../services/purchase-scan.service';

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

describe('PurchaseUploadComponent invoice number (issue #1162)', () => {
  it('starts a new purchase with no invoice number', () => {
    const { component } = readyToUpload();

    expect(component.form.invoiceNumber).toBe('');
  });

  it('submits the invoice number that was typed', () => {
    const { component, upload } = readyToUpload();
    component.form.invoiceNumber = ' INV-123 ';

    component.upload();

    // Sent as typed: the server trims it, so the form never has to agree with it about trimming.
    expect(uploadedPayload(upload).invoiceNumber).toBe(' INV-123 ');
  });

  it('submits no invoice number when the field was left empty', () => {
    const { component, upload } = readyToUpload();

    component.upload();

    expect(uploadedPayload(upload).invoiceNumber).toBeNull();
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
    expect(savedLineAmount(item)).toBe(32.5);

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
        { provide: PurchaseScanService, useValue: { scan: jest.fn() } },
        { provide: AccessService, useValue: { allowsPlanFeature: () => of(true) } },
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
    expect(savedLineAmount(component.items[0])).toBe(100);
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

  it('disables the Save purchase button while a session is active', async () => {
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
        { provide: PurchaseScanService, useValue: { scan: jest.fn() } },
        { provide: AccessService, useValue: { allowsPlanFeature: () => of(true) } },
        { provide: ActivatedRoute, useValue: route },
        provideRouter([])
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(PurchaseUploadComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const submit = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Save purchase') as HTMLButtonElement;
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

/**
 * The redesigned Add Purchase (issue #1029): a suggested title the person's own typing always wins
 * over, the compact receipt control, and a line that must name a product.
 */
describe('PurchaseUploadComponent redesigned form (issue #1029)', () => {
  function todayLabel(): string {
    const now = new Date();
    const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    return `${now.getDate()} ${months[now.getMonth()]} ${now.getFullYear()}`;
  }

  it('opens on today, with a title suggested from the date', () => {
    const { component } = createHarness(null);

    expect(component.form.title).toBe(`Purchase — ${todayLabel()}`);
  });

  it('follows the supplier and the date while the person has not typed a title', () => {
    const { component } = createHarness(null);
    component.suppliers = [{ id: 3, name: 'Costco Wholesale' }];

    component.form.supplierId = 3;
    component.form.purchaseDate = '2026-10-24';
    component.refreshSuggestedTitle();

    expect(component.form.title).toBe('Costco Wholesale — 24 Oct 2026');
  });

  it('never overwrites a title the person typed', () => {
    const { component } = createHarness(null);
    component.suppliers = [{ id: 3, name: 'Costco Wholesale' }];

    component.form.title = 'Snacks for the gym';
    component.onTitleEdited();
    component.form.supplierId = 3;
    component.refreshSuggestedTitle();

    expect(component.form.title).toBe('Snacks for the gym');
  });

  it('names the supplier of the order it is receiving', () => {
    const { component } = createHarness('9');
    component.suppliers = [{ id: 1, name: 'Acme Supplies' }];
    component.refreshSuggestedTitle();

    expect(component.form.title).toBe(`Acme Supplies — ${todayLabel()}`);
  });

  it('starts a new line with no product chosen and refuses to save it until one is', () => {
    const { component, upload } = createHarness(null);
    component.addItem();
    component.onLineQuantityChange(component.items[0], 3);
    component.onLineTotalChange(component.items[0], 6);

    expect(component.items[0].productId).toBe(0);
    component.upload();

    expect(upload).not.toHaveBeenCalled();
    expect(component.error).toBe('Choose a product for every item. 1 item(s) have no product.');

    component.items[0].productId = 5;
    component.upload();
    expect(upload).toHaveBeenCalledTimes(1);
  });

  it('sends the attached receipt with the create, and nothing once it was removed again', () => {
    const { component, upload } = readyToUpload();
    const receipt = new File(['scan'], 'receipt.jpg', { type: 'image/jpeg' });

    component.onReceiptChange(receipt);
    component.onReceiptChange(null);
    component.upload();

    expect(uploadedPayload(upload).file).toBeNull();
  });

  it('sends a replaced receipt rather than the first one chosen', () => {
    const { component, upload } = readyToUpload();
    const replacement = new File(['scan 2'], 'receipt-2.pdf', { type: 'application/pdf' });

    component.onReceiptChange(new File(['scan 1'], 'receipt-1.pdf', { type: 'application/pdf' }));
    component.onReceiptChange(replacement);
    component.upload();

    expect(uploadedPayload(upload).file).toBe(replacement);
  });

  it('keeps an entered receipt total exactly as typed, whether or not it matches', () => {
    const { component, upload } = readyToUpload();
    component.form.totalAmount = 6.5;

    component.upload();

    expect(uploadedPayload(upload).totalAmount).toBe(6.5);
  });

  it('cancels back to where the purchase was started, saving nothing', () => {
    const manual = createHarness(null);
    manual.component.cancel();
    expect(manual.navigate).toHaveBeenCalledWith(['/purchases']);
    expect(manual.upload).not.toHaveBeenCalled();

    const fromOrder = createHarness('9');
    fromOrder.component.cancel();
    expect(fromOrder.navigate).toHaveBeenCalledWith(['/purchases/orders']);
  });
});

/**
 * Scanning a receipt into the Add Purchase form (issue #1166, sub-issue D of #975).
 *
 * What a scan does to this page is the page's own business; the Scan receipt workflow itself - the
 * plan lock, the request, its cancellation, the replace question and its error states - is
 * `purchase-receipt-scan.component.spec.ts`. **Nothing a scan fills in is saved** until the ordinary
 * create request this page has always sent.
 */
describe('PurchaseUploadComponent scanned receipt (issue #1166)', () => {
  function value<T>(scanned: T, provenance: 'read' | 'suggested' = 'read'): PurchaseScanValue<T> {
    return { value: scanned, provenance };
  }

  function line(overrides: Partial<PurchaseScanLine> = {}): PurchaseScanLine {
    return {
      kind: 'stock',
      text: value('COKE 375ML 24PK'),
      packs: value(2),
      unitsPerPack: value(24),
      units: value(48, 'suggested'),
      lineTotal: value(60),
      unitCost: value(1.25, 'suggested'),
      productId: value(5, 'suggested'),
      gst: { priceIncludesGst: 'yes', taxable: 'yes', marker: null },
      levyUnresolved: false,
      missing: [],
      ...overrides
    };
  }

  function scanDraft(overrides: Partial<PurchaseScanDraft> = {}): PurchaseScanDraft {
    return {
      supplierId: value(1, 'suggested'),
      purchaseDate: value('2026-11-02'),
      invoiceNumber: value('INV-77'),
      receiptTotal: value(180),
      lines: [
        line(),
        line({ productId: value(6, 'suggested'), units: value(24, 'suggested'), unitCost: value(2.5, 'suggested') }),
        line({ productId: value(7, 'suggested'), units: value(12, 'suggested'), unitCost: value(5, 'suggested') })
      ],
      usage: { month: '2026-11', used: 1, remaining: 99, limit: 100 },
      ...overrides
    };
  }

  /** The page with this business's catalogue known, as its own reads establish it. */
  function pageWithCatalogue(supplierOrderId: string | null = null) {
    const harness = createHarness(supplierOrderId);
    harness.component.products = [5, 6, 7].map((id) => ({ id, name: `Product ${id}` }) as Product);
    harness.component.suppliers = [{ id: 1, name: 'Acme Supplies' }];
    return harness;
  }

  /** The scan's own answer, handed to the page exactly as the workflow component reports it. */
  function scan(component: PurchaseUploadComponent, draft: PurchaseScanDraft): File {
    const file = new File(['receipt bytes'], 'receipt.jpg', { type: 'image/jpeg' });
    component.onScanned({ draft, file });
    return file;
  }

  it('fills three lines with the products, units and unit costs of the draft, and saves nothing yet', () => {
    const { component, upload } = pageWithCatalogue();

    const file = scan(component, scanDraft());

    expect(component.items).toHaveLength(3);
    expect(component.items.map((item) => item.productId)).toEqual([5, 6, 7]);
    expect(component.items.map((item) => item.quantity)).toEqual([48, 24, 12]);
    expect(component.items.map((item) => item.unitCost)).toEqual([1.25, 2.5, 5]);
    expect(component.selectedFile).toBe(file);
    expect(component.scannedReceipt).toBe(file);
    expect(upload).not.toHaveBeenCalled();
  });

  it('fills the supplier, date, invoice number, receipt total and the charges the draft read', () => {
    const { component } = pageWithCatalogue();

    scan(component, scanDraft({ deliveryCost: value(9.5), packageCost: value(0.25) }));

    expect(component.form.supplierId).toBe(1);
    expect(component.form.purchaseDate).toBe('2026-11-02');
    expect(component.form.invoiceNumber).toBe('INV-77');
    expect(component.form.totalAmount).toBe(180);
    expect(component.form.deliveryCost).toBe(9.5);
    expect(component.form.packageCost).toBe(0.25);
  });

  it('leaves the date and the supplier alone when the receipt carried neither', () => {
    const { component } = pageWithCatalogue();
    const today = component.form.purchaseDate;

    scan(component, scanDraft({ supplierId: null, purchaseDate: null }));

    expect(component.form.purchaseDate).toBe(today);
    expect(component.form.supplierId).toBe('');
  });

  it('sends exactly one create request, with the scanned file, through the existing endpoint', () => {
    const { component, upload, navigate } = pageWithCatalogue();
    component.form.title = 'Weekly restock';
    const file = scan(component, scanDraft());

    component.upload();

    expect(upload).toHaveBeenCalledTimes(1);
    expect(uploadedPayload(upload).file).toBe(file);
    expect(uploadedItems(upload)).toEqual([
      { productId: 5, quantity: 48, unitCost: 1.25 },
      { productId: 6, quantity: 24, unitCost: 2.5 },
      { productId: 7, quantity: 12, unitCost: 5 }
    ]);
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
  });

  it('blocks Save while a scanned line has no product, until one is chosen', () => {
    const { component, upload } = pageWithCatalogue();
    component.form.title = 'Weekly restock';
    scan(component, scanDraft({ lines: [line({ productId: null })] }));

    component.upload();
    expect(upload).not.toHaveBeenCalled();
    expect(component.error).toContain('Choose a product for every item');

    component.items[0].productId = 5;
    component.upload();

    expect(upload).toHaveBeenCalledTimes(1);
  });

  it('blocks Save while a scanned line has no pack size, until the units are filled in', () => {
    const { component, upload } = pageWithCatalogue();
    component.form.title = 'Weekly restock';
    scan(
      component,
      scanDraft({ lines: [line({ unitsPerPack: null, units: null, unitCost: null, missing: ['unitsPerPack'] })] })
    );

    expect(component.items[0].quantity).toBeNull();
    component.upload();
    expect(upload).not.toHaveBeenCalled();
    expect(component.error).toContain('whole number of units');

    component.onLineQuantityChange(component.items[0], 48);
    component.upload();

    // The printed $60.00 over the 48 units typed: the price was never asked for again.
    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 48, unitCost: 1.25 }]);
  });

  it('shows a line that is not stock as skipped and saves no purchase line for it', () => {
    const { component, upload } = pageWithCatalogue();
    component.form.title = 'Weekly restock';
    scan(
      component,
      scanDraft({
        lines: [
          line(),
          line({
            kind: 'other',
            text: value('A4 COPY PAPER'),
            productId: null,
            units: value(1, 'suggested'),
            unitCost: value(8.5, 'suggested'),
            lineTotal: value(8.5)
          })
        ]
      })
    );

    expect(component.items).toHaveLength(2);
    expect(component.items[1].scan?.skipped).toBe(true);

    // Not validated either: a line that is not saved has nothing to complete.
    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 48, unitCost: 1.25 }]);
  });

  it('saves a line that is not stock once a product is chosen for it', () => {
    const { component, upload } = pageWithCatalogue();
    component.form.title = 'Weekly restock';
    scan(
      component,
      scanDraft({
        lines: [
          line({
            kind: 'other',
            productId: null,
            units: value(1, 'suggested'),
            unitCost: value(8.5, 'suggested'),
            lineTotal: value(8.5)
          })
        ]
      })
    );

    component.items[0].scan!.skipped = false;
    component.items[0].productId = 6;
    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 6, quantity: 1, unitCost: 8.5 }]);
  });

  it('keeps an unresolved levy out of the lines and saves no purchase line for it', () => {
    const { component, upload } = pageWithCatalogue();
    component.form.title = 'Weekly restock';
    scan(
      component,
      scanDraft({
        lines: [
          line(),
          line({
            kind: 'levy',
            text: value('CONTAINER DEPOSIT'),
            packs: null,
            unitsPerPack: null,
            units: null,
            unitCost: null,
            productId: null,
            lineTotal: value(1.1),
            levyUnresolved: true
          })
        ]
      })
    );

    expect(component.items).toHaveLength(1);
    expect(component.scannedLevies).toEqual([
      { text: 'CONTAINER DEPOSIT', amount: 1.1, message: UNRESOLVED_LEVY_MESSAGE }
    ]);

    component.upload();

    expect(uploadedItems(upload)).toEqual([{ productId: 5, quantity: 48, unitCost: 1.25 }]);
  });

  it('classifies nothing for GST, which only a person or a rule decides', () => {
    const { component, upload } = pageWithCatalogue();
    component.form.title = 'Weekly restock';
    scan(component, scanDraft());

    component.upload();

    expect(component.items.every((item) => item.gstClassification === GstClassification.Unknown)).toBe(true);
    expect(uploadedItems(upload).every((item) => item.gstClassification === undefined)).toBe(true);
  });

  it('asks about the supplier order again, rather than reusing a projection of the replaced lines', () => {
    const { component, upload } = pageWithCatalogue('9');
    component.form.title = 'Weekly restock';
    component.upload();
    component.keepSupplierOrderOpen();
    expect(upload).toHaveBeenCalledTimes(1);

    scan(component, scanDraft());
    component.upload();

    expect(component.completionChoice).not.toBeNull();
    expect(upload).toHaveBeenCalledTimes(1);
  });

  /**
   * Issue #968: the receipt would take the business past its attachment storage allowance. The
   * purchase itself is always recordable, so the filled form is kept and the one way forward is
   * offered - and nothing is saved until it is chosen.
   */
  describe('a receipt the storage allowance refuses', () => {
    const REFUSAL =
      'This upload would exceed the business’s attachment storage allowance. ' +
      'Delete attachments you no longer need, or upgrade the plan, and try again. ' +
      'The record itself can still be saved without a document.';

    function refusedOnce() {
      const harness = pageWithCatalogue();
      let attempt = 0;
      harness.upload.mockImplementation(() => {
        attempt += 1;
        return attempt === 1 ? throwError(() => ({ error: REFUSAL })) : of({} as Purchase);
      });
      harness.component.form.title = 'Weekly restock';
      scan(harness.component, scanDraft());
      return harness;
    }

    it('keeps the filled form and offers to save the purchase without the receipt', () => {
      const { component } = refusedOnce();

      component.upload();

      expect(component.error).toBe(REFUSAL);
      expect(component.storageRefusedReceipt).toBe(true);
      expect(component.items).toHaveLength(3);
      expect(component.form.invoiceNumber).toBe('INV-77');
      expect(component.selectedFile).not.toBeNull();
    });

    it('saves nothing more until that choice is made, and then saves without the document', () => {
      const { component, upload, navigate } = refusedOnce();
      component.upload();
      expect(upload).toHaveBeenCalledTimes(1);
      expect(navigate).not.toHaveBeenCalled();

      component.saveWithoutReceipt();

      expect(upload).toHaveBeenCalledTimes(2);
      const second = (upload as jest.Mock).mock.calls[1][0] as PurchaseUploadPayload;
      expect(second.file).toBeNull();
      expect(second.items).toHaveLength(3);
      expect(component.storageRefusedReceipt).toBe(false);
      expect(navigate).toHaveBeenCalledWith(['/purchases']);
    });

    it('offers nothing of the kind for another refusal, or for a purchase with no document', () => {
      const other = pageWithCatalogue();
      other.component.form.title = 'Weekly restock';
      scan(other.component, scanDraft());
      other.upload.mockImplementation(() => throwError(() => ({ error: 'One or more purchase products do not exist.' })));
      other.component.upload();
      expect(other.component.storageRefusedReceipt).toBe(false);

      const noDocument = pageWithCatalogue();
      noDocument.component.form.title = 'Weekly restock';
      scan(noDocument.component, scanDraft());
      noDocument.component.onReceiptChange(null);
      noDocument.upload.mockImplementation(() => throwError(() => ({ error: REFUSAL })));
      noDocument.component.upload();
      expect(noDocument.component.storageRefusedReceipt).toBe(false);
    });

    it('withdraws the offer as soon as the next attempt is refused for something else', () => {
      const { component } = refusedOnce();
      component.upload();
      expect(component.storageRefusedReceipt).toBe(true);

      component.form.title = '  ';
      component.upload();

      expect(component.storageRefusedReceipt).toBe(false);
      expect(component.error).toBe('Please enter a purchase title.');
    });

    it('does nothing at all if that choice is somehow made when no receipt was refused', () => {
      const { component, upload } = pageWithCatalogue();
      component.form.title = 'Weekly restock';
      scan(component, scanDraft());

      component.saveWithoutReceipt();

      expect(upload).not.toHaveBeenCalled();
      expect(component.selectedFile).not.toBeNull();
    });
  });
});
