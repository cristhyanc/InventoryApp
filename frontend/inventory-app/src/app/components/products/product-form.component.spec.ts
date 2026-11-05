import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, Subject, throwError } from 'rxjs';
import {
  PRODUCT_GST_OUTSTANDING_LEAVE_WARNING,
  PRODUCT_SAVE_IN_FLIGHT_LEAVE_WARNING,
  PRODUCT_UNSAVED_LEAVE_WARNING,
  ProductFormComponent
} from './product-form.component';
import { confirmProductFormLeave } from './product-form-leave.guard';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { Category, GstClassification, MasterProductSuggestion, Product } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { SEARCH_DEBOUNCE_MS } from '../shared/filter-request-trigger';
import {
  MASTER_PRODUCT_CHANGED_CODE,
  MASTER_PRODUCT_REFUSED_CODE,
  MASTER_PRODUCT_UNAVAILABLE_CODE
} from './master-product-selection';

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 7,
    name: 'E2E Chips',
    sku: 'SNK-001',
    description: 'Salted chips',
    averageUnitCost: 1.2,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 12,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 4,
    restockTo: 18,
    needToOrder: 0,
    mdbCode: null,
    unit: 'bag',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId: 2,
    isActive: true,
    isLowStock: false,
    isReorderAlert: false,
    providerManagedFields: [],
    // What `GET api/products/{id}` answers for an ordinary product nothing refers to (issue #737):
    // the delete is allowed and there is no refusal to show. The issue #825 tests about the
    // deletion notice override it below.
    canDelete: true,
    deletionRefusal: null,
    ...overrides
  };
}

const categories: Category[] = [
  { id: 1, name: 'Drinks' },
  { id: 2, name: 'Snacks' }
];

/**
 * One master product as the lookup of issue #1049 publishes it, with the server's own prefill: the
 * two values a selection fills in and the two the create compares the submission against.
 */
function masterProductSuggestion(overrides: Partial<MasterProductSuggestion> = {}): MasterProductSuggestion {
  return {
    id: 12,
    brand: "CC's",
    name: 'Tasty Cheese Corn Chips',
    sizeValue: 45,
    sizeUnit: 'g',
    productName: "CC's Tasty Cheese Corn Chips",
    unitLabel: '45g',
    ...overrides
  };
}

/** The 175 g variant of that product: the case two near-identical sizes have to be told apart. */
const partySizeSuggestion = masterProductSuggestion({ id: 13, sizeValue: 175, unitLabel: '175g' });

/** The label the suggestion list renders, which is how a row is pressed in these tests. */
const SUGGESTION_LABEL = "CC's Tasty Cheese Corn Chips · 45 g";
const PARTY_SUGGESTION_LABEL = "CC's Tasty Cheese Corn Chips · 175 g";

/** A `409` of the shape issue #1051's create refuses a selection with. */
function selectionConflict(code: string, message: string): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: { title: 'The selected master product is no longer available', detail: message, status: 409, message, code }
  });
}

