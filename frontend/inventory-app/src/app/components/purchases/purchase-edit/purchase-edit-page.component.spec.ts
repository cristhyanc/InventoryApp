import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, of, throwError } from 'rxjs';
import { PurchaseEditPageComponent } from './purchase-edit-page.component';
import { PurchaseService, PurchaseUpdatePayload } from '../../../services/purchase.service';
import { SupplierService } from '../../../services/supplier.service';
import { ProductService } from '../../../services/product.service';
import {
  GstClassification,
  GstClassificationSource,
  Product,
  Purchase,
  PurchaseItem
} from '../../../models/models';

function item(overrides: Partial<PurchaseItem> = {}): PurchaseItem {
  return {
    id: 11,
    receiptId: 42,
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
    id: 42,
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

interface Stubs {
  get?: jest.Mock;
  update?: jest.Mock;
  id?: string;
}

/** The routed page with its route id, built the way a direct URL entry or a refresh builds it. */
function createPage(stubs: Stubs = {}) {
  const get = stubs.get ?? jest.fn(() => of(purchase()));
  const update = stubs.update ?? jest.fn(() => of(purchase()));
  const purchaseService = { get, update } as unknown as PurchaseService;
  const supplierService = { getAll: jest.fn(() => of([])) } as unknown as SupplierService;
  const productService = {
    getAll: jest.fn(() => of([product(10, 'Coke'), product(20, 'Chips')]))
  } as unknown as ProductService;
  const paramMap = new BehaviorSubject(convertToParamMap({ id: stubs.id ?? '42' }));
  const route = { paramMap: paramMap.asObservable() } as unknown as ActivatedRoute;
  const navigate = jest.fn();
  const router = { navigate } as unknown as Router;

  const component = new PurchaseEditPageComponent(purchaseService, supplierService, productService, router, route);
  component.ngOnInit();

  return { component, get, update, navigate, paramMap };
}

/** The update payload the page handed to `PurchaseService.update`, with the id it used. */
function updatedWith(update: jest.Mock): [number, PurchaseUpdatePayload] {
  expect(update).toHaveBeenCalledTimes(1);
  return update.mock.calls[0] as [number, PurchaseUpdatePayload];
}

const PAYLOAD: PurchaseUpdatePayload = {
  title: 'Weekly restock',
  notes: null,
  totalAmount: 10,
  deliveryCost: null,
  packageCost: null,
  purchaseDate: '2026-04-01T00:00:00.000Z',
  supplierId: null,
  items: [{ id: 11, productId: 10, quantity: 2, unitCost: 1.1 }]
};

describe('PurchaseEditPageComponent loading (issue #475)', () => {
  it('loads the purchase named by the route id, so a bookmarked URL and a refresh both work', () => {
    const { component, get } = createPage({ id: '42' });

    expect(get).toHaveBeenCalledWith(42);
    expect(component.purchase?.id).toBe(42);
    expect(component.loading).toBe(false);
    expect(component.unavailable).toBe('');
  });

  it('reloads for a different route id, so browser Back and Forward show the right purchase', () => {
    const { component, get, paramMap } = createPage({ id: '42' });

    paramMap.next(convertToParamMap({ id: '43' }));

    expect(get).toHaveBeenNthCalledWith(2, 43);
    expect(component.purchase).not.toBeNull();
  });

  it('stays in its loading state while the read is outstanding, rather than showing an empty form', () => {
    const { component } = createPage({ get: jest.fn(() => NEVER) });

    expect(component.loading).toBe(true);
    expect(component.purchase).toBeNull();
  });

  it('reports a purchase this business cannot see as unavailable and offers no form', () => {
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const { component } = createPage({ get: jest.fn(() => throwError(() => ({ status: 404 }))) });
    logged.mockRestore();

    expect(component.purchase).toBeNull();
    expect(component.loading).toBe(false);
    expect(component.unavailable).toContain('no longer available');
  });

  it('reports a failed read separately from a purchase that does not exist', () => {
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const { component } = createPage({ get: jest.fn(() => throwError(() => ({ status: 500 }))) });
    logged.mockRestore();

    expect(component.purchase).toBeNull();
    expect(component.unavailable).toContain('Failed to load');
  });

  it('refuses a route id that is not a purchase id, without calling the API', () => {
    const get = jest.fn(() => of(purchase()));
    const { component } = createPage({ id: 'abc', get });

    expect(get).not.toHaveBeenCalled();
    expect(component.unavailable).not.toBe('');
  });
});

describe('PurchaseEditPageComponent saving (issue #475)', () => {
  it('saves through the existing update API with the route id and returns to the list', () => {
    const { component, update, navigate } = createPage();

    component.onSave(PAYLOAD);

    const [id, payload] = updatedWith(update);
    expect(id).toBe(42);
    expect(payload).toBe(PAYLOAD);
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
    expect(component.saving).toBe(false);
  });

  it('keeps the page open with the entered values and reports the API message when the save is refused', () => {
    const refused = jest.fn(() =>
      throwError(() => ({ error: 'A GST classification must be one of Unknown, Taxable, GstFree.' }))
    );
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const { component, navigate } = createPage({ update: refused });

    component.onSave(PAYLOAD);
    logged.mockRestore();

    expect(navigate).not.toHaveBeenCalled();
    expect(component.purchase?.id).toBe(42);
    expect(component.saveError).toContain('A GST classification must be one of');
    expect(component.saving).toBe(false);
  });

  it('falls back to its own message when a refusal carries no text', () => {
    const refused = jest.fn(() => throwError(() => ({ status: 500 })));
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const { component } = createPage({ update: refused });

    component.onSave(PAYLOAD);
    logged.mockRestore();

    expect(component.saveError).toBe('Failed to save the purchase.');
  });

  it('ignores a second save while the first is still in flight', () => {
    const pending = jest.fn(() => NEVER);
    const { component } = createPage({ update: pending });

    component.onSave(PAYLOAD);
    component.onSave(PAYLOAD);

    expect(pending).toHaveBeenCalledTimes(1);
    expect(component.saving).toBe(true);
  });

  it('clears a previous refusal when the person saves again', () => {
    let refuse = true;
    const update = jest.fn(() => (refuse ? throwError(() => ({ error: 'Rejected' })) : of(purchase())));
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const { component, navigate } = createPage({ update });

    component.onSave(PAYLOAD);
    expect(component.saveError).toBe('Rejected');

    refuse = false;
    component.onSave(PAYLOAD);
    logged.mockRestore();

    expect(component.saveError).toBe('');
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
  });

  it('returns to the list on Cancel without persisting anything', () => {
    const { component, update, navigate } = createPage();

    component.onCancel();

    expect(update).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
  });
});

describe('PurchaseEditPageComponent template (issue #475)', () => {
  const render = async (stubs: { get: jest.Mock }) => {
    await TestBed.configureTestingModule({
      imports: [PurchaseEditPageComponent],
      providers: [
        { provide: PurchaseService, useValue: { get: stubs.get, update: jest.fn(() => of(purchase())) } },
        { provide: SupplierService, useValue: { getAll: jest.fn(() => of([])) } },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of([product(10, 'Coke')])) } },
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: '42' })) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(PurchaseEditPageComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  };

  afterEach(() => TestBed.resetTestingModule());

  it('renders the edit form for the loaded purchase', async () => {
    const { host } = await render({ get: jest.fn(() => of(purchase())) });

    expect(host.querySelector('app-purchase-edit-form')).not.toBeNull();
    expect(host.querySelector('[data-testid="purchase-edit-unavailable"]')).toBeNull();
    expect((host.querySelector('#editTitle') as HTMLInputElement).value).toBe('Weekly restock');
  });

  /** The same page shell and capped card width `/purchases/new` uses, so both forms read alike. */
  it('uses the shared page shell and the purchase form card width', async () => {
    const { host } = await render({ get: jest.fn(() => of(purchase())) });

    expect(host.querySelector('.page > .page-header .page-title')?.textContent).toContain('Edit Purchase');
    expect(host.querySelector('.card.mx-auto.w-full.max-w-5xl')).not.toBeNull();
  });

  it('renders an unavailable state with a way back to the list instead of a form', async () => {
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const { host } = await render({ get: jest.fn(() => throwError(() => ({ status: 404 }))) });
    logged.mockRestore();

    expect(host.querySelector('app-purchase-edit-form')).toBeNull();
    const unavailable = host.querySelector('[data-testid="purchase-edit-unavailable"]');
    expect(unavailable?.textContent).toContain('no longer available');
    expect(host.querySelector('[data-testid="purchase-edit-back"]')).not.toBeNull();
  });
});
