import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { ProductFormComponent } from './product-form.component';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { Category, GstClassification, Product } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';

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
  } = {}
) {
  const update = overrides.update ?? jest.fn(() => of(undefined));
  const create = overrides.create ?? jest.fn(() => of(product()));
  const getCategories = overrides.getCategories ?? jest.fn(() => of(categories.map((category) => ({ ...category }))));
  const createCategory = overrides.createCategory ?? jest.fn(() => of({ id: 9, name: 'Chocolate' } as Category));
  const getGstRule =
    overrides.getGstRule ?? jest.fn(() => of({ productId: 7, gstRule: GstClassification.Unknown }));
  const setGstRule = overrides.setGstRule ?? jest.fn(() => of(undefined));
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
          getPriceComparison: jest.fn(() => of({ productId: 7, entries: [] }))
        }
      },
      { provide: CategoryService, useValue: { getAll: getCategories, create: createCategory } },
      { provide: SupportSessionService, useValue: { active$: overrides.sessionActive ?? of(false) } },
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

  /** The retry offered by the create-GST-rule panel after its own write failed (issue #829). */
  const gstRuleRetry = () =>
    host.querySelector<HTMLButtonElement>('app-product-create-gst-rule button');

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

  return {
    fixture,
    component: fixture.componentInstance,
    host,
    update,
    create,
    getGstRule,
    setGstRule,
    getCategories,
    createCategory,
    chooseGstRule,
    submit,
    gstRuleRetry,
    typeCategory,
    categoryOption,
    createCategoryOption,
    clickCategoryOption,
    navigate
  };
}

afterEach(() => TestBed.resetTestingModule());