/** The routed edit page, rendered the way entering `/products/7/edit` renders it. */
async function render(
  overrides: {
    product?: Product;
    update?: jest.Mock;
    create?: jest.Mock;
    getGstRule?: jest.Mock;
    setGstRule?: jest.Mock;
    sessionActive?: Observable<boolean>;
    routeParams?: Record<string, string>;
    getCategories?: jest.Mock;
    createCategory?: jest.Mock;
    getPriceTracking?: jest.Mock;
    setPriceTracking?: jest.Mock;
    getMasterProductSuggestions?: jest.Mock;
  } = {}
) {
  const update = overrides.update ?? jest.fn(() => of(undefined));
  const create = overrides.create ?? jest.fn(() => of(product()));
  const getCategories = overrides.getCategories ?? jest.fn(() => of(categories.map((category) => ({ ...category }))));
  const createCategory = overrides.createCategory ?? jest.fn(() => of({ id: 9, name: 'Chocolate' } as Category));
  const getGstRule =
    overrides.getGstRule ?? jest.fn(() => of({ productId: 7, gstRule: GstClassification.Unknown }));
  const setGstRule = overrides.setGstRule ?? jest.fn(() => of(undefined));
  const getPriceTracking =
    overrides.getPriceTracking ??
    jest.fn(() =>
      of({
        productId: 7,
        tracked: false,
        status: null,
        searchName: null,
        unitLabel: null,
        masterProduct: null,
        canTrack: true,
        trackRefusal: null
      })
    );
  // The Name suggestions on New Product (issue #1052). Their own behaviour - the debounce, the
  // superseded response, the list states and the keyboard - is
  // `product-master-product-picker.component.spec.ts`; here the lookup only has to answer.
  const setPriceTracking = overrides.setPriceTracking ?? jest.fn(() => of(undefined));
  const getMasterProductSuggestions =
    overrides.getMasterProductSuggestions ??
    jest.fn(() => of({ products: [masterProductSuggestion()], page: 1, pageSize: 10, hasMore: false }));
  const navigate = jest.fn();

  await TestBed.configureTestingModule({
    imports: [ProductFormComponent],
    providers: [
      {
        provide: ProductService,
        useValue: {
          get: jest.fn(() => of(overrides.product ?? product())),
          update,
          create,
          getGstRule,
          setGstRule,
          // No `getPriceComparison` (issue #1111): the supplier price comparison is gone from Edit
          // Product, so a render that still asked for it would fail here.
          // The "Track supplier prices" panel (issue #938) reads this; its own behaviour is
          // ProductPriceTrackingComponent's spec.
          getPriceTracking: getPriceTracking,
          setPriceTracking,
          getMasterProductSuggestions
        }
      },
      { provide: CategoryService, useValue: { getAll: getCategories, create: createCategory } },
      { provide: SupportSessionService, useValue: { active$: overrides.sessionActive ?? of(false) } },
      { provide: RolePreviewService, useValue: { active$: of(false) } },
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(overrides.routeParams ?? { id: '7' }) } } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductFormComponent);
  jest.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(navigate);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;

  /** Chooses a GST rule the way a person does, through the composed panel's own picker. */
  const chooseGstRule = (label: string) => {
    const picker = host.querySelector<HTMLSelectElement>('app-product-gst-rule select')!;
    picker.selectedIndex = Array.from(picker.options).findIndex((option) => option.textContent?.trim() === label);
    picker.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  };

  /**
   * Presses the page's one submit control and lets the composed components see the result, the way
   * a change-detection pass follows the response in the running application. The created product's
   * GST rule is written by `app-product-create-gst-rule` on the id this page hands it (issue #829),
   * so that pass is what carries the create's outcome to it.
   */
  const submit = () => {
    fixture.componentInstance.save();
    fixture.detectChanges();
  };

  /**
   * The retry of a created product's rule after its write failed (issue #829): since issue #1111
   * it is the page's one primary action, relabelled, never a button of the GST card's own.
   */
  const gstRuleRetry = () => {
    const button = host.querySelector<HTMLButtonElement>('[data-testid="product-form-submit"]');
    return button?.textContent?.trim() === 'Retry GST rule' ? button : null;
  };

  /** Types into the composed category combobox the way a person does (issue #830). */
  const typeCategory = (text: string) => {
    const element = host.querySelector<HTMLInputElement>('app-product-category-picker input[role="combobox"]')!;
    element.value = text;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  const categoryOption = (label: string) =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('app-product-category-picker [role="option"]')).find(
      (option) => option.textContent?.trim() === label
    );

  const createCategoryOption = () =>
    host.querySelector<HTMLButtonElement>('app-product-category-picker [role="option"][data-entry-kind="create"]');

  /** Presses a row of the category list and lets the page see the selection it reported. */
  const clickCategoryOption = (option: HTMLButtonElement) => {
    option.click();
    fixture.detectChanges();
  };

  /** Types into the composed Name combobox the way a person does (issue #1052). */
  const typeName = (text: string) => {
    const element = host.querySelector<HTMLInputElement>(
      'app-product-master-product-picker input[role="combobox"]'
    )!;
    element.value = text;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  /** Types into the Unit Label field the way a person does, through its own binding. */
  const typeUnit = (text: string) => {
    const element = host.querySelector<HTMLInputElement>('[name="unit"]')!;
    element.value = text;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  /**
   * Settles the suggestion lookup's debounce and presses the row for one master product, which is
   * the only way a suggestion is taken. Requires `jest.useFakeTimers()` in the calling describe.
   */
  const chooseSuggestion = (label: string) => {
    jest.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    fixture.detectChanges();
    const option = Array.from(
      host.querySelectorAll<HTMLButtonElement>('app-product-master-product-picker [role="option"]')
    ).find((candidate) => candidate.textContent?.replace(/\s+/g, ' ').trim() === label);
    expect(option).not.toBeUndefined();
    option!.click();
    fixture.detectChanges();
  };

  /** The note and opt-out the page shows while a master product is selected (issue #1052). */
  const selectionNote = () => host.querySelector<HTMLElement>('[data-testid="product-master-product-selection"]');

  const trackSupplierPricesCheckbox = () =>
    host.querySelector<HTMLInputElement>('[name="trackSupplierPrices"]');

  /**
   * Lets the template-driven form settle: Angular registers a newly rendered `ngModel` control with
   * its form, and writes a programmatic value to the DOM, one microtask after the render. A test
   * that asserts what is on screen, or that interacts with a control that has just appeared, needs
   * that tick; a person cannot press or read faster than it.
   */
  const settleFormBindings = async () => {
    await Promise.resolve();
    fixture.detectChanges();
  };

  /** Ticks or unticks the opt-out the way a person does: the DOM moves, then the form hears about it. */
  const toggleTrackSupplierPrices = async () => {
    await settleFormBindings();
    const checkbox = trackSupplierPricesCheckbox()!;
    checkbox.checked = !checkbox.checked;
    checkbox.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  };

  /** The refused-selection panel a `409` create answers with (issue #1051's codes). */
  const conflictPanel = () => host.querySelector<HTMLElement>('[data-testid="product-master-product-conflict"]');

  const conflictButton = (label: string) =>
    Array.from(conflictPanel()?.querySelectorAll<HTMLButtonElement>('button') ?? []).find(
      (button) => button.textContent?.trim() === label
    );

  const pressConflictButton = (label: string) => {
    const button = conflictButton(label);
    expect(button).not.toBeUndefined();
    button!.click();
    fixture.detectChanges();
  };

  return {
    fixture,
    component: fixture.componentInstance,
    host,
    update,
    create,
    getGstRule,
    setGstRule,
    getPriceTracking,
    setPriceTracking,
    getCategories,
    createCategory,
    chooseGstRule,
    submit,
    gstRuleRetry,
    typeCategory,
    categoryOption,
    createCategoryOption,
    clickCategoryOption,
    getMasterProductSuggestions,
    typeName,
    typeUnit,
    chooseSuggestion,
    selectionNote,
    trackSupplierPricesCheckbox,
    toggleTrackSupplierPrices,
    settleFormBindings,
    conflictPanel,
    conflictButton,
    pressConflictButton,
    navigate
  };
}

afterEach(() => TestBed.resetTestingModule());

describe('ProductFormComponent content width (issue #1111)', () => {
  /**
   * Issue #478 let the old single card fill the content area. The approved layout of issue #1111
   * replaces it with a centred content area about 1,100px wide, holding a two-thirds main column
   * and a one-third settings column from `lg`, one column below it.
   */
  it('centres a content area about 1,100px wide with a two-thirds / one-third grid', async () => {
    const { host } = await render();

    const area = host.querySelector('.page > div');
    expect(Array.from(area?.classList ?? [])).toEqual(expect.arrayContaining(['mx-auto', 'max-w-[1100px]']));

    const grid = host.querySelector('[data-testid="product-form-grid"]')!;
    expect(Array.from(grid.classList)).toEqual(expect.arrayContaining(['grid-cols-1', 'lg:grid-cols-3']));
    expect(host.querySelector('[data-testid="product-form-main"]')?.classList).toContain('lg:col-span-2');
  });
});

describe('ProductFormComponent editing a product this business maintains itself (issue #737)', () => {
  it('shows every field editable, including name and category, and carries no provider-managed note', async () => {
    const { host } = await render();

    const value = (name: string) => (host.querySelector(`[name="${name}"]`) as HTMLInputElement | null)?.value;
    expect(value('name')).toBe('E2E Chips');
    expect(value('sku')).toBe('SNK-001');
    expect(value('description')).toBe('Salted chips');
    expect(value('lowStockThreshold')).toBe('4');
    expect(value('restockTo')).toBe('18');
    expect(value('unit')).toBe('bag');
    expect((host.querySelector('[name="isActive"]') as HTMLInputElement).checked).toBe(true);
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).value).toContain('2');

    // No Supplier control at all (issue #787, A1 of #786): a product carries no supplier, so the
    // form offers none and no field is labelled "Supplier". A supplier is chosen on the purchase or
    // the supplier order instead, and nothing on this page loads the supplier list - the TestBed
    // above provides no SupplierService, so injecting one would fail this render. The supplier
    // price comparison and the GST-rule note still mention suppliers, because both are about the
    // purchases a product was actually bought in, which this change leaves alone.
    expect(host.querySelector('[name="supplierId"]')).toBeNull();
    expect(
      Array.from(host.querySelectorAll('form .field-label')).map((label) => label.textContent?.trim())
    ).not.toContain('Supplier');

    // No Unit Price control either (issue #792, A2 of #786): a product carries no selling price,
    // so the form offers no input for one and no field is labelled "Unit Price ($)". The Initial
    // Unit Cost field on the create form is a cost and is unaffected.
    expect(host.querySelector('[name="unitPrice"]')).toBeNull();
    expect(
      Array.from(host.querySelectorAll('form .field-label')).map((label) => label.textContent?.trim())
    ).not.toContain('Unit Price ($) *');

    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).disabled).toBe(false);
    expect(host.querySelector('.alert-info')).toBeNull();

    expect(host.querySelectorAll('label.field, label.flex').length).toBeGreaterThanOrEqual(7);
  });

  it('keeps the field labels and their inputs together, so a wider card does not separate them', async () => {
    const { host } = await render();

    for (const name of ['sku', 'description', 'categoryId', 'lowStockThreshold', 'restockTo', 'unit']) {
      const control = host.querySelector(`[name="${name}"]`);
      expect(control?.closest('label')?.querySelector('.field-label')).not.toBeNull();
    }
  });

  it('refuses to save, and says why, while Restock To is below the Low Stock Threshold', async () => {
    const { fixture, component, host, update, navigate } = await render();

    component.form.lowStockThreshold = 10;
    component.form.restockTo = 4;
    fixture.detectChanges();

    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);

    component.save();

    expect(update).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(component.error).toContain('Restock To must be greater than or equal to the Low Stock Threshold');
  });

  it('refuses to save an empty name', async () => {
    const { component, update, navigate } = await render();

    component.form.name = '   ';
    component.save();

    expect(update).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(component.error).toBe('Product name is required.');
  });

  it('saves the edited values, including the now-editable name and category, and returns to the list', async () => {
    const { component, update, navigate } = await render();

    component.form.name = 'E2E Chips (renamed)';
    component.form.categoryId = 1;
    component.form.sku = 'SNK-002';
    component.form.lowStockThreshold = 6;
    component.form.restockTo = 24;
    component.form.isActive = false;
    component.save();

    expect(update).toHaveBeenCalledWith(7, {
      name: 'E2E Chips (renamed)',
      sku: 'SNK-002',
      description: 'Salted chips',
      lowStockThreshold: 6,
      restockTo: 24,
      unit: 'bag',
      categoryId: 1,
      isActive: false
    });
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('shows the server\'s refusal message and does not navigate when the update is refused', async () => {
    const update = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: 'The selected category does not exist.' })));
    const { fixture, component, host, navigate } = await render({ update });

    component.form.categoryId = 1;
    component.save();
    fixture.detectChanges();

    expect(component.saveProblem).toEqual({
      failed: 'Product details',
      reason: 'The selected category does not exist.',
      saved: [],
      notAttempted: []
    });
    expect(host.querySelector('[data-testid="product-form-save-problem"]')?.textContent).toContain(
      'The selected category does not exist.'
    );
    expect(component.saving).toBe(false);
    expect(component.form.categoryId).toBe(1);
    expect(component.hasUnsavedChanges).toBe(true);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('cancels back to the product list without saving', async () => {
    const { host, update } = await render();

    const cancel = host.querySelector('[data-testid="product-form-cancel"]') as HTMLAnchorElement;
    expect(cancel.getAttribute('href')).toBe('/products');
    expect(update).not.toHaveBeenCalled();
  });
});

describe('ProductFormComponent editing a provider-linked product (issue #737)', () => {
  function providerManagedProduct(): Product {
    return product({ providerManagedFields: ['name', 'categoryId'] });
  }

  it('locks name and category, names Nayax, and leaves the other local fields editable', async () => {
    const { host } = await render({ product: providerManagedProduct() });

    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(true);
    // A provider-managed category is shown read-only (issue #1111) rather than as a disabled
    // dropdown: there is no category control to change at all.
    expect(host.querySelector('[name="categoryId"]')).toBeNull();
    const category = host.querySelector('[name="categoryName"]') as HTMLInputElement;
    expect(category.readOnly).toBe(true);
    expect(category.value).toBe('Snacks');
    expect((host.querySelector('[name="unit"]') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('[name="restockTo"]') as HTMLInputElement).disabled).toBe(false);

    expect(host.querySelector('[data-testid="product-provider-note"]')?.textContent).toContain(
      'Name and category are synced from Nayax.'
    );
  });

  it('shows one Managed by Nayax badge and no per-field ownership badges (issue #1111)', async () => {
    const { host } = await render({ product: providerManagedProduct() });

    expect(host.querySelector('h1')?.textContent?.trim()).toBe('E2E Chips');
    expect(host.querySelectorAll('[data-testid="product-form-provider-badge"]').length).toBe(1);
    expect(host.textContent?.match(/Managed by Nayax/g)?.length).toBe(1);
    expect(host.textContent).not.toMatch(/\bSynced\b/);
    expect(host.querySelector('[data-testid="product-form-status-badge"]')?.textContent?.trim()).toBe('Active');
  });

  it('still saves the editable fields for a provider-linked product', async () => {
    const { component, update, navigate } = await render({ product: providerManagedProduct() });

    component.form.restockTo = 30;
    component.save();

    expect(update).toHaveBeenCalledWith(
      7,
      expect.objectContaining({ name: 'E2E Chips', categoryId: 2, restockTo: 30 })
    );
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });
});

