import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ProductListComponent } from './product-list.component';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
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
    needToOrder: 0,
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
    isLowStock: false,
    isReorderAlert: false,
    ...overrides
  };
}

describe('ProductListComponent Actions column layout (issue #451)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[]) {
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of(products) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: SupplierService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host };
  }

  it('keeps the Actions header pinned to the right of the scrollable table with an opaque token background', async () => {
    const { host } = await render([
      product({ id: 1, name: 'A product with a very long descriptive catalogue name that forces the table to overflow its viewport width' })
    ]);

    const scrollArea = host.querySelector('.overflow-x-auto');
    expect(scrollArea).not.toBeNull();
    expect(scrollArea?.querySelector('table')).not.toBeNull();

    const headerCells = Array.from(host.querySelectorAll<HTMLTableCellElement>('thead th'));
    const actionsHeader = headerCells[headerCells.length - 1];
    expect(Array.from(actionsHeader.classList)).toEqual(expect.arrayContaining(['sticky', 'right-0', 'z-10', 'bg-md-gray-100']));
    // #410 defines no arbitrary shadow/colour token: the opaque bg-md-gray-100 surface is the only separator.
    expect(Array.from(actionsHeader.classList).filter((cssClass) => cssClass.startsWith('shadow'))).toEqual([]);
  });

  it('keeps each row\'s Actions cell pinned to the right, opaque, and bounded on narrow viewports, without changing the action controls', async () => {
    const { host } = await render([product({ id: 42, name: 'Widget' })]);

    const row = host.querySelector('tbody tr') as HTMLTableRowElement;
    const cells = Array.from(row.querySelectorAll<HTMLTableCellElement>('td'));
    const actionsCell = cells[cells.length - 1];

    expect(Array.from(actionsCell.classList)).toEqual(expect.arrayContaining(['sticky', 'right-0', 'z-10', 'bg-white']));
    expect(Array.from(actionsCell.classList).filter((cssClass) => cssClass.startsWith('shadow'))).toEqual([]);

    // The 390px acceptance criterion: the pinned column stays bounded on narrow viewports instead
    // of growing wide enough to obscure the rest of the readable row, but is unconstrained on desktop.
    const actionsWrapper = actionsCell.querySelector('div');
    expect(actionsWrapper).not.toBeNull();
    expect(Array.from(actionsWrapper!.classList)).toEqual(expect.arrayContaining(['max-w-[140px]', 'sm:max-w-none']));

    // Stock and Delete remain; Edit is gone from the Actions column (issue #497 moves it to the row).
    const stockLink = actionsCell.querySelector('a[href="/products/42/stock"]');
    const deleteButton = Array.from(actionsCell.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    expect(actionsCell.querySelector('a[href="/products/42/edit"]')).toBeNull();
    expect(stockLink?.textContent?.trim()).toBe('Stock');
    expect(deleteButton).toBeDefined();
  });

  it('preserves every product field already shown alongside the Actions column', async () => {
    const { host } = await render([
      product({
        id: 7,
        name: 'Widget',
        category: { id: 1, name: 'Snacks' },
        supplier: { id: 2, name: 'Acme' },
        unitPrice: 3.5,
        quantityInStock: 8,
        unit: 'ea',
        isActive: true,
        isLowStock: false
      })
    ]);

    expect(host.textContent).toContain('Widget');
    expect(host.textContent).toContain('Snacks');
    expect(host.textContent).toContain('Acme');
    expect(host.textContent).toContain('$3.50');
    expect(host.textContent).toContain('8 ea');
    expect(host.textContent).toContain('Active');
  });
});

describe('ProductListComponent row navigation and sorting (issue #497)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[]) {
    const navigate = jest.fn();
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of(products), update: () => of(undefined) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: SupplierService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } }
      ]
    }).compileComponents();

    jest.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(navigate);

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, navigate };
  }

  it('navigates to the product editor when a non-action part of the row is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const row = host.querySelector('tbody tr') as HTMLTableRowElement;
    const nameCell = row.querySelector('td') as HTMLTableCellElement;
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

  it('leaves Enter on the name link to the link itself so navigation is not triggered twice', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const editLink = host.querySelector('tbody tr a.table-row-anchor') as HTMLAnchorElement;
    editLink.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(navigate).not.toHaveBeenCalled();
  });

  it.each([
    ['Ctrl', { ctrlKey: true }],
    ['Meta', { metaKey: true }],
    ['Shift', { shiftKey: true }],
    ['Alt', { altKey: true }],
    ['middle-button', { button: 1 }]
  ])('leaves a %s click on a non-action cell to the browser instead of navigating in place', async (_label, init) => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const supplierCell = host.querySelectorAll('tbody tr td')[2] as HTMLTableCellElement;
    supplierCell.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ...init }));

    expect(navigate).not.toHaveBeenCalled();
  });

  it('does not navigate the row when the name link itself is clicked, so the link handles it once', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const editLink = host.querySelector('tbody tr a.table-row-anchor') as HTMLAnchorElement;
    editLink.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true }));

    expect(navigate).not.toHaveBeenCalled();
  });

  it('does not navigate when the Stock link is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const stockLink = host.querySelector('a[href="/products/9/stock"]') as HTMLAnchorElement;
    stockLink.click();

    expect(navigate).not.toHaveBeenCalled();
  });

  it('does not navigate when the Delete button is clicked, including while opening the delete confirmation', async () => {
    const { fixture, host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete') as HTMLButtonElement;
    deleteButton.click();
    fixture.detectChanges();

    expect(navigate).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Confirm delete');
  });

  it('does not navigate when the Status badge button is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget', isActive: true })]);

    const statusButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Active') as HTMLButtonElement;
    statusButton.click();

    expect(navigate).not.toHaveBeenCalled();
  });

  it('sorts by name ascending then descending, with a visible and accessible active sort indicator', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'Banana' }),
      product({ id: 2, name: 'Apple' }),
      product({ id: 3, name: 'Cherry' })
    ]);

    const nameHeader = host.querySelector('thead th:first-child') as HTMLTableCellElement;
    const nameButton = nameHeader.querySelector('button') as HTMLButtonElement;

    nameButton.click();
    fixture.detectChanges();
    let rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelector('td')?.textContent?.trim())).toEqual(['Apple', 'Banana', 'Cherry']);
    expect(nameHeader.getAttribute('aria-sort')).toBe('ascending');
    expect(nameButton.textContent).toContain('ascending');

    nameButton.click();
    fixture.detectChanges();
    rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelector('td')?.textContent?.trim())).toEqual(['Cherry', 'Banana', 'Apple']);
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
    expect(rows.map((r) => r.querySelector('td')?.textContent?.trim())).toEqual(['C', 'A', 'B']);
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
    // 'Snacks' sorts first; the two null categories tie and keep their id order (1 before... here ids 2 then 3).
    expect(rows.map((r) => r.querySelector('td')?.textContent?.trim())).toEqual(['A', 'B', 'C']);
    expect(component.sortColumn).toBe('category');
  });

  it('keeps sorting applied after the product list is refreshed by a filter change', async () => {
    const { fixture, host } = await render([product({ id: 1, name: 'Banana' }), product({ id: 2, name: 'Apple' })]);
    const component = fixture.componentInstance;

    const nameButton = (host.querySelector('thead th:first-child') as HTMLElement).querySelector('button') as HTMLButtonElement;
    nameButton.click();
    fixture.detectChanges();

    component.applyFilters();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelector('td')?.textContent?.trim())).toEqual(['Apple', 'Banana']);
  });
});