describe('ProductFormComponent content width (issue #478)', () => {
  /**
   * The page's own card must not cap or centre itself: the application content area already
   * supplies the sidebar space and the page gutters (issue #454), so a narrower card here is the
   * unused width this issue removes. Asserting the absence of a cap is the regression contract -
   * a future `max-w-*` would silently restore the narrow centred card.
   */
  it('lets the form card use the whole available content width', async () => {
    const { host } = await render();

    const card = host.querySelector('.page > .card');
    expect(card).not.toBeNull();

    const classes = Array.from(card?.classList ?? []);
    expect(classes).not.toContain('mx-auto');
    expect(classes.filter((className) => /(^|:)max-w-/.test(className))).toEqual([]);
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
    const { component, navigate } = await render({ update });

    component.save();

    expect(component.error).toBe('The selected category does not exist.');
    expect(component.saving).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('cancels back to the product list without saving', async () => {
    const { host, update } = await render();

    const cancel = host.querySelector('a.btn-secondary') as HTMLAnchorElement;
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
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).disabled).toBe(true);
    expect((host.querySelector('[name="unit"]') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('[name="restockTo"]') as HTMLInputElement).disabled).toBe(false);

    expect(host.querySelector('.alert-info .alert-title')?.textContent).toContain(
      'Name and category are managed by Nayax and cannot be edited here.'
    );
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

    expect(host.querySelector('h1')?.textContent).toContain('New Product');
    expect((host.querySelector('[name="name"]') as HTMLInputElement).value).toBe('');
    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(false);
    // The category is chosen through the searchable picker here since issue #830, not the dropdown
    // the edit page still uses; its own describe below covers what that control does.
    expect(
      (host.querySelector('app-product-category-picker input[role="combobox"]') as HTMLInputElement).value
    ).toBe('');
    expect(host.querySelector('.alert-info')).toBeNull();
    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).textContent).toContain('Create Product');
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
      unit: 'unit',
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
    component.form.initialUnitCost = -1;
    component.save();

    expect(create).not.toHaveBeenCalled();
    expect(component.error).toBe('Initial unit cost cannot be negative.');
  });

  it('shows the server\'s refusal message and does not navigate when the create is refused', async () => {
    const create = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: 'Product name is required.' })));
    const { component, navigate } = await render({ routeParams: {}, create });

    component.form.name = 'New Snack';
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

  it('leaves the edit page on its dropdown, so the provider-managed category restriction is unchanged', async () => {
    const { host } = await render({ product: product({ providerManagedFields: ['name', 'categoryId'] }) });

    expect(host.querySelector('app-product-category-picker')).toBeNull();
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).disabled).toBe(true);
  });

  it('creates the product with the id of the category selected in the picker', async () => {
    const { component, typeCategory, categoryOption, clickCategoryOption, create, submit } = await render({
      routeParams: {}
    });

    component.form.name = 'New Snack';
    typeCategory('sn');
    clickCategoryOption(categoryOption('Snacks')!);
    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: 'New Snack', categoryId: 2 }));
  });

  it('creates the product with no category while none is selected', async () => {
    const { component, create, submit } = await render({ routeParams: {} });

    component.form.name = 'New Snack';
    submit();

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ categoryId: null }));
  });

  it('clears the category back to none through the picker', async () => {
    const { component, typeCategory, categoryOption, clickCategoryOption, create, submit } = await render({
      routeParams: {}
    });

    component.form.name = 'New Snack';
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
    expect(host.querySelector('h1')?.textContent).toContain('New Product');

    submit();

    expect(create).toHaveBeenCalledWith({
      name: 'New Snack',
      sku: 'SNK-900',
      description: null,
      lowStockThreshold: 3,
      restockTo: 9,
      unit: 'unit',
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
    expect(panel!.textContent).toContain('does not change the GST classification of any');

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
  });

  it('retries the rule alone, so a retry never creates a second product', async () => {
    const create = createdProduct();
    const setGstRule = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(undefined));
    const { component, chooseGstRule, submit, gstRuleRetry, navigate } = await render({
      routeParams: {},
      create,
      setGstRule
    });

    chooseGstRule('Taxable');
    named(component);
    submit();
    expect(navigate).not.toHaveBeenCalled();

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
    const { component, chooseGstRule, submit, gstRuleRetry } = await render({
      routeParams: {},
      create: createdProduct(),
      setGstRule
    });

    chooseGstRule('Taxable');
    named(component);
    submit();

    chooseGstRule('GST-free');
    gstRuleRetry()!.click();

    expect(setGstRule).toHaveBeenLastCalledWith(55, GstClassification.GstFree);
  });

  /**
   * The create has happened, so the page must offer no create control at all - the retry of the one
   * outstanding write lives with the component that owns it, next to the choice it would store.
   */
  it('offers the rule retry and no create control once the product exists', async () => {
    const { component, host, chooseGstRule, submit, gstRuleRetry } = await render({
      routeParams: {},
      create: createdProduct(),
      setGstRule: refusedRule()
    });

    chooseGstRule('Taxable');
    named(component);
    submit();

    expect(host.querySelector('button[type="submit"]')).toBeNull();
    const retry = gstRuleRetry();
    expect(retry?.textContent).toContain('Save GST rule');
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

  it('leaves the edit page saving its rule in the panel rather than with the product', async () => {
    const { component, host, update, setGstRule, navigate } = await render();

    component.save();

    expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ name: 'E2E Chips' }));
    expect(setGstRule).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/products']);
    expect(host.querySelector('app-product-create-gst-rule')).toBeNull();
    expect(
      Array.from(host.querySelectorAll('app-product-gst-rule button')).map((button) => button.textContent?.trim())
    ).toContain('Save GST rule');
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
 * Why this product cannot be deleted, said once, here (issue #825). The Products list used to carry
 * the server's refusal as a paragraph on every ineligible row; it is a per-product explanation, so
 * it belongs on the product's own page. `GET api/products/{id}` already answers `canDelete` and the
 * `deletionRefusal` behind it (issue #737), and this notice shows that refusal **verbatim** - the
 * page never words the deletion rule a second time. It is informational: it gates no field, no Save
 * and no other control.
 */
describe('ProductFormComponent deletion notice (issue #825)', () => {
  const historyRefusal =
    'This product cannot be deleted because its history refers to it (2 stock movements, 1 sale). ' +
    'Deactivate it instead - a deactivated product keeps its history, stays out of the reorder ' +
    'alerts and can be reactivated. Nothing was deleted.';

  function notice(host: HTMLElement): HTMLElement | null {
    return host.querySelector('.alert-danger');
  }

  it('shows the server’s refusal verbatim, in a light-red alert directly under the Edit Product header', async () => {
    const { host } = await render({ product: product({ canDelete: false, deletionRefusal: historyRefusal }) });

    const shown = notice(host);
    expect(shown).not.toBeNull();
    expect(Array.from(shown!.classList)).toEqual(expect.arrayContaining(['alert', 'alert-danger']));
    expect(shown!.textContent?.trim()).toBe(historyRefusal);
    // Directly under the page header, before the form card.
    expect(host.querySelector('.page > header.page-header')?.nextElementSibling).toBe(shown);
  });

  it('falls back to its own sentence when the response carries no refusal text', async () => {
    const { host } = await render({ product: product({ canDelete: false, deletionRefusal: null }) });

    expect(notice(host)?.textContent?.trim()).toBe('This product cannot be deleted.');
  });

  it('falls back to its own sentence when the refusal text is blank', async () => {
    const { host } = await render({ product: product({ canDelete: false, deletionRefusal: '   ' }) });

    expect(notice(host)?.textContent?.trim()).toBe('This product cannot be deleted.');
  });

  it('shows no notice for a product the server would delete', async () => {
    const { host } = await render({ product: product({ canDelete: true, deletionRefusal: null }) });

    expect(notice(host)).toBeNull();
  });

  it('shows no notice on New Product, where there is no product to answer for', async () => {
    const { host } = await render({ routeParams: {} });

    expect(notice(host)).toBeNull();
  });

  it('shows the notice for an inactive product and during a support session, and changes nothing on the form', async () => {
    const { host } = await render({
      product: product({ isActive: false, canDelete: false, deletionRefusal: historyRefusal }),
      sessionActive: of(true)
    });

    expect(notice(host)?.textContent?.trim()).toBe(historyRefusal);
    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('[name="isActive"]') as HTMLInputElement).checked).toBe(false);
    // Save stays disabled only for the reason it already was - the session, not this notice.
    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('still saves an undeletable product, so the notice gates nothing', async () => {
    const { component, update, navigate } = await render({
      product: product({ canDelete: false, deletionRefusal: historyRefusal })
    });

    component.save();

    expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ name: 'E2E Chips' }));
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });
});