describe('ProductFormComponent creating a product (issue #737)', () => {
  it('renders an empty, fully editable form titled "New Product"', async () => {
    const { host } = await render({ routeParams: {} });

    expect(host.querySelector('h1')?.textContent).toContain('New product');
    // Name is the suggesting combobox here since issue #1052, not a plain textbox; it is still free
    // text and still empty, and its own describe below covers what the suggestions do.
    const name = host.querySelector('app-product-master-product-picker input[role="combobox"]') as HTMLInputElement;
    expect(name.value).toBe('');
    expect(name.readOnly).toBe(false);
    // The category is chosen through the searchable picker here since issue #830, not the dropdown
    // the edit page still uses; its own describe below covers what that control does.
    expect(
      (host.querySelector('app-product-category-picker input[role="combobox"]') as HTMLInputElement).value
    ).toBe('');
    expect(host.querySelector('.alert-info')).toBeNull();
    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).textContent).toContain('Create product');
    expect(host.querySelector('[name="quantityInStock"]')).not.toBeNull();
    expect(host.querySelector('[name="initialUnitCost"]')).not.toBeNull();

    // No price history for a product that does not exist yet. The GST rule panel *is* offered
    // here since issue #829 - the choices it makes are written once the product has an id - and is
    // covered by its own describe below.
    expect(host.querySelector('app-product-price-history')).toBeNull();
  });

  it('creates the product with the entered fields and returns to the list', async () => {
    const { component, create, submit, navigate } = await render({ routeParams: {} });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    component.form.categoryId = 2;
    component.form.quantityInStock = 10;
    component.form.initialUnitCost = 1.1;
    component.form.lowStockThreshold = 2;
    component.form.restockTo = 8;
    submit();

    expect(create).toHaveBeenCalledWith({
      name: 'New Snack',
      sku: null,
      description: null,
      lowStockThreshold: 2,
      restockTo: 8,
      unit: 'bag',
      categoryId: 2,
      isActive: true,
      quantityInStock: 10,
      initialUnitCost: 1.1
    });
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('refuses to create a product with no name', async () => {
    const { component, create } = await render({ routeParams: {} });

    component.form.name = '';
    component.save();

    expect(create).not.toHaveBeenCalled();
    expect(component.error).toBe('Product name is required.');
  });

  it('refuses a negative initial unit cost', async () => {
    const { component, create } = await render({ routeParams: {} });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    component.form.initialUnitCost = -1;
    component.save();

    expect(create).not.toHaveBeenCalled();
    expect(component.error).toBe('Initial unit cost cannot be negative.');
  });

  it('shows the server\'s refusal message and does not navigate when the create is refused', async () => {
    const create = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: 'Product name is required.' })));
    const { component, navigate } = await render({ routeParams: {}, create });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    component.save();

    expect(component.error).toBe('Product name is required.');
    expect(component.saving).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
  });
});

/**
 * Choosing the category while creating a product (issue #830), seen from the page.
 *
 * The searchable list, the filtering, the explicit create and that write's own pending/refusal
 * lifecycle belong to `app-product-category-picker` (the page composition boundary), and
 * `product-category-picker.component.spec.ts` covers them there. What these tests pin is the page's
 * side of it: which control New Product offers, that the edit page is untouched, and that what the
 * picker reports is the id the product is created with - with every other typed field, and the GST
 * rule chosen beside it (issue #829), still intact.
 */
describe('ProductFormComponent category on New Product (issue #830)', () => {
  it('offers the searchable category picker instead of a dropdown, and reads the options once', async () => {
    const { host, getCategories } = await render({ routeParams: {} });

    expect(host.querySelector('app-product-category-picker')).not.toBeNull();
    expect(host.querySelector('[name="categoryId"]')).toBeNull();
    // The picker owns that read; the page makes no second one of its own.
    expect(getCategories).toHaveBeenCalledTimes(1);
  });

  it('leaves the edit page on its dropdown, and a provider-managed category read-only', async () => {
    const managed = await render({ product: product({ providerManagedFields: ['name', 'categoryId'] }) });

    expect(managed.host.querySelector('app-product-category-picker')).toBeNull();
    expect(managed.host.querySelector('[name="categoryId"]')).toBeNull();
    expect((managed.host.querySelector('[name="categoryName"]') as HTMLInputElement).readOnly).toBe(true);
    TestBed.resetTestingModule();

    const { host } = await render();
    expect(host.querySelector('app-product-category-picker')).toBeNull();
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).disabled).toBe(false);
  });

  it('creates the product with the id of the category selected in the picker', async () => {
    const { component, typeCategory, categoryOption, clickCategoryOption, create, submit } = await render({
      routeParams: {}
    });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    typeCategory('sn');
    clickCategoryOption(categoryOption('Snacks')!);
    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: 'New Snack', categoryId: 2 }));
  });

  it('creates the product with no category while none is selected', async () => {
    const { component, create, submit } = await render({ routeParams: {} });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ categoryId: null }));
  });

  it('clears the category back to none through the picker', async () => {
    const { component, typeCategory, categoryOption, clickCategoryOption, create, submit } = await render({
      routeParams: {}
    });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    typeCategory('dri');
    clickCategoryOption(categoryOption('Drinks')!);
    typeCategory('');
    clickCategoryOption(categoryOption('No category')!);
    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ categoryId: null }));
  });

  /**
   * The point of the issue: a missing category is created without leaving the form, and neither
   * that create nor the GST rule chosen beside it (issue #829) costs the other its state.
   */
  it('creates a missing category inline and keeps every other product field and the GST rule', async () => {
    const { component, fixture, host, typeCategory, createCategoryOption, clickCategoryOption, chooseGstRule, createCategory, create, setGstRule, submit, navigate } =
      await render({ routeParams: {}, create: jest.fn(() => of(product({ id: 55 }))) });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    component.form.sku = 'SNK-900';
    component.form.quantityInStock = 12;
    component.form.initialUnitCost = 1.25;
    component.form.lowStockThreshold = 3;
    component.form.restockTo = 9;
    chooseGstRule('Taxable');
    fixture.detectChanges();

    typeCategory('Chocolate');
    clickCategoryOption(createCategoryOption()!);

    expect(createCategory).toHaveBeenCalledWith({ name: 'Chocolate', description: null });
    // Still on the form, with nothing navigated away from and no product created by the category
    // write.
    expect(navigate).not.toHaveBeenCalled();
    expect(create).not.toHaveBeenCalled();
    expect(host.querySelector('h1')?.textContent).toContain('New product');

    submit();

    expect(create).toHaveBeenCalledWith({
      name: 'New Snack',
      sku: 'SNK-900',
      description: null,
      lowStockThreshold: 3,
      restockTo: 9,
      unit: 'bag',
      categoryId: 9,
      isActive: true,
      quantityInStock: 12,
      initialUnitCost: 1.25
    });
    expect(setGstRule).toHaveBeenCalledWith(55, GstClassification.Taxable);
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('keeps the created category selected when the GST rule is chosen afterwards', async () => {
    const { component, typeCategory, createCategoryOption, clickCategoryOption, chooseGstRule, create, setGstRule, submit } =
      await render({ routeParams: {}, create: jest.fn(() => of(product({ id: 55 }))) });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    typeCategory('Chocolate');
    clickCategoryOption(createCategoryOption()!);
    chooseGstRule('GST-free');
    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ categoryId: 9 }));
    expect(setGstRule).toHaveBeenCalledWith(55, GstClassification.GstFree);
  });

  it('keeps the typed product fields when the inline category create is refused', async () => {
    const createCategory = jest.fn(() =>
      throwError(
        () => new HttpErrorResponse({ status: 400, error: 'This business already has a category called "Chocolate".' })
      )
    );
    const { component, host, typeCategory, createCategoryOption, clickCategoryOption, create, submit } = await render({
      routeParams: {},
      createCategory
    });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    component.form.sku = 'SNK-900';
    typeCategory('Chocolate');
    clickCategoryOption(createCategoryOption()!);

    expect(host.querySelector('app-product-category-picker [role="alert"]')?.textContent).toContain(
      'This business already has a category called "Chocolate".'
    );
    expect(component.form.name).toBe('New Snack');
    expect(component.form.sku).toBe('SNK-900');
    expect(component.form.categoryId).toBe('');
    expect(create).not.toHaveBeenCalled();

    // The product itself is still saveable, without a category.
    submit();
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: 'New Snack', categoryId: null }));
  });
});

/**
 * Choosing a product's GST treatment while creating it (issue #829), seen from the page.
 *
 * The product GST rule is its own API resource (`PUT api/products/{id}/gst-rule`) and the product
 * payload is a pinned contract, so creating a product with a rule is two writes: the create, then
 * the rule on the id the create answered. The page makes the create and hands that id to
 * `app-product-create-gst-rule`, which owns the second write, its partial failure and its retry
 * (the page composition boundary); what these tests pin is the outcome a person gets through the
 * composed page - both writes, in order, and a create that is never reported as finished while the
 * rule is unsaved. `product-create-gst-rule.component.spec.ts` covers that component on its own.
 */
