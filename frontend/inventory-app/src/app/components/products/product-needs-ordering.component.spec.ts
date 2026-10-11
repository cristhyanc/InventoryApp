import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ProductNeedsOrderingComponent } from './product-needs-ordering.component';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ToastService } from '../../services/toast.service';
import { Product } from '../../models/models';

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Product',
    sku: null,
    description: null,
    unitPrice: 2.5,
    averageUnitCost: 1,
    costingQuantity: null,
    inventoryValue: null,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 10,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 15,
    needToOrder: 3,
    mdbCode: null,
    unit: 'ea',
    lastEatBefore1: null,
    lastEatBefore2: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId: null,
    category: null,
    supplierId: null,
    supplier: null,
    isActive: true,
    isLowStock: true,
    isReorderAlert: false,
    ...overrides
  };
}

describe('ProductNeedsOrderingComponent row navigation, Delete removal and sorting (issue #498)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[]) {
    const navigate = jest.fn();
    await TestBed.configureTestingModule({
      imports: [ProductNeedsOrderingComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getLowStock: () => of(products), update: () => of(undefined) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: SupplierService, useValue: { getAll: () => of([]) } },
        { provide: SupplierOrderService, useValue: { create: () => of(undefined) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } }
      ]
    }).compileComponents();

    jest.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(navigate);

    const fixture = TestBed.createComponent(ProductNeedsOrderingComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, navigate };
  }

  it('navigates to the product editor when a non-action part of the row is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const row = host.querySelector('tbody tr') as HTMLTableRowElement;
    const nameCell = row.querySelectorAll('td')[1] as HTMLTableCellElement;
    nameCell.click();

    expect(navigate).toHaveBeenCalledWith(['/products', 9, 'edit']);
  });

  it('gives keyboard users a real link to the product editor instead of a focusable row with role="link"', async () => {
    const { host } = await render([product({ id: 9, name: 'Widget' })]);

    const row = host.querySelector('tbody tr') as HTMLTableRowElement;
    expect(row.hasAttribute('tabindex')).toBe(false);
    expect(row.hasAttribute('role')).toBe(false);

    const editLink = row.querySelector('a.table-row-anchor') as HTMLAnchorElement;
    expect(editLink.getAttribute('href')).toBe('/products/9/edit');
    expect(editLink.getAttribute('aria-label')).toContain('Widget');
    expect(editLink.textContent?.trim()).toBe('Widget');
  });

  it('activates row navigation with Enter on a non-action part of the row', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const row = host.querySelector('tbody tr') as HTMLTableRowElement;
    row.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(navigate).toHaveBeenCalledWith(['/products', 9, 'edit']);
  });

  it('does not remove the Edit button twice when the name link itself is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const editLink = host.querySelector('tbody tr a.table-row-anchor') as HTMLAnchorElement;
    editLink.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true }));

    expect(navigate).not.toHaveBeenCalled();
  });

  it.each([
    ['Ctrl', { ctrlKey: true }],
    ['Meta', { metaKey: true }],
    ['Shift', { shiftKey: true }],
    ['Alt', { altKey: true }],
    ['middle-button', { button: 1 }]
  ])('leaves a %s click on a non-action cell to the browser instead of navigating in place', async (_label, init) => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget', category: { id: 1, name: 'Snacks' } })]);

    const categoryCell = host.querySelectorAll('tbody tr td')[2] as HTMLTableCellElement;
    categoryCell.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ...init }));

    expect(navigate).not.toHaveBeenCalled();
  });

  it('does not navigate when the selection checkbox is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const checkbox = host.querySelector('tbody tr input[type="checkbox"]') as HTMLInputElement;
    checkbox.click();

    expect(navigate).not.toHaveBeenCalled();
  });

  it('does not navigate when the Stock link is clicked, and leaves its destination unchanged', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const stockLink = host.querySelector('a[href="/products/9/stock"]') as HTMLAnchorElement;
    expect(stockLink).not.toBeNull();
    expect(stockLink.textContent?.trim()).toBe('Stock');
    stockLink.click();

    expect(navigate).not.toHaveBeenCalled();
  });

  it('does not navigate when the Status badge button is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget', isActive: true })]);

    const statusButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Active') as HTMLButtonElement;
    statusButton.click();

    expect(navigate).not.toHaveBeenCalled();
  });

  it('removes the standalone Edit and Delete buttons from Needs Ordering', async () => {
    const { host } = await render([product({ id: 9, name: 'Widget' })]);

    const editButton = Array.from(host.querySelectorAll('a, button')).find((el) => el.textContent?.trim() === 'Edit');
    expect(editButton).toBeUndefined();
    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    expect(deleteButton).toBeUndefined();
    expect(host.textContent).not.toContain('Confirm delete');
  });

  it('sorts by name ascending then descending, with a visible and accessible active sort indicator', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'Banana' }),
      product({ id: 2, name: 'Apple' }),
      product({ id: 3, name: 'Cherry' })
    ]);

    const nameHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Name')) as HTMLTableCellElement;
    const nameButton = nameHeader.querySelector('button') as HTMLButtonElement;

    nameButton.click();
    fixture.detectChanges();
    let rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['Apple', 'Banana', 'Cherry']);
    expect(nameHeader.getAttribute('aria-sort')).toBe('ascending');
    expect(nameButton.textContent).toContain('ascending');

    nameButton.click();
    fixture.detectChanges();
    rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['Cherry', 'Banana', 'Apple']);
    expect(nameHeader.getAttribute('aria-sort')).toBe('descending');
  });

  it('sorts numeric columns numerically rather than lexicographically', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'A', unitPrice: 9 }),
      product({ id: 2, name: 'B', unitPrice: 10 }),
      product({ id: 3, name: 'C', unitPrice: 2 })
    ]);

    const priceHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Price')) as HTMLTableCellElement;
    (priceHeader.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['C', 'A', 'B']);
  });

  it('sorts missing values (no category) consistently to the end, and keeps ties stable by product id', async () => {
    const { fixture, host } = await render([
      product({ id: 3, name: 'C', category: null }),
      product({ id: 1, name: 'A', category: { id: 1, name: 'Snacks' } }),
      product({ id: 2, name: 'B', category: null })
    ]);
    const component = fixture.componentInstance;

    const categoryHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Category')) as HTMLTableCellElement;
    (categoryHeader.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['A', 'B', 'C']);
    expect(component.sortColumn).toBe('category');
  });

  it('keeps sorting applied after the product list is refreshed by a filter change', async () => {
    const { fixture, host } = await render([product({ id: 1, name: 'Banana' }), product({ id: 2, name: 'Apple' })]);
    const component = fixture.componentInstance;

    const nameButton = (Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Name')) as HTMLElement).querySelector(
      'button'
    ) as HTMLButtonElement;
    nameButton.click();
    fixture.detectChanges();

    component.applyFilters();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['Apple', 'Banana']);
  });

  it('does not mutate the underlying products when sorted', async () => {
    const { fixture, host } = await render([product({ id: 1, name: 'Banana' }), product({ id: 2, name: 'Apple' })]);
    const component = fixture.componentInstance;

    const nameButton = (Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Name')) as HTMLElement).querySelector(
      'button'
    ) as HTMLButtonElement;
    nameButton.click();
    fixture.detectChanges();

    expect(component.products.map((p) => p.name)).toEqual(['Banana', 'Apple']);
    expect(component.sortedProducts.map((p) => p.name)).toEqual(['Apple', 'Banana']);
  });

  it('preserves selection and the Mark as ordered workflow alongside sorting', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'Banana', needToOrder: 4 }),
      product({ id: 2, name: 'Apple', needToOrder: 2 })
    ]);
    const component = fixture.componentInstance;

    const nameButton = (Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Name')) as HTMLElement).querySelector(
      'button'
    ) as HTMLButtonElement;
    nameButton.click();
    fixture.detectChanges();

    // After ascending sort by name, the first row is Apple (id 2).
    const firstRowName = host.querySelectorAll('tbody tr')[0].querySelectorAll('td')[1]?.textContent?.trim();
    expect(firstRowName).toBe('Apple');

    const checkbox = host.querySelector('tbody tr input[type="checkbox"]') as HTMLInputElement;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    component.openOrderForm();
    fixture.detectChanges();

    expect(component.selectedProductIds.has(2)).toBe(true);
    expect(component.showOrderForm).toBe(true);
    expect(component.selectedProducts().map((p) => p.name)).toEqual(['Apple']);
  });
});
