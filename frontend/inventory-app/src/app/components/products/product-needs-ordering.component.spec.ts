import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { ProductNeedsOrderingComponent } from './product-needs-ordering.component';
import { SupportSessionService } from '../../support-session/support-session.service';
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

  async function render(
    products: Product[],
    options: { sessionActive?: Observable<boolean>; update?: jest.Mock; create?: jest.Mock } = {}
  ) {
    const navigate = jest.fn();
    const update = options.update ?? jest.fn(() => of(undefined));
    const create = options.create ?? jest.fn(() => of(undefined));
    await TestBed.configureTestingModule({
      imports: [ProductNeedsOrderingComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getLowStock: () => of(products), update } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: SupplierService, useValue: { getAll: () => of([]) } },
        { provide: SupplierOrderService, useValue: { create } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } }
      ]
    }).compileComponents();

    jest.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(navigate);

    const fixture = TestBed.createComponent(ProductNeedsOrderingComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, navigate, update, create };
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

  it('defers Enter on the focusable Name anchor to the anchor itself instead of double-navigating', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    // The Name anchor is the real keyboard target (it is focusable); the row's own
    // keydown.enter handler must recognize the event originated in an own control
    // and defer to the anchor instead of firing a second, possibly conflicting navigation.
    const editLink = host.querySelector('tbody tr a.table-row-anchor') as HTMLAnchorElement;
    editLink.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(navigate).not.toHaveBeenCalled();
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

  it('keeps a missing category last in descending order too (regression for issue #582)', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'A', category: { id: 1, name: 'Snacks' } }),
      product({ id: 2, name: 'B', category: null }),
      product({ id: 3, name: 'C', category: { id: 2, name: 'Drinks' } })
    ]);

    const categoryHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Category')) as HTMLTableCellElement;
    const categoryButton = categoryHeader.querySelector('button') as HTMLButtonElement;
    categoryButton.click();
    fixture.detectChanges();
    categoryButton.click();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    // Descending by category name (Snacks, Drinks) with the missing value still last, not first.
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['A', 'C', 'B']);
  });

  it('keeps a missing supplier last in both ascending and descending order', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'A', supplier: { id: 1, name: 'Acme' } }),
      product({ id: 2, name: 'B', supplier: null }),
      product({ id: 3, name: 'C', supplier: { id: 2, name: 'Zenith' } })
    ]);

    const supplierHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Supplier')) as HTMLTableCellElement;
    const supplierButton = supplierHeader.querySelector('button') as HTMLButtonElement;

    supplierButton.click();
    fixture.detectChanges();
    let rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['A', 'C', 'B']);

    supplierButton.click();
    fixture.detectChanges();
    rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['C', 'A', 'B']);
  });

  it('breaks a tie between two missing categories deterministically in descending order', async () => {
    const { fixture, host } = await render([
      product({ id: 2, name: 'B', category: null }),
      product({ id: 1, name: 'A', category: null }),
      product({ id: 3, name: 'C', category: { id: 1, name: 'Snacks' } })
    ]);

    const categoryHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Category')) as HTMLTableCellElement;
    const categoryButton = categoryHeader.querySelector('button') as HTMLButtonElement;
    categoryButton.click();
    fixture.detectChanges();
    categoryButton.click();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    // The non-missing value (Snacks) sorts first; the two missing values stay last,
    // broken deterministically by id (A before B) rather than by insertion order.
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['C', 'A', 'B']);
  });

  it('sorts numeric columns numerically in descending order as well', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'A', unitPrice: 9 }),
      product({ id: 2, name: 'B', unitPrice: 10 }),
      product({ id: 3, name: 'C', unitPrice: 2 })
    ]);

    const priceHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Price')) as HTMLTableCellElement;
    const priceButton = priceHeader.querySelector('button') as HTMLButtonElement;
    priceButton.click();
    fixture.detectChanges();
    priceButton.click();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['B', 'A', 'C']);
  });

  it('sorts the status column both ascending and descending', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'A', isActive: true }),
      product({ id: 2, name: 'B', isActive: false }),
      product({ id: 3, name: 'C', isActive: true })
    ]);

    const statusHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Status')) as HTMLTableCellElement;
    const statusButton = statusHeader.querySelector('button') as HTMLButtonElement;

    statusButton.click();
    fixture.detectChanges();
    let rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['B', 'A', 'C']);

    statusButton.click();
    fixture.detectChanges();
    rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['C', 'A', 'B']);
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

  describe('in a support session (issue #661)', () => {
    const markAsOrdered = (host: HTMLElement) =>
      Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
        button.textContent?.includes('Mark as ordered')
      );
    const statusToggle = (host: HTMLElement) => host.querySelector<HTMLButtonElement>('tbody button.badge')!;

    it('hides Mark as ordered and disables the status toggle while a session is active', async () => {
      const { host } = await render([product({ id: 9, name: 'Widget' })], { sessionActive: of(true) });

      expect(markAsOrdered(host)).toBeUndefined();
      expect(statusToggle(host).disabled).toBe(true);
    });

    it('leaves both controls available while no session is active', async () => {
      const { host } = await render([product({ id: 9, name: 'Widget' })], { sessionActive: of(false) });

      expect(markAsOrdered(host)).toBeDefined();
      expect(statusToggle(host).disabled).toBe(false);
    });

    it('creates no supplier order from the guarded handler while a session is active', async () => {
      const create = jest.fn();
      const { fixture } = await render([product({ id: 9, name: 'Widget' })], { sessionActive: of(true), create });

      const component = fixture.componentInstance;
      component.selectedProductIds.add(9);
      component.orderForm.supplierId = 3;
      component.orderQuantities[9] = 4;
      component.saveOrder();

      expect(create).not.toHaveBeenCalled();
      expect(component.savingOrder).toBe(false);
    });

    it('does not open the order dialog while a session is active', async () => {
      const { fixture } = await render([product({ id: 9, name: 'Widget' })], { sessionActive: of(true) });

      fixture.componentInstance.selectedProductIds.add(9);
      fixture.componentInstance.openOrderForm();

      expect(fixture.componentInstance.showOrderForm).toBe(false);
    });

    it('updates no product status from the guarded handler while a session is active', async () => {
      const update = jest.fn();
      const { fixture } = await render([product({ id: 9, name: 'Widget' })], { sessionActive: of(true), update });

      fixture.componentInstance.toggleActive(product({ id: 9, name: 'Widget' }));

      expect(update).not.toHaveBeenCalled();
    });
  });
});