describe('ProductFormComponent GST rule on New Product (issue #829)', () => {
  /** The create answers the product it stored, with the id the database assigned it. */
  const createdProduct = () => jest.fn(() => of(product({ id: 55 })));
  const refusedRule = () => jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));

  /** Fills in the one field the create validation requires. */
  function named(component: ProductFormComponent): ProductFormComponent {
    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    return component;
  }

  it('offers the GST choices the edit page offers, through the same panel', async () => {
    const { host, getGstRule } = await render({ routeParams: {} });

    const panel = host.querySelector('app-product-gst-rule');
    expect(panel).not.toBeNull();
    expect(Array.from(panel!.querySelectorAll('option')).map((option) => option.textContent?.trim())).toEqual([
      'No rule',
      'Taxable',
      'GST-free'
    ]);
    expect(panel!.textContent).toContain('takes precedence over any supplier default');
    expect(panel!.textContent).toContain('Applies to future purchases. Existing purchases keep their recorded GST treatment.');
    expect(panel!.textContent).toContain('"No rule" means unclassified, not GST-free.');

    // Nothing is read for a product that does not exist, and no Save is offered before there is a
    // product: the rule is written with the product the page's own control creates.
    expect(getGstRule).not.toHaveBeenCalled();
    expect(
      Array.from(host.querySelectorAll('app-product-create-gst-rule button')).map((button) =>
        button.textContent?.trim()
      )
    ).toEqual([]);
  });

  it('creates the product and then persists the chosen rule before returning to the list', async () => {
    const create = createdProduct();
    const { component, chooseGstRule, submit, setGstRule, navigate } = await render({ routeParams: {}, create });

    chooseGstRule('Taxable');
    named(component);
    submit();

    expect(create).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledWith(55, GstClassification.Taxable);
    expect(navigate).toHaveBeenCalledWith(['/products']);
    expect(component.error).toBe('');
  });

  it('persists GST-free as GST-free, not as the unclassified state', async () => {
    const { component, chooseGstRule, submit, setGstRule } = await render({
      routeParams: {},
      create: createdProduct()
    });

    chooseGstRule('GST-free');
    named(component);
    submit();

    expect(setGstRule).toHaveBeenCalledWith(55, GstClassification.GstFree);
  });

  /**
   * "No rule" is what a created product already carries (`Product.GstRule` starts at
   * `GstRules.None`), so the choice is persisted by the create itself and a second write would say
   * nothing. It must not be sent as GST-free either, which is why this is asserted rather than
   * left to read as an omission.
   */
  it('writes no rule resource when "No rule" is chosen, because a new product carries no rule', async () => {
    const create = createdProduct();
    const { component, chooseGstRule, submit, setGstRule, navigate } = await render({ routeParams: {}, create });

    chooseGstRule('No rule');
    named(component);
    submit();

    expect(create).toHaveBeenCalledTimes(1);
    expect(setGstRule).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('attempts no rule write when the create itself is refused', async () => {
    const create = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: 'The selected category does not exist.' })));
    const { component, chooseGstRule, submit, setGstRule, navigate } = await render({ routeParams: {}, create });

    chooseGstRule('Taxable');
    named(component);
    submit();

    expect(setGstRule).not.toHaveBeenCalled();
    expect(component.createdProductId).toBeNull();
    expect(component.error).toBe('The selected category does not exist.');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('reports a created product whose rule was not saved, and never claims the create finished', async () => {
    const { component, host, chooseGstRule, submit, navigate } = await render({
      routeParams: {},
      create: createdProduct(),
      setGstRule: refusedRule()
    });

    chooseGstRule('Taxable');
    named(component);
    submit();

    expect(navigate).not.toHaveBeenCalled();
    expect(component.saving).toBe(false);
    expect(component.createdProductId).toBe(55);
    expect(host.querySelector('app-product-create-gst-rule')?.textContent).toContain(
      'The product was created, but its GST rule was not saved.'
    );
    // The action bar says so too, and leaving now asks first.
    expect(host.querySelector('[data-testid="product-form-save-state"]')?.textContent).toContain(
      'Product created; GST rule not saved'
    );
    expect(component.hasUnsavedChanges).toBe(true);
  });

  it('retries the rule alone, so a retry never creates a second product', async () => {
    const create = createdProduct();
    const setGstRule = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(undefined));
    const { component, fixture, chooseGstRule, submit, gstRuleRetry, navigate } = await render({
      routeParams: {},
      create,
      setGstRule
    });

    chooseGstRule('Taxable');
    named(component);
    submit();
    expect(navigate).not.toHaveBeenCalled();

    fixture.detectChanges();
    gstRuleRetry()!.click();

    expect(create).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledTimes(2);
    expect(setGstRule).toHaveBeenLastCalledWith(55, GstClassification.Taxable);
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('retries with the rule now on screen, so a corrected choice is the one stored', async () => {
    const setGstRule = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(undefined));
    const { component, fixture, chooseGstRule, submit, gstRuleRetry } = await render({
      routeParams: {},
      create: createdProduct(),
      setGstRule
    });

    chooseGstRule('Taxable');
    named(component);
    submit();

    chooseGstRule('GST-free');
    fixture.detectChanges();
    gstRuleRetry()!.click();

    expect(setGstRule).toHaveBeenLastCalledWith(55, GstClassification.GstFree);
  });

  /**
   * The create has happened, so the page must offer no create control at all: its one primary
   * action now retries the outstanding rule write alone (issue #1111), and the GST card offers no
   * button of its own.
   */
  it('offers the rule retry and no create control once the product exists', async () => {
    const { component, fixture, host, chooseGstRule, submit, gstRuleRetry } = await render({
      routeParams: {},
      create: createdProduct(),
      setGstRule: refusedRule()
    });

    chooseGstRule('Taxable');
    named(component);
    submit();

    fixture.detectChanges();
    expect(
      Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim())
    ).not.toContain('Create product');
    expect(host.querySelectorAll('app-product-create-gst-rule button')).toHaveLength(0);
    const retry = gstRuleRetry();
    expect(retry).not.toBeNull();
    expect(retry?.disabled).toBe(false);
  });

  /**
   * The product is already stored, so the create form's own field rules are no longer being
   * written and must not be able to trap the outstanding rule on the page.
   */
  it('keeps the retry available while the form fields are edited into an invalid state', async () => {
    const setGstRule = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(undefined));
    const create = createdProduct();
    const { component, fixture, chooseGstRule, submit, gstRuleRetry, navigate } = await render({
      routeParams: {},
      create,
      setGstRule
    });

    chooseGstRule('Taxable');
    named(component);
    submit();

    component.form.lowStockThreshold = 10;
    component.form.restockTo = 4;
    component.form.name = '   ';
    fixture.detectChanges();

    expect(gstRuleRetry()?.disabled).toBe(false);

    gstRuleRetry()!.click();

    expect(create).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledTimes(2);
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('writes neither the product nor the rule while a support session is active', async () => {
    const create = createdProduct();
    const { component, chooseGstRule, submit, setGstRule } = await render({
      routeParams: {},
      create,
      sessionActive: of(true)
    });

    chooseGstRule('Taxable');
    named(component);
    submit();

    // No create at all, so there is never a product whose rule could be written either. The retry
    // the rule component offers is refused during a session too, which its own spec covers.
    expect(create).not.toHaveBeenCalled();
    expect(setGstRule).not.toHaveBeenCalled();
    expect(component.createdProductId).toBeNull();
  });

  it('offers no Save GST rule button anywhere on the edit page (issue #1111)', async () => {
    const { host } = await render();

    expect(host.querySelector('app-product-create-gst-rule')).toBeNull();
    expect(host.querySelectorAll('app-product-gst-rule button')).toHaveLength(0);
    expect(
      Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim())
    ).not.toContain('Save GST rule');
  });
});

/**
 * How New and Edit Product are laid out (issue #1111's approved design, replacing issue #892's single
 * card).
 *
 * A main column - Product details, Opening inventory (New only), Reordering - and a settings column
 * - Product status, GST rule, Supplier price tracking - in that DOM order, which is also the reading,
 * Tab and narrow-screen stacking order. One sticky action bar holds Cancel and the one primary
 * action, which submits the product form by naming it, so there is still exactly one submit path.
 */
