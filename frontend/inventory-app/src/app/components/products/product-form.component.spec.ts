import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ProductFormComponent } from './product-form.component';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { Category, GstClassification, Product, Supplier } from '../../models/models';

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
    supplierId: 3,
    isActive: true,
    isLowStock: false,
    isReorderAlert: false,
    ...overrides
  };
}

const categories: Category[] = [
  { id: 1, name: 'Drinks' },
  { id: 2, name: 'Snacks' }
];

const suppliers: Supplier[] = [
  { id: 3, name: 'Supplier A' },
  { id: 4, name: 'Supplier B' }
];

/** The routed edit page, rendered the way entering `/products/7/edit` renders it. */
async function render(overrides: { update?: jest.Mock } = {}) {
  const update = overrides.update ?? jest.fn(() => of(undefined));
  const navigate = jest.fn();

  await TestBed.configureTestingModule({
    imports: [ProductFormComponent],
    providers: [
      {
        provide: ProductService,
        useValue: {
          get: jest.fn(() => of(product())),
          update,
          getGstRule: jest.fn(() => of({ productId: 7, gstRule: GstClassification.Unknown })),
          getPriceComparison: jest.fn(() => of({ productId: 7, entries: [] }))
        }
      },
      { provide: CategoryService, useValue: { getAll: jest.fn(() => of(categories)) } },
      { provide: SupplierService, useValue: { getAll: jest.fn(() => of(suppliers)) } },
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: '7' }) } } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductFormComponent);
  jest.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(navigate);
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement, update, navigate };
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

describe('ProductFormComponent editing (issue #478 regression cover)', () => {
  it('shows every editable field with the loaded product values, and the Nayax-managed ones read-only', async () => {
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
    expect((host.querySelector('[name="supplierId"]') as HTMLSelectElement).value).toContain('3');

    expect((host.querySelector('[name="name"]') as HTMLInputElement).readOnly).toBe(true);
    expect((host.querySelector('[name="unitPrice"]') as HTMLInputElement).readOnly).toBe(true);
    expect((host.querySelector('[name="categoryId"]') as HTMLSelectElement).disabled).toBe(true);

    expect(host.querySelectorAll('label.field, label.flex').length).toBeGreaterThanOrEqual(8);
  });

  it('keeps the field labels and their inputs together, so a wider card does not separate them', async () => {
    const { host } = await render();

    for (const name of ['sku', 'description', 'supplierId', 'lowStockThreshold', 'restockTo', 'unit']) {
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

  it('saves the edited values against the routed product and returns to the list', async () => {
    const { component, update, navigate } = await render();

    component.form.sku = 'SNK-002';
    component.form.lowStockThreshold = 6;
    component.form.restockTo = 24;
    component.form.supplierId = 4;
    component.form.isActive = false;
    component.save();

    expect(update).toHaveBeenCalledWith(7, {
      name: 'E2E Chips',
      sku: 'SNK-002',
      description: 'Salted chips',
      unitPrice: 3.5,
      lowStockThreshold: 6,
      restockTo: 24,
      unit: 'bag',
      categoryId: 2,
      supplierId: 4,
      isActive: false
    });
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  it('cancels back to the product list without saving', async () => {
    const { host, update } = await render();

    const cancel = host.querySelector('a.btn-secondary') as HTMLAnchorElement;
    expect(cancel.getAttribute('href')).toBe('/products');
    expect(update).not.toHaveBeenCalled();
  });
});
