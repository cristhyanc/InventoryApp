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
    unitPrice: 3.5,
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
    sessionActive?: Observable<boolean>;
    routeParams?: Record<string, string>;
  } = {}
) {
  const update = overrides.update ?? jest.fn(() => of(undefined));
  const create = overrides.create ?? jest.fn(() => of(product()));
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
          getGstRule: jest.fn(() => of({ productId: 7, gstRule: GstClassification.Unknown })),
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

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, update, create, navigate };
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
  it('shows every field editable, including name, price and category, and carries no provider-managed note', async () => {
    const { host } = await render();

    const value = (name: string) => (host.querySelector(`[name="${name}"]`) as HTMLInputElement | null)?.value;
    expect(value('name')).toBe('E2E Chips');
    expect(value('sku')).toBe('SNK-001');
    expect(value('description')).toBe('Salted chips');
    expect(value('unitPrice')).toBe('3.5');
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

    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('[name="unitPrice"]') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).disabled).toBe(false);
    expect(host.querySelector('.alert-info')).toBeNull();

    expect(host.querySelectorAll('label.field, label.flex').length).toBeGreaterThanOrEqual(8);
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

  it('refuses to save a negative unit price', async () => {
    const { component, update, navigate } = await render();

    component.form.unitPrice = -1;
    component.save();

    expect(update).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(component.error).toBe('Unit price cannot be negative.');
  });

  it('saves the edited values, including the now-editable name, price and category, and returns to the list', async () => {
    const { component, update, navigate } = await render();

    component.form.name = 'E2E Chips (renamed)';
    component.form.unitPrice = 4.25;
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
      unitPrice: 4.25,
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

  it('locks name and category, names Nayax, and leaves price and other local fields editable', async () => {
    const { host } = await render({ product: providerManagedProduct() });

    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(true);
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).disabled).toBe(true);
    expect((host.querySelector('[name="unitPrice"]') as HTMLInputElement).readOnly).toBe(false);
    expect((host.querySelector('[name="unitPrice"]') as HTMLInputElement).disabled).toBe(false);

    expect(host.querySelector('.alert-info .alert-title')?.textContent).toContain(
      'Name and category are managed by Nayax and cannot be edited here.'
    );
  });

  it('still saves the editable fields, including price, for a provider-linked product', async () => {
    const { component, update, navigate } = await render({ product: providerManagedProduct() });

    component.form.unitPrice = 5;
    component.save();

    expect(update).toHaveBeenCalledWith(
      7,
      expect.objectContaining({ name: 'E2E Chips', categoryId: 2, unitPrice: 5 })
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

    // No GST rule/price history for a product that does not exist yet.
    expect(host.querySelector('app-product-gst-rule')).toBeNull();
    expect(host.querySelector('app-product-price-history')).toBeNull();
  });

  it('creates the product with the entered fields and returns to the list', async () => {
    const { component, create, navigate } = await render({ routeParams: {} });

    component.form.name = 'New Snack';
    component.form.unitPrice = 2.5;
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
      unitPrice: 2.5,
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