describe('ProductFormComponent layout (issue #1111)', () => {
  const headings = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('h2')).map((heading) => heading.textContent?.trim());

  const section = (host: HTMLElement, name: string) =>
    host.querySelector<HTMLElement>(`[data-testid="product-section-${name}"]`)!;

  /** The field grid of one section, which is the only grid a section has. */
  const grid = (host: HTMLElement, name: string) => section(host, name).querySelector<HTMLElement>('.grid')!;

  /** The controls of a container in DOM order, named by `name` or by composed picker. */
  const controlNames = (container: HTMLElement) =>
    Array.from(
      container.querySelectorAll('app-product-category-picker, app-product-master-product-picker, [name]')
    )
      .filter((element) => !element.closest('app-product-category-picker, app-product-master-product-picker') || element.tagName.startsWith('APP-'))
      .map((element) => element.getAttribute('name') ?? element.tagName.toLowerCase());

  it('orders New Product as the main column, then the settings column', async () => {
    const { host } = await render({ routeParams: {} });

    expect(headings(host)).toEqual([
      'Product details',
      'Opening inventory',
      'Reordering',
      'Product status',
      'GST rule',
      'Supplier price tracking'
    ]);
    expect(host.querySelectorAll('app-product-create-gst-rule')).toHaveLength(1);
    expect(host.querySelectorAll('app-product-gst-rule')).toHaveLength(1);
  });

  it('orders Edit Product the same way, with no Opening inventory and no price comparison', async () => {
    const { host } = await render();

    expect(headings(host)).toEqual(['Product details', 'Reordering', 'Product status', 'GST rule', 'Supplier price tracking']);
    expect(host.querySelector('[data-testid="product-section-opening-inventory"]')).toBeNull();
    expect(host.querySelector('[name="quantityInStock"]')).toBeNull();
    expect(host.querySelector('[name="initialUnitCost"]')).toBeNull();
  });

  it('keeps Name first and full width, then Category, SKU, Unit Label and Description', async () => {
    const created = await render({ routeParams: {} });
    expect(controlNames(grid(created.host, 'product-details'))).toEqual([
      'app-product-master-product-picker',
      'app-product-category-picker',
      'sku',
      'unit',
      'description'
    ]);
    TestBed.resetTestingModule();

    const { host } = await render();
    expect(controlNames(grid(host, 'product-details'))).toEqual(['name', 'categoryId', 'sku', 'unit', 'description']);
    expect(host.querySelector('[name="name"]')!.closest('.sm\\:col-span-2')).not.toBeNull();
    expect(host.querySelector('[name="description"]')!.closest('label')!.classList).toContain('sm:col-span-2');
  });

  it('puts Opening Stock beside Initial Unit Cost, keeping their defaults', async () => {
    const { host } = await render({ routeParams: {} });

    const opening = grid(host, 'opening-inventory');
    expect(controlNames(opening)).toEqual(['quantityInStock', 'initialUnitCost']);
    expect(Array.from(opening.classList)).toEqual(expect.arrayContaining(['sm:grid-cols-2', 'items-start']));
    await Promise.resolve();
    expect((host.querySelector('[name="quantityInStock"]') as HTMLInputElement).value).toBe('0');
    expect((host.querySelector('[name="initialUnitCost"]') as HTMLInputElement).value).toBe('');
  });

  it('puts Low Stock Threshold beside Restock To, with guidance worded from the reorder rule', async () => {
    const { fixture, component, host } = await render({ routeParams: {} });

    const reordering = grid(host, 'reordering');
    expect(controlNames(reordering)).toEqual(['lowStockThreshold', 'restockTo']);
    expect(Array.from(reordering.querySelectorAll('.field-hint')).map((hint) => hint.textContent?.trim())).toEqual([
      'A reorder is suggested at or below this projected warehouse stock.',
      'Target warehouse stock after machines are refilled and the order is received.'
    ]);

    component.form.lowStockThreshold = 4;
    component.form.restockTo = 12;
    fixture.detectChanges();
    const guidance = host.querySelector('[data-testid="product-reorder-guidance"]')!.textContent!;
    expect(guidance).toContain('at or below 4 units');
    expect(guidance).toContain('back to 12 units');

    component.form.lowStockThreshold = 10;
    component.form.restockTo = 4;
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="product-reorder-guidance"]')).toBeNull();
    const message = section(host, 'reordering').querySelector('.field-error');
    expect(message?.textContent).toContain('Restock To must be greater than or equal to the Low Stock Threshold');
  });

  it('puts the Active product switch in the Product status card with its history sentence', async () => {
    const { host } = await render({ routeParams: {} });

    const status = section(host, 'status');
    const active = status.querySelector<HTMLInputElement>('[name="isActive"]')!;
    expect(active.getAttribute('role')).toBe('switch');
    expect(active.checked).toBe(true);
    expect(active.closest('label')?.textContent).toContain('Active product');
    expect(status.textContent).toContain('Inactive products keep their history and are excluded from reorder alerts.');
  });

  /**
   * The action bar is outside the form because the settings cards own form controls of their own,
   * and nesting one form in another is not markup a browser keeps. It submits the product form by
   * naming it, so there is still exactly one submit path.
   */
  it('creates the product from the sticky action bar, through the form that bar names', async () => {
    const { fixture, component, host, create, setGstRule, navigate } = await render({
      routeParams: {},
      create: jest.fn(() => of(product({ id: 55 })))
    });

    const form = host.querySelector('form')!;
    const actions = host.querySelector('[data-testid="product-form-actions"]')!;
    const submit = actions.querySelector<HTMLButtonElement>('button[type="submit"]')!;
    expect(Array.from(actions.classList)).toContain('sticky');
    expect(submit.textContent?.trim()).toBe('Create product');
    expect(submit.getAttribute('form')).toBe(form.id);
    expect(form.contains(submit)).toBe(false);
    expect(form.querySelector('app-product-create-gst-rule')).toBeNull();
    expect(host.querySelectorAll('form')).toHaveLength(1);
    expect(Array.from(actions.querySelectorAll('button, a')).map((control) => control.textContent?.trim())).toEqual([
      'Cancel',
      'Create product'
    ]);

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    fixture.detectChanges();
    submit.click();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledTimes(1);
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: 'New Snack' }));
    expect(setGstRule).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('names the edit page’s primary action Save changes, with Cancel back to the list', async () => {
    const { host } = await render();

    const actions = host.querySelector('[data-testid="product-form-actions"]')!;
    expect(Array.from(actions.querySelectorAll('button, a')).map((control) => control.textContent?.trim())).toEqual([
      'Cancel',
      'Save changes'
    ]);
    expect(actions.querySelector('a')?.getAttribute('href')).toBe('/products');
    expect(host.querySelector('[data-testid="product-form-back"]')?.getAttribute('href')).toBe('/products');
  });

  it('leaves the category picker’s own buttons unable to submit the product', async () => {
    const { host, typeCategory, categoryOption, clickCategoryOption, create } = await render({ routeParams: {} });

    typeCategory('sn');
    expect(
      Array.from(host.querySelectorAll('app-product-category-picker button')).every(
        (button) => button.getAttribute('type') === 'button'
      )
    ).toBe(true);

    clickCategoryOption(categoryOption('Snacks')!);

    expect(create).not.toHaveBeenCalled();
    expect(host.querySelector('h1')?.textContent).toContain('New product');
  });

  it('keeps Cancel on the product list and turns Create into the rule retry once the product exists', async () => {
    const { fixture, host, component, chooseGstRule, submit, gstRuleRetry } = await render({
      routeParams: {},
      create: jest.fn(() => of(product({ id: 55 }))),
      setGstRule: jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })))
    });

    const actions = host.querySelector('[data-testid="product-form-actions"]')!;
    expect(actions.querySelector<HTMLAnchorElement>('a')?.getAttribute('href')).toBe('/products');

    chooseGstRule('Taxable');
    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    submit();
    fixture.detectChanges();

    expect(gstRuleRetry()).not.toBeNull();
    expect(actions.querySelectorAll('button[type="submit"]')).toHaveLength(1);
    expect(actions.querySelector<HTMLAnchorElement>('a')?.getAttribute('href')).toBe('/products');
  });

  it('keeps fields clear of the sticky action bar when they are focused', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="product-form-grid"]')?.className).toContain('[&_input]:scroll-mb-40');
  });
});

/**
 * Save while a support session is active (issue #554). A session grants `InventoryOperate`, so
 * this control would otherwise still render enabled.
 */
describe('ProductFormComponent write control during a support session (issue #554)', () => {
  it('disables Save while a session is active', async () => {
    const { host } = await render({ sessionActive: of(true) });

    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('does not write the edit while a session is active, even if save() is invoked directly', async () => {
    const { component, update, navigate } = await render({ sessionActive: of(true) });

    component.save();

    expect(update).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('leaves Save enabled while no session is active', async () => {
    const { host } = await render({ sessionActive: of(false) });

    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(false);
  });
});

/**
 * Issue #825 put the server's deletion refusal under the Edit Product header. Issue #1111's approved
 * design removes it: Edit Product shows no permanent deletion warning, and the Product status card's
 * own sentence is what explains deactivation. Deleting is still decided by the server, on the
 * Products list, exactly as before.
 */
describe('ProductFormComponent without a deletion notice (issue #1111)', () => {
  const historyRefusal =
    'This product cannot be deleted because its history refers to it (2 stock movements, 1 sale). ' +
    'Deactivate it instead - a deactivated product keeps its history, stays out of the reorder ' +
    'alerts and can be reactivated. Nothing was deleted.';

  it('shows no deletion warning for a product the server would refuse to delete', async () => {
    const { host } = await render({ product: product({ canDelete: false, deletionRefusal: historyRefusal }) });

    expect(host.querySelector('.alert-warning')).toBeNull();
    expect(host.textContent).not.toContain('cannot be deleted');
    expect(host.textContent).toContain('Inactive products keep their history and are excluded from reorder alerts.');
  });

  it('still saves an undeletable product', async () => {
    const { component, update, navigate } = await render({
      product: product({ canDelete: false, deletionRefusal: historyRefusal })
    });

    component.form.sku = 'SNK-009';
    component.save();

    expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ name: 'E2E Chips', sku: 'SNK-009' }));
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });
});

describe('ProductFormComponent requiring a Unit Label (issue #895)', () => {
  /** Every `.field-error` paragraph on the page - the page's own error and the inline field messages. */
  const errors = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('.field-error')).map((paragraph) => paragraph.textContent?.trim());

  const unitInput = (host: HTMLElement) => host.querySelector('[name="unit"]') as HTMLInputElement;

  const unitLabelText = (host: HTMLElement) =>
    unitInput(host).closest('label')?.querySelector('.field-label')?.textContent?.trim();

  it('marks Unit Label required on Edit Product, with the field accessibly labelled', async () => {
    const { host } = await render();

    expect(unitInput(host).required).toBe(true);
    expect(unitLabelText(host)).toBe('Unit Label *');
    expect(errors(host)).not.toContain('Unit label is required.');
  });

  it('refuses to save an edited product whose Unit Label was cleared, and says why', async () => {
    const { fixture, component, host, update, navigate } = await render();

    component.form.unit = '   ';
    fixture.detectChanges();

    expect(errors(host)).toContain('Unit label is required.');
    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);

    component.save();

    expect(update).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(component.error).toBe('Unit label is required.');
  });

  /**
   * The prefill issue #895 removed: a legacy product whose stored label is missing used to open with
   * "unit" substituted, so an operator saved a label they never chose. The field opens empty and
   * required instead, and nothing is written until they choose one.
   */
  it.each([[null], ['']])('opens a product whose stored label is %p empty and required', async (unit) => {
    const { component, host, update } = await render({ product: product({ unit }) });

    expect(component.form.unit).toBe('');
    expect(unitInput(host).value).toBe('');
    expect(unitInput(host).required).toBe(true);
    expect(errors(host)).toContain('Unit label is required.');

    component.save();

    expect(update).not.toHaveBeenCalled();
    expect(component.error).toBe('Unit label is required.');
  });

  it('gives a legacy product the label the operator chose, trimmed', async () => {
    const { component, update, navigate } = await render({ product: product({ unit: null }) });

    component.form.unit = '  bottle  ';
    component.save();

    expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ unit: 'bottle' }));
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  /**
   * New Product opens with Unit Label empty rather than prefilled with "unit", so a product is never
   * created with a label the operator did not choose: an untouched field is refused, not saved.
   */
  it('opens Unit Label empty on New Product, marks it required and refuses it untouched', async () => {
    const { component, host, create } = await render({ routeParams: {} });

    expect(component.form.unit).toBe('');
    expect(unitInput(host).value).toBe('');
    expect(unitInput(host).placeholder).toBe('e.g. 375 mL, 45 g, pouch, can');
    expect(unitInput(host).required).toBe(true);
    expect(unitLabelText(host)).toBe('Unit Label *');

    component.form.name = 'New Snack';
    component.save();

    expect(create).not.toHaveBeenCalled();
    expect(component.error).toBe('Unit label is required.');
  });

  it('refuses to create a product with no Unit Label', async () => {
    const { fixture, component, host, create } = await render({ routeParams: {} });

    component.form.name = 'New Snack';
    component.form.unit = '';
    fixture.detectChanges();

    expect(errors(host)).toContain('Unit label is required.');
    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);

    component.save();

    expect(create).not.toHaveBeenCalled();
    expect(component.error).toBe('Unit label is required.');
  });

  it('creates the product with the entered label, trimmed', async () => {
    const { component, create } = await render({ routeParams: {} });

    component.form.name = 'New Snack';
    component.form.unit = '  bag  ';
    component.save();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ unit: 'bag' }));
  });
});

