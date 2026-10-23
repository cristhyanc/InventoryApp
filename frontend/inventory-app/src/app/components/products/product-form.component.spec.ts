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
  } = {}
) {
  const update = overrides.update ?? jest.fn(() => of(undefined));
  const create = overrides.create ?? jest.fn(() => of(product()));
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
      { provide: CategoryService, useValue: { getAll: jest.fn(() => of(categories)) } },
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

  return {
    fixture,
    component: fixture.componentInstance,
    host,
    update,
    create,
    getGstRule,
    setGstRule,
    chooseGstRule,
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
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).disabled).toBe(false);
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
    const { component, create, navigate } = await render({ routeParams: {} });

    component.form.name = 'New Snack';
    component.form.categoryId = 2;
    component.form.quantityInStock = 10;
    component.form.initialUnitCost = 1.1;
    component.form.lowStockThreshold = 2;
    component.form.restockTo = 8;
    component.save();

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
 * Choosing a product's GST treatment while creating it (issue #829).
 *
 * The product GST rule is its own API resource (`PUT api/products/{id}/gst-rule`) and the product
 * payload is a pinned contract, so creating a product with a rule is two writes: the create, then
 * the rule on the id the create answered. This page therefore has to own the partial failure - a
 * product that exists with its rule unsaved - and must never report that as a completed create.
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

    // Nothing is read for a product that does not exist, and the panel offers no Save of its own:
    // the rule is written with the product.
    expect(getGstRule).not.toHaveBeenCalled();
    expect(
      Array.from(panel!.querySelectorAll('button')).filter((button) => button.textContent?.trim().startsWith('Save'))
    ).toEqual([]);
  });

  it('creates the product and then persists the chosen rule before returning to the list', async () => {
    const create = createdProduct();
    const { component, chooseGstRule, setGstRule, navigate } = await render({ routeParams: {}, create });

    chooseGstRule('Taxable');
    named(component).save();

    expect(create).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledWith(55, GstClassification.Taxable);
    expect(navigate).toHaveBeenCalledWith(['/products']);
    expect(component.error).toBe('');
  });

  it('persists GST-free as GST-free, not as the unclassified state', async () => {
    const { component, chooseGstRule, setGstRule } = await render({ routeParams: {}, create: createdProduct() });

    chooseGstRule('GST-free');
    named(component).save();

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
    const { component, chooseGstRule, setGstRule, navigate } = await render({ routeParams: {}, create });

    chooseGstRule('No rule');
    named(component).save();

    expect(create).toHaveBeenCalledTimes(1);
    expect(setGstRule).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('attempts no rule write when the create itself is refused', async () => {
    const create = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: 'The selected category does not exist.' })));
    const { component, chooseGstRule, setGstRule, navigate } = await render({ routeParams: {}, create });

    chooseGstRule('Taxable');
    named(component).save();

    expect(setGstRule).not.toHaveBeenCalled();
    expect(component.createdProductId).toBeNull();
    expect(component.error).toBe('The selected category does not exist.');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('reports a created product whose rule was not saved, and never claims the create finished', async () => {
    const { component, host, fixture, chooseGstRule, navigate } = await render({
      routeParams: {},
      create: createdProduct(),
      setGstRule: refusedRule()
    });

    chooseGstRule('Taxable');
    named(component).save();
    fixture.detectChanges();

    expect(navigate).not.toHaveBeenCalled();
    expect(component.saving).toBe(false);
    expect(component.createdProductId).toBe(55);
    expect(component.error).toContain('The product was created, but its GST rule was not saved.');
    expect(host.querySelector('.field-error')?.textContent).toContain('GST rule was not saved');
  });

  it('retries the rule alone, so a retry never creates a second product', async () => {
    const create = createdProduct();
    const setGstRule = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(undefined));
    const { component, chooseGstRule, navigate } = await render({ routeParams: {}, create, setGstRule });

    chooseGstRule('Taxable');
    named(component).save();
    expect(navigate).not.toHaveBeenCalled();

    component.save();

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
    const { component, fixture, chooseGstRule } = await render({
      routeParams: {},
      create: createdProduct(),
      setGstRule
    });

    chooseGstRule('Taxable');
    named(component).save();
    fixture.detectChanges();

    chooseGstRule('GST-free');
    component.save();

    expect(setGstRule).toHaveBeenLastCalledWith(55, GstClassification.GstFree);
  });

  it('labels the outstanding write for the rule alone and keeps it reachable', async () => {
    const { component, host, fixture, chooseGstRule } = await render({
      routeParams: {},
      create: createdProduct(),
      setGstRule: refusedRule()
    });

    chooseGstRule('Taxable');
    named(component).save();
    fixture.detectChanges();

    const submit = host.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.textContent).toContain('Save GST Rule');
    expect(submit.disabled).toBe(false);
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
    const { component, host, fixture, chooseGstRule, navigate } = await render({
      routeParams: {},
      create,
      setGstRule
    });

    chooseGstRule('Taxable');
    named(component).save();

    component.form.lowStockThreshold = 10;
    component.form.restockTo = 4;
    component.form.name = '   ';
    fixture.detectChanges();

    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(false);

    component.save();

    expect(create).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledTimes(2);
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('writes neither the product nor the rule while a support session is active', async () => {
    const create = createdProduct();
    const { component, setGstRule } = await render({ routeParams: {}, create, sessionActive: of(true) });

    named(component).save();
    expect(create).not.toHaveBeenCalled();

    // The same holds for a retry of an outstanding rule: the endpoint refuses it either way.
    component.createdProductId = 55;
    component.save();
    expect(setGstRule).not.toHaveBeenCalled();
  });

  it('leaves the edit page saving its rule in the panel rather than with the product', async () => {
    const { component, host, update, setGstRule, navigate } = await render();

    component.save();

    expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ name: 'E2E Chips' }));
    expect(setGstRule).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/products']);
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
