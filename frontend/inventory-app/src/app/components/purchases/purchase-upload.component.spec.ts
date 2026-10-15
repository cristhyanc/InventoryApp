import { TestBed } from '@angular/core/testing';
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

/** A ready-to-submit manual purchase form with one line. */
function readyToUpload(supplierOrderId: string | null = null, sessionActive: Observable<boolean> = of(false)) {
  const harness = createHarness(supplierOrderId, sessionActive);
  harness.component.selectedFile = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
  harness.component.form.title = 'Weekly restock';
  harness.component.addItem();
  harness.component.items[0] = { ...harness.component.items[0], productId: 5, quantity: 3, unitCost: 2 };
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