/**
 * The "Track supplier prices" switch on the product form (issue #938). The switch's own behaviour -
 * the write, the unit-label message and the five statuses - is `ProductPriceTrackingComponent`'s
 * spec; what this page is responsible for is mounting it for the product being edited, and saying
 * where to find it on New Product.
 */
describe('ProductFormComponent supplier price tracking (issue #938)', () => {
  it('mounts the switch for the product being edited, and reads that product', async () => {
    const { host, getPriceTracking } = await render();

    expect(host.querySelector('[data-testid="product-price-tracking"]')).not.toBeNull();
    expect(getPriceTracking).toHaveBeenCalledWith(7);
  });

  /**
   * New Product offers no switch: there is no product yet to track, and the search text is read from
   * the stored product rather than from this form - so the switch would either write nothing or
   * capture a name that is not stored. The note is what says where it is instead.
   */
  it('offers no switch on New Product, and says where it is', async () => {
    const { host, getPriceTracking } = await render({ routeParams: {} });

    expect(host.querySelector('[data-testid="product-price-tracking"]')).toBeNull();
    expect(getPriceTracking).not.toHaveBeenCalled();
    expect(
      host.querySelector('[data-testid="product-price-tracking-after-create"]')?.textContent
    ).toContain("switch tracking on from this product's Edit page once it has been created");
  });

  /**
   * The panel reads and writes its own resource, so the product form's Save is untouched by it: an
   * edit still sends exactly the catalogue fields it always did, with no tracking value among them.
   */
  it('leaves the product update carrying no tracking value', async () => {
    const { component, update } = await render();

    component.form.sku = 'SNK-002';
    component.save();

    expect(update).toHaveBeenCalledWith(7, expect.not.objectContaining({ tracked: expect.anything() }));
  });
});

/**
 * Creating a product from a master product suggestion (issue #1052), seen from the page.
 *
 * The suggestion list itself - the debounced lookup, the superseded response, the loading, empty and
 * failure states and the combobox keyboard - belongs to `app-product-master-product-picker` (the
 * page composition boundary) and is covered by its own spec. What these tests pin is the page's
 * side: what a selection fills in, when the selection is dropped, what is actually submitted, and
 * the two ways on from the create's `409`.
 */
