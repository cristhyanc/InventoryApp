import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { ProductNeedsOrderingComponent } from './product-needs-ordering.component';
import { AccessService } from '../../access/access.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { ToastService } from '../../services/toast.service';
import { Product, Supplier } from '../../models/models';

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Product',
    sku: null,
    description: null,
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
    options: {
      sessionActive?: Observable<boolean>;
      update?: jest.Mock;
      create?: jest.Mock;
      suppliers?: Supplier[];
    } = {}
  ) {
    const navigate = jest.fn();
    const update = options.update ?? jest.fn(() => of(undefined));
    const create = options.create ?? jest.fn(() => of(undefined));
    await TestBed.configureTestingModule({
      imports: [ProductNeedsOrderingComponent],
      providers: [
        provideRouter([]),
        {
          provide: ProductService,
          useValue: {
            // Machine-aware, the mode a business with a usable provider gets (issue #726): the
            // storage-only listing and its label are this page's provider-optional spec's.
            getLowStock: () => of({ products, mode: 'MachineAware', machineNeedUnavailableReason: null }),
            update
          }
        },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: SupplierService, useValue: { getAll: () => of(options.suppliers ?? []) } },
        { provide: SupplierOrderService, useValue: { create } },
        // The purchase-cost columns of issue #790 have their own spec file; here they are simply
        // granted and answered with "no product has been bought", so the row and sort behaviour
        // these specs are about is unaffected by them.
        { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } },
        { provide: AccessService, useValue: { allows: () => of(true) } }
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

  /**
   * The name anchor is a real, focusable link with an accessible label, and the row is still not a
   * focusable `role="link"`. Where it points changed with issue #790 (D of #786): it is now a
   * Google search for the product rather than the product editor, which the row click still opens.
   * The encoding and the label are that issue's own spec file's.
   */
  it('gives keyboard users a real, labelled link in the Name cell instead of a focusable row with role="link"', async () => {
    const { host } = await render([product({ id: 9, name: 'Widget' })]);

    const row = host.querySelector('tbody tr') as HTMLTableRowElement;
    expect(row.hasAttribute('tabindex')).toBe(false);
    expect(row.hasAttribute('role')).toBe(false);

    const nameLink = row.querySelector('a.table-row-anchor') as HTMLAnchorElement;
    expect(nameLink.getAttribute('href')).toBe('https://www.google.com/search?q=Widget');
    expect(nameLink.getAttribute('aria-label')).toContain('Widget');
    expect(nameLink.textContent?.trim()).toBe('Widget');
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

  /**
   * There is no Status badge to click any more (issue #790): the list holds only active products,
   * so the column, its sort and the toggle behind it are gone. What used to be asserted about that
   * button's click not navigating is now asserted about the button not existing.
   */
  it('renders no Status badge button at all', async () => {
    const { host } = await render([product({ id: 9, name: 'Widget', isActive: true })]);

    const statusButton = Array.from(host.querySelectorAll('button')).find((b) =>
      ['Active', 'Inactive'].includes(b.textContent?.trim() ?? '')
    );
    expect(statusButton).toBeUndefined();
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
      product({ id: 1, name: 'A', quantityInStock: 9 }),
      product({ id: 2, name: 'B', quantityInStock: 10 }),
      product({ id: 3, name: 'C', quantityInStock: 2 })
    ]);

    const stockHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Stock')) as HTMLTableCellElement;
    (stockHeader.querySelector('button') as HTMLButtonElement).click();
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

  /**
   * The supplier controls this page used to carry (issue #787, A1 of #786): the "All Suppliers"
   * filter and the Supplier column with its sort button. A product carries no supplier, so none of
   * them can exist. The order dialog's own supplier selector is a different control and stays - see
   * the supplier-order tests below.
   */
  it('offers no supplier filter, column or sort over the products', async () => {
    const { host } = await render([product({ id: 1, name: 'A' })]);

    expect(host.textContent).not.toContain('All Suppliers');
    expect(
      Array.from(host.querySelectorAll('thead th')).some((th) => th.textContent?.includes('Supplier'))
    ).toBe(false);
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
      product({ id: 1, name: 'A', quantityInStock: 9 }),
      product({ id: 2, name: 'B', quantityInStock: 10 }),
      product({ id: 3, name: 'C', quantityInStock: 2 })
    ]);

    const stockHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Stock')) as HTMLTableCellElement;
    const stockButton = stockHeader.querySelector('button') as HTMLButtonElement;
    stockButton.click();
    fixture.detectChanges();
    stockButton.click();
    fixture.detectChanges();

    const rows = Array.from(host.querySelectorAll('tbody tr'));
    expect(rows.map((r) => r.querySelectorAll('td')[1]?.textContent?.trim())).toEqual(['B', 'A', 'C']);
  });

  /**
   * The Status column used to be sorted here. Issue #790 removed it, so what the page offers is
   * now asserted as a whole: the Price column left with issue #792, the Status column with #790,
   * and the two purchase-cost columns arrived immediately after "Need to Order".
   */
  it('offers exactly the columns issue #790 leaves, with no Price and no Status among them', async () => {
    const { host } = await render([product({ id: 1, name: 'A' })]);

    expect(Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.trim())).toEqual([
      '',
      'Name',
      'Category',
      'Stock',
      'Machine need',
      'Low Stock Threshold',
      'Restock To',
      'On order',
      'Need to Order',
      'Last purchase',
      'Cheapest purchase',
      ''
    ]);
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

  /**
   * The order dialog opens with no supplier chosen (issue #787, A1 of #786). It used to prefill the
   * selected products' own supplier when they all agreed; a product carries none now, so the
   * supplier being ordered from is an explicit choice. The selector stays, stays required, and is
   * populated from the supplier list - which is why this page still loads it.
   */
  it('opens the order dialog with no supplier chosen and a required supplier selector', async () => {
    const { fixture, host } = await render(
      [product({ id: 1, name: 'Banana', needToOrder: 4 })],
      { suppliers: [{ id: 3, name: 'Acme' }, { id: 4, name: 'Zenith' }] }
    );
    const component = fixture.componentInstance;

    component.toggleSelection(1, true);
    component.openOrderForm();
    fixture.detectChanges();

    expect(component.orderForm.supplierId).toBeNull();

    const selector = host.querySelector('[name="orderSupplier"]') as HTMLSelectElement;
    expect(selector).not.toBeNull();
    expect(selector.required).toBe(true);
    expect(Array.from(selector.options).map((option) => option.textContent?.trim())).toEqual([
      'Select supplier',
      'Acme',
      'Zenith'
    ]);
  });

  describe('in a support session (issue #661)', () => {
    const markAsOrdered = (host: HTMLElement) =>
      Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
        button.textContent?.includes('Mark as ordered')
      );
    /**
     * The status toggle this page disabled during a session is gone with issue #790's Status
     * column, so there is nothing left here for a session to disable: "Mark as ordered" is now
     * this page's only write.
     */
    const statusToggle = (host: HTMLElement) => host.querySelector<HTMLButtonElement>('tbody button.badge');

    it('hides Mark as ordered while a session is active, and has no status toggle left to disable', async () => {
      const { host } = await render([product({ id: 9, name: 'Widget' })], { sessionActive: of(true) });

      expect(markAsOrdered(host)).toBeUndefined();
      expect(statusToggle(host)).toBeNull();
    });

    it('leaves Mark as ordered available while no session is active', async () => {
      const { host } = await render([product({ id: 9, name: 'Widget' })], { sessionActive: of(false) });

      expect(markAsOrdered(host)).toBeDefined();
      expect(statusToggle(host)).toBeNull();
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

    /**
     * The page writes no product at all since issue #790 removed the status toggle, so the write
     * this used to guard cannot be reached from anywhere on it - during a session or outside one.
     */
    it('writes no product from this page at all, in a session or out of it', async () => {
      const update = jest.fn();
      const { host } = await render([product({ id: 9, name: 'Widget' })], { sessionActive: of(true), update });

      expect(update).not.toHaveBeenCalled();
      expect(
        Array.from(host.querySelectorAll('button')).some((button) =>
          ['Active', 'Inactive'].includes(button.textContent?.trim() ?? '')
        )
      ).toBe(false);
    });
  });
});
