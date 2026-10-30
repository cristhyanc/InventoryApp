import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of, throwError } from 'rxjs';
import { PurchaseEditPageComponent } from './purchase-edit-page.component';
import { PurchaseService, PurchaseUpdatePayload } from '../../../services/purchase.service';
import { SupplierService } from '../../../services/supplier.service';
import { ProductService } from '../../../services/product.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { ToastService } from '../../../services/toast.service';
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
  attachDocument?: jest.Mock;
  getFile?: jest.Mock;
  gstSummary?: { inputGst: number; unresolvedComponentCount: number; unresolvedAmount: number } | null;
  id?: string;
  sessionActive?: Observable<boolean>;
}

/** The routed page with its route id, built the way a direct URL entry or a refresh builds it. */
function createPage(stubs: Stubs = {}) {
  const get = stubs.get ?? jest.fn(() => of(purchase()));
  const update = stubs.update ?? jest.fn(() => of(purchase()));
  const attachDocument = stubs.attachDocument ?? jest.fn(() => of(purchase()));
  const getFile = stubs.getFile ?? jest.fn(() => of(new Blob(['receipt'])));
  const getGstSummaryFor = jest.fn(() => stubs.gstSummary ?? null);
  const purchaseService = { get, update, attachDocument, getFile, getGstSummaryFor } as unknown as PurchaseService;
  // `getAllIncludingHidden` is deliberately absent (issue #934): a picker must ask for the
  // selectable list, and calling the Suppliers page's own read here would fail this render.
  const getSuppliers = jest.fn(() => of([]));
  const supplierService = { getAll: getSuppliers } as unknown as SupplierService;
  const productService = {
    getAll: jest.fn(() => of([product(10, 'Coke'), product(20, 'Chips')]))
  } as unknown as ProductService;
  const paramMap = new BehaviorSubject(convertToParamMap({ id: stubs.id ?? '42' }));
  const route = { paramMap: paramMap.asObservable() } as unknown as ActivatedRoute;
  const navigate = jest.fn();
  const router = { navigate } as unknown as Router;
  const supportSession = { active$: stubs.sessionActive ?? of(false) } as unknown as SupportSessionService;
  const toastService = { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() };

  const component = new PurchaseEditPageComponent(
    purchaseService, supplierService, productService, router, route, supportSession,
    toastService as unknown as ToastService);
  component.ngOnInit();

  return { component, get, update, attachDocument, getFile, navigate, paramMap, getSuppliers, toastService };
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

  /**
   * The supplier picker asks for the selectable list (issue #934), which the API answers without
   * the suppliers the business has marked "Not used" - so one cannot be chosen for a new purchase
   * while a purchase that already names it still shows it.
   */
  it('asks for the selectable suppliers rather than the Suppliers page list', () => {
    const { getSuppliers } = createPage();

    expect(getSuppliers).toHaveBeenCalledTimes(1);
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

  it('keeps the saved GST summary the read returned, for the form to show as last saved', () => {
    const { component } = createPage({ gstSummary: { inputGst: 2.5, unresolvedComponentCount: 1, unresolvedAmount: 4 } });

    expect(component.gstSummary).toEqual({ inputGst: 2.5, unresolvedComponentCount: 1, unresolvedAmount: 4 });
  });

  it('returns to the list on Cancel without persisting anything', () => {
    const { component, update, navigate } = createPage();

    component.onCancel();

    expect(update).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
  });
});

describe('PurchaseEditPageComponent template (issue #475)', () => {
  const render = async (stubs: { get: jest.Mock; sessionActive?: Observable<boolean> }) => {
    await TestBed.configureTestingModule({
      imports: [PurchaseEditPageComponent],
      providers: [
        {
          provide: PurchaseService,
          useValue: { get: stubs.get, update: jest.fn(() => of(purchase())), getGstSummaryFor: jest.fn(() => null) }
        },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
        { provide: SupplierService, useValue: { getAll: jest.fn(() => of([])) } },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of([product(10, 'Coke')])) } },
        { provide: SupportSessionService, useValue: { active$: stubs.sessionActive ?? of(false) } },
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

  /**
   * The same page shell `/purchases/new` uses, headed Edit Purchase with no subtitle (issue #1029).
   * The shared form layout draws its own cards, so the page adds no card around it, and nothing caps
   * or centres the content area that already supplies the sidebar space and the gutters.
   */
  it('uses the shared page shell, headed Edit Purchase with no subtitle', async () => {
    const { host } = await render({ get: jest.fn(() => of(purchase())) });

    expect(host.querySelector('.page h1')?.textContent?.trim()).toBe('Edit Purchase');
    expect(host.querySelector('.page-subtitle')).toBeNull();
    expect(host.querySelector('.page > .card')).toBeNull();
    const page = host.querySelector('.page');
    const classes = Array.from(page?.classList ?? []);
    expect(classes).not.toContain('mx-auto');
    expect(classes.filter((className) => /(^|:)max-w-/.test(className))).toEqual([]);
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

  /**
   * Save while a support session is active (issue #657, extending #554). A session grants
   * `PurchasingOperate`, so this write would otherwise still be offered - disabling it is this
   * page's own job, not the capability check's.
   */
  it('passes sessionActive through to the form, disabling Save', async () => {
    const { host } = await render({ get: jest.fn(() => of(purchase())), sessionActive: of(true) });

    const save = host.querySelector('[data-testid="edit-purchase-save"]') as HTMLButtonElement;
    expect(save.disabled).toBe(true);
  });

  it('leaves Save enabled while no session is active', async () => {
    const { host } = await render({ get: jest.fn(() => of(purchase())), sessionActive: of(false) });

    const save = host.querySelector('[data-testid="edit-purchase-save"]') as HTMLButtonElement;
    expect(save.disabled).toBe(false);
  });
});

/**
 * A receipt chosen during the edit (issue #1029). It goes through the existing attach endpoint only
 * after the edit has saved, and nothing here replaces or deletes a receipt the purchase already holds.
 */
describe('PurchaseEditPageComponent receipt (issue #1029)', () => {
  const file = () => new File(['scan'], 'receipt.pdf', { type: 'application/pdf' });

  it('saves the edit without attaching anything when no receipt was chosen', () => {
    const { component, update, attachDocument, navigate } = createPage();

    component.onSave(PAYLOAD);

    expect(update).toHaveBeenCalledTimes(1);
    expect(attachDocument).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
  });

  it('attaches the chosen receipt through the existing endpoint only after the edit saved', () => {
    const order: string[] = [];
    const update = jest.fn(() => { order.push('update'); return of(purchase()); });
    const attachDocument = jest.fn(() => { order.push('attach'); return of(purchase()); });
    const { component, navigate } = createPage({ update, attachDocument });
    const chosen = file();
    component.pendingReceipt = chosen;

    component.onSave(PAYLOAD);

    expect(order).toEqual(['update', 'attach']);
    expect(attachDocument).toHaveBeenCalledWith(42, chosen);
    expect(component.pendingReceipt).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
  });

  it('never attaches when the edit itself is refused, and keeps the chosen receipt', () => {
    const logged = jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const { component, attachDocument, navigate } = createPage({ update: jest.fn(() => throwError(() => ({ error: 'Rejected' }))) });
    const chosen = file();
    component.pendingReceipt = chosen;

    component.onSave(PAYLOAD);
    logged.mockRestore();

    expect(attachDocument).not.toHaveBeenCalled();
    expect(component.pendingReceipt).toBe(chosen);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('reports a refused attach without undoing the saved edit', () => {
    const attachDocument = jest.fn(() => throwError(() => ({ error: 'The document is too large.' })));
    const { component, navigate, toastService } = createPage({ attachDocument });
    component.pendingReceipt = file();

    component.onSave(PAYLOAD);

    expect(toastService.error).toHaveBeenCalledWith(
      'The purchase was saved, but the receipt was not attached. The document is too large. You can add it from the Purchases list.');
    expect(navigate).toHaveBeenCalledWith(['/purchases']);
    expect(component.saving).toBe(false);
  });

  it('forgets a chosen receipt when another purchase is opened', () => {
    const { component, paramMap } = createPage();
    component.pendingReceipt = file();

    paramMap.next(convertToParamMap({ id: '43' }));

    expect(component.pendingReceipt).toBeNull();
  });

  it('opens the saved receipt through the authorized file read', () => {
    const open = jest.spyOn(window, 'open').mockImplementation(() => null);
    const createObjectURL = jest.fn(() => 'blob:receipt');
    Object.defineProperty(URL, 'createObjectURL', { value: createObjectURL, configurable: true });
    const { component, getFile } = createPage();

    component.previewStoredReceipt();

    expect(getFile).toHaveBeenCalledWith(42);
    expect(open).toHaveBeenCalledWith('blob:receipt', '_blank', 'noopener');
    open.mockRestore();
  });
});

describe('PurchaseEditPageComponent write controls during a support session (issue #657)', () => {
  it('does not save while a session is active, even if onSave is invoked directly', () => {
    const { component, update } = createPage({ sessionActive: of(true) });

    component.onSave(PAYLOAD);

    expect(update).not.toHaveBeenCalled();
  });
});