describe('ProductFormComponent master product suggestions on New Product (issue #1052)', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('offers the suggesting Name field on New Product and leaves the edit page a plain textbox', async () => {
    const createMode = await render({ routeParams: {} });

    expect(createMode.host.querySelector('app-product-master-product-picker')).not.toBeNull();
    expect(
      (createMode.host.querySelector('app-product-master-product-picker input[role="combobox"]') as HTMLInputElement)
        .value
    ).toBe('');
    // Nothing is read until something is typed: the lookup is the field's, not the page's load.
    expect(createMode.getMasterProductSuggestions).not.toHaveBeenCalled();

    TestBed.resetTestingModule();
    const editMode = await render();

    expect(editMode.host.querySelector('app-product-master-product-picker')).toBeNull();
    expect((editMode.host.querySelector('[name="name"]') as HTMLInputElement).value).toBe('E2E Chips');
  });

  it('fills Name and Unit label from the suggestion, says prices will be tracked, and sends the selection', async () => {
    const {
      component,
      host,
      typeName,
      chooseSuggestion,
      selectionNote,
      trackSupplierPricesCheckbox,
      settleFormBindings,
      submit,
      create,
      navigate
    } = await render({ routeParams: {} });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    await settleFormBindings();

    expect(component.form.name).toBe("CC's Tasty Cheese Corn Chips");
    expect(component.form.unit).toBe('45g');
    expect((host.querySelector('app-product-master-product-picker input[role="combobox"]') as HTMLInputElement).value).toBe(
      "CC's Tasty Cheese Corn Chips"
    );
    expect((host.querySelector('[name="unit"]') as HTMLInputElement).value).toBe('45g');

    // The note is the operator's warning that this does something beyond naming the product, and
    // its checkbox is the opt-out, on by default.
    expect(selectionNote()?.textContent).toContain('Selected match');
    expect(host.querySelector('[data-testid="product-new-tracking-match"]')?.textContent).toContain(
      'supplier prices are tracked from the moment the product is created'
    );
    expect(trackSupplierPricesCheckbox()?.checked).toBe(true);

    submit();

    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({
        name: "CC's Tasty Cheese Corn Chips",
        unit: '45g',
        selectedMasterProductId: 12,
        trackSupplierPrices: true
      })
    );
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('tells two sizes of one product apart and sends the one that was taken', async () => {
    const { component, typeName, chooseSuggestion, submit, create } = await render({
      routeParams: {},
      getMasterProductSuggestions: jest.fn(() =>
        of({ products: [masterProductSuggestion(), partySizeSuggestion], page: 1, pageSize: 10, hasMore: false })
      )
    });

    typeName('cheese corn');
    chooseSuggestion(PARTY_SUGGESTION_LABEL);

    expect(component.form.unit).toBe('175g');
    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ selectedMasterProductId: 13, trackSupplierPrices: true }));
  });

  /**
   * A Name nobody picked is still a Name: the payload is then exactly the one every create sent
   * before this issue, with neither master-product field on it at all.
   */
  it('creates a typed Name with no selection and no master-product field in the payload', async () => {
    const { component, typeName, selectionNote, submit, create } = await render({ routeParams: {} });

    typeName('Hand typed snack');
    jest.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    component.form.unit = 'bag';
    submit();

    expect(selectionNote()).toBeNull();
    expect(create).toHaveBeenCalledWith({
      name: 'Hand typed snack',
      sku: null,
      description: null,
      lowStockThreshold: 5,
      restockTo: 5,
      unit: 'bag',
      categoryId: null,
      isActive: true,
      quantityInStock: 0,
      initialUnitCost: null
    });
  });

  it('drops the selection when the Name is edited away from it, and typing it back does not revive it', async () => {
    const { component, typeName, chooseSuggestion, selectionNote, submit, create } = await render({ routeParams: {} });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    typeName("CC's Tasty Cheese Corn Chips (party)");

    expect(selectionNote()).toBeNull();

    // Typing the prefill back by hand is not choosing the product again: the link rests on a
    // selection, and that selection is gone.
    typeName("CC's Tasty Cheese Corn Chips");
    submit();

    expect(create).toHaveBeenCalledWith(expect.not.objectContaining({ selectedMasterProductId: expect.anything() }));
    expect(create).toHaveBeenCalledWith(expect.not.objectContaining({ trackSupplierPrices: expect.anything() }));
    expect(component.form.name).toBe("CC's Tasty Cheese Corn Chips");
  });

  it('keeps the selection through the differences the server does not call an edit', async () => {
    const { typeName, chooseSuggestion, typeUnit, selectionNote, submit, create } = await render({ routeParams: {} });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    typeName("  cc's  Tasty Cheese Corn Chips ");
    typeUnit(' 45G ');

    expect(selectionNote()).not.toBeNull();
    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ selectedMasterProductId: 12 }));
  });

  it('drops the selection when the Unit label is edited away from it', async () => {
    const { typeName, chooseSuggestion, typeUnit, selectionNote, submit, create } = await render({ routeParams: {} });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    // The spacing the prefill never produces is a different size, which the server calls a change.
    typeUnit('45 g');

    expect(selectionNote()).toBeNull();
    submit();

    expect(create).toHaveBeenCalledWith(expect.not.objectContaining({ selectedMasterProductId: expect.anything() }));
  });

  it('starts a fresh selection when another suggestion is taken, with the opt-out back on', async () => {
    const {
      component,
      typeName,
      chooseSuggestion,
      trackSupplierPricesCheckbox,
      toggleTrackSupplierPrices,
      settleFormBindings,
      host,
      submit,
      create
    } = await render({
      routeParams: {},
      getMasterProductSuggestions: jest.fn(() =>
        of({ products: [masterProductSuggestion(), partySizeSuggestion], page: 1, pageSize: 10, hasMore: false })
      )
    });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);

    await toggleTrackSupplierPrices();
    expect(trackSupplierPricesCheckbox()?.checked).toBe(false);

    // Choosing again refills both values and is a new selection, so the opt-out starts over.
    typeName('cheese corn');
    chooseSuggestion(PARTY_SUGGESTION_LABEL);
    await settleFormBindings();

    expect(component.form.unit).toBe('175g');
    expect(trackSupplierPricesCheckbox()?.checked).toBe(true);
    expect((host.querySelector('[name="unit"]') as HTMLInputElement).value).toBe('175g');

    submit();
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ selectedMasterProductId: 13, trackSupplierPrices: true }));
  });

  it('sends trackSupplierPrices false when the opt-out is unticked, and nothing else changes', async () => {
    const { typeName, chooseSuggestion, toggleTrackSupplierPrices, submit, create } = await render({
      routeParams: {}
    });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    await toggleTrackSupplierPrices();

    submit();

    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({
        name: "CC's Tasty Cheese Corn Chips",
        unit: '45g',
        selectedMasterProductId: 12,
        trackSupplierPrices: false
      })
    );
  });

  it('leaves the selection alone when another field, or the GST rule, is changed', async () => {
    const { component, typeName, chooseSuggestion, typeCategory, categoryOption, clickCategoryOption, chooseGstRule, selectionNote, submit, create } =
      await render({ routeParams: {} });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);

    component.form.sku = 'SNK-009';
    component.form.quantityInStock = 4;
    component.form.initialUnitCost = 1.5;
    component.form.isActive = false;
    typeCategory('Snacks');
    clickCategoryOption(categoryOption('Snacks')!);
    chooseGstRule('Taxable');

    expect(selectionNote()).not.toBeNull();
    submit();

    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({ sku: 'SNK-009', categoryId: 2, selectedMasterProductId: 12, trackSupplierPrices: true })
    );
  });

  it.each([
    [MASTER_PRODUCT_UNAVAILABLE_CODE, 'The product you chose is no longer in the shared catalogue.'],
    [MASTER_PRODUCT_CHANGED_CODE, 'The Name and unit label no longer match the product you chose.'],
    [MASTER_PRODUCT_REFUSED_CODE, 'The product you chose has been ruled out for this Name and unit label.']
  ])('shows the server\'s %s refusal with both ways on, and creates nothing', async (code, message) => {
    const create = jest.fn(() => throwError(() => selectionConflict(code, message)));
    const { component, typeName, chooseSuggestion, conflictPanel, conflictButton, submit, navigate } = await render({
      routeParams: {},
      create
    });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    submit();

    expect(conflictPanel()?.textContent).toContain(message);
    expect(conflictButton('Choose again')).not.toBeUndefined();
    expect(conflictButton('Create without link')).not.toBeUndefined();
    // The page says the refusal once, in the server's words, and offers the two actions beside it.
    expect(component.error).toBe('');
    expect(component.saving).toBe(false);
    expect(component.createdProductId).toBeNull();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('clears the refusal and the selection for "Choose again", without creating anything', async () => {
    const create = jest.fn(() => throwError(() => selectionConflict(MASTER_PRODUCT_CHANGED_CODE, 'Choose it again.')));
    const { component, typeName, chooseSuggestion, selectionNote, conflictPanel, pressConflictButton, submit } =
      await render({ routeParams: {}, create });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    submit();
    pressConflictButton('Choose again');

    expect(conflictPanel()).toBeNull();
    expect(selectionNote()).toBeNull();
    expect(create).toHaveBeenCalledTimes(1);
    // The typed values stay: choosing again is the operator picking the product, not the form
    // throwing their work away.
    expect(component.form.name).toBe("CC's Tasty Cheese Corn Chips");
    expect(component.form.unit).toBe('45g');
  });

  it('resubmits the same product with no selection for "Create without link"', async () => {
    const create = jest
      .fn()
      .mockReturnValueOnce(throwError(() => selectionConflict(MASTER_PRODUCT_REFUSED_CODE, 'Ruled out.')))
      .mockReturnValueOnce(of(product({ id: 55 })));
    const { component, typeName, chooseSuggestion, conflictPanel, pressConflictButton, submit, navigate } =
      await render({ routeParams: {}, create });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    submit();
    pressConflictButton('Create without link');

    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[1][0]).toEqual({
      name: "CC's Tasty Cheese Corn Chips",
      sku: null,
      description: null,
      lowStockThreshold: 5,
      restockTo: 5,
      unit: '45g',
      categoryId: null,
      isActive: true,
      quantityInStock: 0,
      initialUnitCost: null
    });
    expect(conflictPanel()).toBeNull();
    // Created and finished: the rule needed no write, so nothing is outstanding.
    expect(component.createdProductId).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  /** A refusal that is not one of the three codes is still the ordinary create failure. */
  it('shows an ordinary refusal as the page always did, with no choose-again panel', async () => {
    const create = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: 'Unit label is required.' }))
    );
    const { component, typeName, chooseSuggestion, conflictPanel, submit } = await render({ routeParams: {}, create });

    typeName('cheese corn');
    chooseSuggestion(SUGGESTION_LABEL);
    submit();

    expect(component.error).toBe('Unit label is required.');
    expect(conflictPanel()).toBeNull();
  });

  /**
   * A failed lookup is the shared catalogue being unreachable, which says nothing about the product
   * this business is creating: the Name is typed, nothing is disabled and the create still happens.
   */
  it('still creates the product when the suggestion lookup fails', async () => {
    const { host, typeName, typeUnit, submit, create, navigate } = await render({
      routeParams: {},
      getMasterProductSuggestions: jest.fn(() => throwError(() => new Error('lookup unavailable')))
    });

    typeName('Hand typed snack');
    jest.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    typeUnit('bag');

    expect(host.querySelector('[data-testid="master-product-suggestions-error"]')).not.toBeNull();
    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(false);

    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: 'Hand typed snack', unit: 'bag' }));
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });
});

/**
 * One primary action for every editable section (issue #1111). Edit Product's Save changes writes
 * the product, then its GST rule, then its supplier price tracking - only what changed, in that
 * order, stopping at the first failure - and says exactly what was and was not saved.
 */
describe('ProductFormComponent single save orchestration (issue #1111)', () => {
  const trackingState = (tracked: boolean) =>
    jest.fn(() =>
      of({
        productId: 7,
        tracked,
        status: null,
        searchName: null,
        unitLabel: null,
        masterProduct: null,
        canTrack: true,
        trackRefusal: null
      })
    );

  /** Turns the tracking switch the way a person does. */
  const toggleTracking = (host: HTMLElement) => {
    const toggle = host.querySelector<HTMLInputElement>('[data-testid="price-tracking-switch"]')!;
    toggle.checked = !toggle.checked;
    toggle.dispatchEvent(new Event('change'));
  };

  it('writes nothing and returns to the list when nothing changed', async () => {
    const { component, update, setGstRule, setPriceTracking, navigate } = await render();

    expect(component.hasUnsavedChanges).toBe(false);
    component.save();

    expect(update).not.toHaveBeenCalled();
    expect(setGstRule).not.toHaveBeenCalled();
    expect(setPriceTracking).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('saves only a changed GST rule, with no product update and no Save GST rule button', async () => {
    const { fixture, component, chooseGstRule, update, setGstRule, navigate } = await render();

    chooseGstRule('GST-free');
    expect(component.hasUnsavedChanges).toBe(true);
    component.save();
    fixture.detectChanges();

    expect(update).not.toHaveBeenCalled();
    expect(setGstRule).toHaveBeenCalledWith(7, GstClassification.GstFree);
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('writes the product, then the rule, then tracking, when all three changed', async () => {
    const calls: string[] = [];
    const update = jest.fn(() => {
      calls.push('product');
      return of(undefined);
    });
    const setGstRule = jest.fn(() => {
      calls.push('gst');
      return of(undefined);
    });
    const setPriceTracking = jest.fn(() => {
      calls.push('tracking');
      return of(undefined);
    });
    const { fixture, component, host, chooseGstRule, navigate } = await render({
      update,
      setGstRule,
      setPriceTracking,
      getPriceTracking: trackingState(false)
    });

    component.form.restockTo = 30;
    chooseGstRule('Taxable');
    toggleTracking(host);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="product-form-save-state"]')?.textContent).toContain('Unsaved changes');

    component.save();
    fixture.detectChanges();

    expect(calls).toEqual(['product', 'gst', 'tracking']);
    expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ restockTo: 30 }));
    expect(setGstRule).toHaveBeenCalledWith(7, GstClassification.Taxable);
    expect(setPriceTracking).toHaveBeenCalledWith(7, true);
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('reports a partial save truthfully, keeps the values, and retries only what is still unsaved', async () => {
    const setGstRule = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValue(of(undefined));
    const { fixture, component, host, chooseGstRule, update, setPriceTracking, navigate } = await render({
      setGstRule,
      getPriceTracking: trackingState(false)
    });

    component.form.sku = 'SNK-777';
    chooseGstRule('Taxable');
    toggleTracking(host);
    fixture.detectChanges();

    component.save();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledTimes(1);
    expect(setPriceTracking).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(component.saving).toBe(false);
    expect(component.saveProblem).toEqual({
      failed: 'GST rule',
      reason: 'Failed to save the GST rule.',
      saved: ['Product details'],
      notAttempted: ['Supplier price tracking']
    });
    const banner = host.querySelector('[data-testid="product-form-save-problem"]')!.textContent!;
    expect(banner).toContain('Some changes were not saved.');
    expect(banner).toContain('Saved: Product details.');
    expect(banner).toContain('Not saved: GST rule, Supplier price tracking.');
    // Nothing on screen is lost, and the saved section is no longer counted as unsaved.
    expect(component.form.sku).toBe('SNK-777');
    expect(component.productFieldsDirty).toBe(false);
    expect(component.hasUnsavedChanges).toBe(true);

    component.save();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledTimes(2);
    expect(setPriceTracking).toHaveBeenCalledWith(7, true);
    expect(component.saveProblem).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('stops at a failed product update and attempts neither the rule nor tracking', async () => {
    const update = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: 'Unit label is required.' })));
    const { fixture, component, chooseGstRule, setGstRule } = await render({ update });

    component.form.description = 'Changed';
    chooseGstRule('Taxable');
    component.save();
    fixture.detectChanges();

    expect(setGstRule).not.toHaveBeenCalled();
    expect(component.saveProblem).toEqual({
      failed: 'Product details',
      reason: 'Unit label is required.',
      saved: [],
      notAttempted: ['GST rule']
    });
  });

  it('protects against a duplicate submit while a save is in flight', async () => {
    const pending = new Subject<void>();
    const update = jest.fn(() => pending.asObservable());
    const { fixture, component, host, navigate } = await render({ update });

    component.form.sku = 'SNK-100';
    component.save();
    fixture.detectChanges();
    component.save();

    const submit = host.querySelector<HTMLButtonElement>('[data-testid="product-form-submit"]')!;
    expect(update).toHaveBeenCalledTimes(1);
    expect(submit.disabled).toBe(true);
    expect(submit.textContent?.trim()).toBe('Saving...');
    expect(component.leaveWarning()).toBe(PRODUCT_SAVE_IN_FLIGHT_LEAVE_WARNING);

    pending.next();
    pending.complete();
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  /** Every product control of the page, the composed pickers' and the settings switches' included. */
  const productControls = (host: HTMLElement) =>
    Array.from(
      host.querySelectorAll<HTMLElement>(
        '[data-testid="product-form-main"] :is(input, select, textarea, button), input[name="isActive"], input[name="trackSupplierPrices"]'
      )
    );

  it('holds every product field still while an edit save is in flight, so no later edit is dropped', async () => {
    const pending = new Subject<void>();
    const update = jest.fn(() => pending.asObservable());
    const { fixture, component, host, navigate } = await render({ update });

    component.form.sku = 'SNK-A';
    component.save();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const controls = productControls(host);
    expect(controls.length).toBeGreaterThan(5);
    expect(host.querySelector<HTMLFieldSetElement>('[data-testid="product-form-fields"]')!.disabled).toBe(true);
    for (const control of controls) {
      expect(control.matches(':disabled')).toBe(true);
    }
    // The SKU field cannot take a second value while the first is being written.
    expect(host.querySelector<HTMLInputElement>('input[name="sku"]')!.matches(':disabled')).toBe(true);

    pending.next();
    pending.complete();
    expect(update).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ sku: 'SNK-A' }));
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('gives the fields back, with the entered values, when an edit save fails', async () => {
    const pending = new Subject<void>();
    const update = jest.fn(() => pending.asObservable());
    const { fixture, component, host, navigate } = await render({ update });

    component.form.sku = 'SNK-A';
    component.save();
    fixture.detectChanges();
    pending.error(new HttpErrorResponse({ status: 500 }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.querySelector<HTMLFieldSetElement>('[data-testid="product-form-fields"]')!.disabled).toBe(false);
    expect(host.querySelector<HTMLInputElement>('input[name="sku"]')!.matches(':disabled')).toBe(false);
    expect(host.querySelector<HTMLInputElement>('input[name="isActive"]')!.matches(':disabled')).toBe(false);
    expect(component.form.sku).toBe('SNK-A');
    expect(component.hasUnsavedChanges).toBe(true);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('holds every product field still while a create is in flight, so no later edit is dropped', async () => {
    const pending = new Subject<Product>();
    const create = jest.fn(() => pending.asObservable());
    const { fixture, component, host, navigate } = await render({ routeParams: {}, create });

    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    component.form.categoryId = 2;
    component.form.lowStockThreshold = 2;
    component.form.restockTo = 8;
    component.save();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const controls = productControls(host);
    expect(controls.length).toBeGreaterThan(5);
    for (const control of controls) {
      expect(control.matches(':disabled')).toBe(true);
    }

    pending.next(product({ id: 21, name: 'New Snack' }));
    pending.complete();
    expect(create).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('removes the supplier price comparison from Edit Product entirely', async () => {
    const { host } = await render();

    expect(host.querySelector('app-product-price-history')).toBeNull();
    expect(host.textContent).not.toMatch(/price comparison|price history|Lowest|Latest purchase/i);
    // The tracking settings stay.
    expect(host.querySelector('[data-testid="product-price-tracking"]')).not.toBeNull();
  });

  it('shows no Managed by Nayax badge or synced note for a product the business maintains', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="product-form-provider-badge"]')).toBeNull();
    expect(host.querySelector('[data-testid="product-provider-note"]')).toBeNull();
    expect(host.textContent).not.toContain('Nayax');
  });
});

/**
 * Unsaved changes (issue #1111): only actual modifications count, every departure asks before they
 * are lost, and the state clears only after the write that stored them succeeded.
 */
describe('ProductFormComponent unsaved changes (issue #1111)', () => {
  let confirmSpy: jest.SpyInstance;

  beforeEach(() => {
    confirmSpy = jest.spyOn(window, 'confirm').mockReturnValue(false);
  });

  afterEach(() => confirmSpy.mockRestore());

  it('lets an untouched edit page go without asking', async () => {
    const { component, host } = await render();

    expect(component.hasUnsavedChanges).toBe(false);
    expect(component.confirmLeave()).toBe(true);
    expect(confirmSpy).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="product-form-save-state"]')?.textContent).not.toContain('Unsaved changes');
  });

  it('asks before leaving a changed edit page, and stays when the operator declines', async () => {
    const { fixture, component, host } = await render();

    component.form.restockTo = 25;
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="product-form-save-state"]')?.textContent).toContain('Unsaved changes');
    expect(component.confirmLeave()).toBe(false);
    expect(confirmSpy).toHaveBeenCalledWith(PRODUCT_UNSAVED_LEAVE_WARNING);
    expect(component.form.restockTo).toBe(25);
  });

  it('treats a value changed back to what is stored as unchanged', async () => {
    const { component } = await render();

    component.form.sku = 'OTHER';
    component.form.sku = 'SNK-001';

    expect(component.hasUnsavedChanges).toBe(false);
  });

  it('asks the browser to confirm a reload only while there is something to lose', async () => {
    const { component } = await render();

    const clean = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent;
    component.onBeforeUnload(clean);
    expect(clean.defaultPrevented).toBe(false);

    component.form.name = 'Renamed';
    const dirty = new Event('beforeunload', { cancelable: true }) as BeforeUnloadEvent;
    component.onBeforeUnload(dirty);
    expect(dirty.defaultPrevented).toBe(true);
  });

  it('counts a chosen GST rule on New Product as a change, and nothing before it', async () => {
    const { component, chooseGstRule } = await render({ routeParams: {} });

    expect(component.hasUnsavedChanges).toBe(false);
    chooseGstRule('Taxable');
    expect(component.hasUnsavedChanges).toBe(true);
  });

  it('asks nothing on the way back to the list after a successful save', async () => {
    const { component } = await render();

    component.form.sku = 'SNK-200';
    component.save();

    expect(component.leaveWarning()).toBeNull();
    expect(component.confirmLeave()).toBe(true);
    expect(confirmSpy).not.toHaveBeenCalled();
  });

  it('keeps asking after a failed save, because nothing was stored', async () => {
    const update = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { component } = await render({ update });

    component.form.sku = 'SNK-300';
    component.save();

    expect(component.leaveWarning()).toBe(PRODUCT_UNSAVED_LEAVE_WARNING);
  });

  it('warns that a created product’s rule is unsaved when leaving after a partial create', async () => {
    const { component, chooseGstRule, submit } = await render({
      routeParams: {},
      create: jest.fn(() => of(product({ id: 55 }))),
      setGstRule: jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })))
    });

    chooseGstRule('Taxable');
    component.form.name = 'New Snack';
    component.form.unit = 'bag';
    submit();

    expect(component.leaveWarning()).toBe(PRODUCT_GST_OUTSTANDING_LEAVE_WARNING);
  });

  it('is wired as the canDeactivate guard of both product form routes', () => {
    const guarded = { confirmLeave: jest.fn(() => false) };

    expect(TestBed.runInInjectionContext(() => confirmProductFormLeave(guarded, null!, null!, null!))).toBe(false);
    expect(guarded.confirmLeave).toHaveBeenCalled();
  });
});
