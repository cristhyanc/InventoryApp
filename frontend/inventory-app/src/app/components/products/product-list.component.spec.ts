import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { ProductListComponent } from './product-list.component';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { ToastService } from '../../services/toast.service';
import { Product } from '../../models/models';
import { SupportSessionService } from '../../support-session/support-session.service';
import { AccessService } from '../../access/access.service';
import { Capability } from '../../access/capabilities';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { ProductPurchaseCostSummary } from '../../models/product-purchase-cost-summary';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { SpecConnectionStatus, provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';

/** A stub matching `AccessService.allows`'s one method, for every describe block below that does not itself care about capability gating. */
function accessServiceStub(allowed = true): Pick<AccessService, 'allows'> {
  return { allows: () => of(allowed) };
}

/**
 * The same stub, answering a different `allowed` per capability (issue #875), for the Import
 * products tests below that need `Imports.Run` and `Dashboard.ViewFinancials` to disagree.
 */
function accessServiceStubPerCapability(answers: Partial<Record<Capability, boolean>>): Pick<AccessService, 'allows'> {
  return { allows: (capability: Capability) => of(answers[capability] ?? true) };
}

/**
 * Every render() below provides the real `ProviderAvailabilityService` over a stubbed connection
 * read (issue #875's `provider-availability.spec-helpers`), because the component now asks it
 * whether Import products belongs in the toolbar. `'NotConfigured'` is the default for every block
 * that is not itself about that button, so none of them renders it and none of their assertions
 * changes; the Import products describe block below overrides it per test.
 */
const NOT_CONNECTED = provideNayaxConnectionStatus('NotConfigured');

/** A stub matching `ProductPurchaseCostService.getSummaries`, defaulting to no purchase history for anything asked. */
function purchaseCostServiceStub(getSummaries: jest.Mock = jest.fn(() => of([]))): Pick<ProductPurchaseCostService, 'getSummaries'> {
  return { getSummaries };
}

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
    needToOrder: 0,
    mdbCode: null,
    unit: 'ea',
    lastEatBefore1: null,
    lastEatBefore2: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId: null,
    category: null,
    isActive: true,
    isLowStock: false,
    isReorderAlert: false,
    // What the catalogue listing answers for an ordinary product nothing refers to (issue #737):
    // the delete is allowed and there is no refusal to show. The tests about an ineligible product
    // override it below.
    canDelete: true,
    deletionRefusal: null,
    ...overrides
  };
}

describe('ProductListComponent Actions column layout (issue #451)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[], sessionActive: Observable<boolean> = of(false)) {
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of(products) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: sessionActive } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
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

  it("keeps each row's Actions cell pinned to the right, opaque, and laid out on one line, without changing the action controls", async () => {
    const { host } = await render([product({ id: 42, name: 'Widget' })]);

    const row = host.querySelector('tbody tr') as HTMLTableRowElement;
    const cells = Array.from(row.querySelectorAll<HTMLTableCellElement>('td'));
    const actionsCell = cells[cells.length - 1];

    expect(Array.from(actionsCell.classList)).toEqual(expect.arrayContaining(['sticky', 'right-0', 'z-10', 'bg-white']));
    expect(Array.from(actionsCell.classList).filter((cssClass) => cssClass.startsWith('shadow'))).toEqual([]);

    // Issue #876: fixed slots on one line at every width, rather than wrapping onto a second line.
    const actionsWrapper = actionsCell.querySelector('div');
    expect(actionsWrapper).not.toBeNull();
    expect(Array.from(actionsWrapper!.classList)).toEqual(expect.arrayContaining(['flex-nowrap']));

    // Stock and Delete remain; Edit is gone from the Actions column (issue #497 moves it to the row).
    const stockLink = actionsCell.querySelector('a[href="/products/42/stock"]');
    const deleteButton = Array.from(actionsCell.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    expect(actionsCell.querySelector('a[href="/products/42/edit"]')).toBeNull();
    expect(stockLink?.textContent?.trim()).toBe('Stock');
    expect(deleteButton).toBeDefined();
  });

  it('preserves every product field already shown alongside the Actions column', async () => {
    const { fixture, host } = await render([
      product({
        id: 7,
        name: 'Widget',
        category: { id: 1, name: 'Snacks' },
        quantityInStock: 8,
        unit: 'ea',
        isActive: true,
        isLowStock: false
      })
    ]);
    // Status is shown only under "All" (issue #828); switched here so this field-preservation check
    // still covers it.
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();

    expect(host.textContent).toContain('Widget');
    expect(host.textContent).toContain('Snacks');
    expect(host.textContent).toContain('8 ea');
    expect(host.textContent).toContain('Active');
  });

  /**
   * Issue #792 (A2 of #786): a product carries no selling price, so no row renders a money value
   * at all on this page. The other columns are untouched.
   */
  it('renders no price for a product anywhere in the listing', async () => {
    const { host } = await render([product({ id: 7, name: 'Widget', category: { id: 1, name: 'Snacks' } })]);

    expect(host.querySelector('tbody')?.textContent).not.toContain('$');
  });
});

/**
 * Three fixed slots, always in the order Stock, Deactivate, Delete (issue #876), so an action lines
 * up under itself down the table regardless of which actions a given row offers. A slot whose
 * action does not apply keeps its width empty rather than disappearing - except during a support
 * session (issue #554), where the Deactivate/Delete slots are skipped entirely because no row can
 * ever show them.
 */
describe('ProductListComponent Actions fixed slots (issue #876)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[], sessionActive: Observable<boolean> = of(false)) {
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of(products) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: sessionActive } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host };
  }

  /** The Actions cell's slot `<div>`s, in DOM order, for the one rendered row. */
  function slotsOf(host: HTMLElement): HTMLElement[] {
    const row = host.querySelector('tbody tr') as HTMLTableRowElement;
    const cells = Array.from(row.querySelectorAll<HTMLTableCellElement>('td'));
    const actionsCell = cells[cells.length - 1];
    const wrapper = actionsCell.querySelector('div') as HTMLElement;
    return Array.from(wrapper.children) as HTMLElement[];
  }

  it('orders Stock, Deactivate and Delete for a row that offers all three', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: true, canDelete: true })]);

    const slots = slotsOf(host);
    expect(slots).toHaveLength(3);
    expect(slots[0].querySelector('a')?.textContent?.trim()).toBe('Stock');
    expect(slots[1].querySelector('button')?.textContent?.trim()).toBe('Deactivate');
    expect(slots[2].querySelector('button')?.textContent?.trim()).toBe('Delete');
  });

  it('keeps the Delete slot present but empty for a row that may not be deleted, with Deactivate still in its own slot', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: true, canDelete: false })]);

    const slots = slotsOf(host);
    expect(slots).toHaveLength(3);
    expect(slots[1].querySelector('button')?.textContent?.trim()).toBe('Deactivate');
    expect(slots[2].querySelector('button')).toBeNull();
  });

  it('keeps the Deactivate slot present but empty for an inactive product, with Delete still in its own slot', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: false, canDelete: true })]);

    const slots = slotsOf(host);
    expect(slots).toHaveLength(3);
    expect(slots[1].querySelector('button')).toBeNull();
    expect(slots[2].querySelector('button')?.textContent?.trim()).toBe('Delete');
  });

  it('renders only the Stock slot during a support session, reserving no width for actions the session can never show', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: true, canDelete: true })], of(true));

    const slots = slotsOf(host);
    expect(slots).toHaveLength(1);
    expect(slots[0].querySelector('a')?.textContent?.trim()).toBe('Stock');
    expect(host.querySelector('tbody')?.querySelector('button')).toBeNull();
  });
});

describe('ProductListComponent row navigation and sorting (issue #497)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[], sessionActive: Observable<boolean> = of(false)) {
    const navigate = jest.fn();
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of(products), update: () => of(undefined) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: sessionActive } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
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

    // The Price cell; it was the Supplier cell at this index until issue #787 removed that column.
    const plainCell = host.querySelectorAll('tbody tr td')[2] as HTMLTableCellElement;
    plainCell.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ...init }));

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
    const { fixture, host, navigate } = await render([product({ id: 9, name: 'Widget', isActive: true })]);
    // Status is shown only under "All" (issue #828).
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();

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
      product({ id: 1, name: 'A', quantityInStock: 9 }),
      product({ id: 2, name: 'B', quantityInStock: 10 }),
      product({ id: 3, name: 'C', quantityInStock: 2 })
    ]);

    const stockHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Stock')) as HTMLTableCellElement;
    (stockHeader.querySelector('button') as HTMLButtonElement).click();
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

/**
 * Delete and the Active/Inactive toggle while a support session is active (issue #554). A session
 * grants `InventoryOperate`, so these would otherwise still render - hiding and disabling them is
 * this page's own job, not the capability check's.
 */
describe('ProductListComponent write controls during a support session (issue #554)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[], sessionActive: Observable<boolean>) {
    const update = jest.fn(() => of(undefined));
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of(products), update } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: sessionActive } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    // Status is shown only under "All" (issue #828); this block's tests are about the status toggle,
    // so it is switched here rather than in each one.
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, update };
  }

  it('hides the Delete button and disables the status toggle while a session is active', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: true })], of(true));

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    const statusButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Active') as HTMLButtonElement;

    expect(deleteButton).toBeUndefined();
    expect(statusButton.disabled).toBe(true);
  });

  it('shows the Delete button and leaves the status toggle enabled while no session is active', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: true })], of(false));

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    const statusButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Active') as HTMLButtonElement;

    expect(deleteButton).toBeDefined();
    expect(statusButton.disabled).toBe(false);
  });

  it('does not open the delete confirmation or write the toggled status while a session is active', async () => {
    const { fixture, host, update } = await render([product({ id: 1, name: 'Widget', isActive: true })], of(true));
    const component = fixture.componentInstance;

    component.confirmDelete(product({ id: 1, name: 'Widget' }));
    component.toggleActive(product({ id: 1, name: 'Widget', isActive: true }));
    fixture.detectChanges();

    expect(component.confirmingProduct).toBeNull();
    expect(update).not.toHaveBeenCalled();
    expect(host.textContent).not.toContain('Confirm delete');
  });
});

/**
 * Delete is offered only where the API says it is allowed (issue #737).
 *
 * The catalogue listing publishes `canDelete` and, when it is false, the refusal the delete itself
 * would answer with - decided by the one `Inventory.Domain.Products.ProductDeletionPolicy`, over a
 * provider link and over the six kinds of history that refer to a product. So the row renders
 * Delete on `true` and, on anything else, nothing at all. An absent `canDelete` is not permission
 * either: a response that did not resolve the question gets no Delete.
 *
 * The answer is advisory - as fresh as the last fetch - so a delete the page offered can still be
 * refused, and that refusal has to reach the operator with the row left in place.
 *
 * Since issue #825 the row no longer *words* that refusal: the paragraph these tests used to assert
 * is gone from the list (it is shown once, on the Edit page), and the row offers **Deactivate**
 * instead of naming the Status toggle in prose. That is the intended behaviour change, and the
 * assertions below were replaced with it rather than weakened - see the issue #825 describe blocks.
 */
describe('ProductListComponent delete vs deactivate (issue #737)', () => {
  afterEach(() => TestBed.resetTestingModule());

  function deleteButtonOf(host: HTMLElement): HTMLButtonElement | undefined {
    return Array.from(host.querySelectorAll('tbody button')).find((b) => b.textContent?.trim() === 'Delete') as
      | HTMLButtonElement
      | undefined;
  }

  function deactivateButtonOf(host: HTMLElement): HTMLButtonElement | undefined {
    return Array.from(host.querySelectorAll('tbody button')).find((b) => b.textContent?.trim() === 'Deactivate') as
      | HTMLButtonElement
      | undefined;
  }

  async function render(deleteFn: jest.Mock, products: Product[] = [product({ id: 1, name: 'Widget' })]) {
    const toastService = { success: jest.fn(), error: jest.fn() };
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of(products), delete: deleteFn } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: toastService },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, toastService };
  }

  it('offers Delete for a product the API can allow it for, and removes it from the list on success', async () => {
    const deleteFn = jest.fn(() => of(undefined));
    const { fixture, host } = await render(deleteFn, [
      product({ id: 1, name: 'Widget', canDelete: true, providerManagedFields: [] })
    ]);

    expect(deleteButtonOf(host)).toBeDefined();

    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete') as HTMLButtonElement;
    deleteButton.click();
    fixture.detectChanges();
    const confirmButton = Array.from(host.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'Delete' && b.closest('.confirm-modal')
    ) as HTMLButtonElement;
    confirmButton.click();

    expect(deleteFn).toHaveBeenCalledWith(1);
  });

  it("explains why, through a toast, and keeps the product on screen when the API refuses the delete", async () => {
    const deleteFn = jest.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error:
              'This product cannot be deleted because its history refers to it (1 stock movement). ' +
              'Deactivate it instead - a deactivated product keeps its history, stays out of the reorder ' +
              'alerts and can be reactivated. Nothing was deleted.'
          })
      )
    );
    const { fixture, host, toastService } = await render(deleteFn);
    const component = fixture.componentInstance;

    component.confirmDelete(product({ id: 1, name: 'Widget' }));
    component.deleteConfirmed();
    fixture.detectChanges();

    expect(toastService.error).toHaveBeenCalledWith(
      'This product cannot be deleted because its history refers to it (1 stock movement). ' +
        'Deactivate it instead - a deactivated product keeps its history, stays out of the reorder ' +
        'alerts and can be reactivated. Nothing was deleted.'
    );
    expect(host.textContent).toContain('Widget');
  });

  it('does not offer Delete for a product whose history blocks it, and offers Deactivate alone without wording the refusal', async () => {
    const deleteFn = jest.fn(() => of(undefined));
    const { fixture, host } = await render(deleteFn, [
      product({
        id: 1,
        name: 'Widget',
        isActive: true,
        canDelete: false,
        deletionRefusal:
          'This product cannot be deleted because its history refers to it (1 stock movement). ' +
          'Deactivate it instead - a deactivated product keeps its history, stays out of the reorder ' +
          'alerts and can be reactivated. Nothing was deleted.'
      })
    ]);
    // Status is shown only under "All" (issue #828).
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();

    expect(deleteButtonOf(host)).toBeUndefined();
    // Issue #825: the refusal is shown once, on the Edit page - not as a paragraph on every row.
    expect(host.textContent).not.toContain('its history refers to it (1 stock movement)');
    expect(host.textContent).not.toContain('Use the Status toggle to deactivate it.');
    expect(deactivateButtonOf(host)).toBeDefined();
    // The row's own toggle is still offered and usable beside it.
    const statusButton = Array.from(host.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'Active'
    ) as HTMLButtonElement;
    expect(statusButton.disabled).toBe(false);
  });

  it('does not offer Delete for a provider-linked product, and offers Deactivate instead', async () => {
    const deleteFn = jest.fn(() => of(undefined));
    const { fixture, host } = await render(deleteFn, [
      product({
        id: 1,
        name: 'Coke',
        isActive: true,
        providerManagedFields: ['name', 'categoryId'],
        canDelete: false,
        deletionRefusal:
          'This product is managed by Nayax and cannot be deleted: the next catalogue import would ' +
          'create it again. Deactivate it instead - a deactivated product keeps its history, stays ' +
          'out of the reorder alerts and can be reactivated. Nothing was deleted.'
      })
    ]);
    // Status is shown only under "All" (issue #828).
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();

    expect(deleteButtonOf(host)).toBeUndefined();
    expect(host.textContent).not.toContain('managed by Nayax and cannot be deleted');
    expect(deactivateButtonOf(host)).toBeDefined();
    const statusButton = Array.from(host.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'Active'
    ) as HTMLButtonElement;
    expect(statusButton.disabled).toBe(false);
  });

  it('offers neither Delete nor Deactivate for an undeletable product that is already retired', async () => {
    const deleteFn = jest.fn(() => of(undefined));
    const { fixture, host } = await render(deleteFn, [
      product({
        id: 1,
        name: 'Coke',
        isActive: false,
        canDelete: false,
        deletionRefusal: 'This product cannot be deleted because its history refers to it (2 sales).'
      })
    ]);
    // Status is shown only under "All" (issue #828); this product is retired, so viewing it at all
    // on this page already means "All" is selected.
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();

    expect(deleteButtonOf(host)).toBeUndefined();
    expect(deactivateButtonOf(host)).toBeUndefined();
    expect(host.textContent).not.toContain('It is deactivated; use the Status toggle to reactivate it.');
    // Reactivation is still one click on the row's own toggle.
    expect(
      Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Inactive')
    ).toBeDefined();
  });

  /**
   * A response that never resolved the question (a reorder listing, an older server) says nothing,
   * and nothing is not permission: the row offers no delete whose outcome is unknown, and the
   * Deactivate path - which is always available for an active product - instead.
   */
  it('does not offer Delete when the response did not say whether the product may be deleted', async () => {
    const deleteFn = jest.fn(() => of(undefined));
    const unanswered = product({ id: 1, name: 'Widget', canDelete: undefined, deletionRefusal: undefined });
    const { fixture, host } = await render(deleteFn, [unanswered]);

    expect(deleteButtonOf(host)).toBeUndefined();
    expect(deactivateButtonOf(host)).toBeDefined();

    fixture.componentInstance.confirmDelete(unanswered);
    expect(deleteFn).not.toHaveBeenCalled();
  });

  it('refuses to open the delete confirmation for an undeletable product, so the write is unavailable rather than merely hidden', async () => {
    const deleteFn = jest.fn(() => of(undefined));
    const blocked = product({
      id: 1,
      name: 'Coke',
      canDelete: false,
      deletionRefusal: 'This product cannot be deleted because its history refers to it (1 sale).'
    });
    const { fixture, host } = await render(deleteFn, [blocked]);
    const component = fixture.componentInstance;

    component.confirmDelete(blocked);
    fixture.detectChanges();

    expect(component.confirmingProduct).toBeNull();
    expect(host.textContent).not.toContain('Confirm delete');
    expect(deleteFn).not.toHaveBeenCalled();
  });

  it('still surfaces the server refusal when a product that looked deletable is refused, because the eligibility it rendered can be stale', async () => {
    // The row was fetched while it was still deletable - `canDelete: true` - and then gained a sale
    // (or the provider's catalogue import named it). The server counts again inside the delete's own
    // transaction and refuses, which is why that recheck is kept and why its message must be shown.
    const refusal =
      'This product is managed by Nayax and cannot be deleted: the next catalogue import would ' +
      'create it again. Deactivate it instead - a deactivated product keeps its history, stays out ' +
      'of the reorder alerts and can be reactivated. Nothing was deleted.';
    const deleteFn = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 409, error: refusal })));
    const stale = product({ id: 1, name: 'Widget', canDelete: true, providerManagedFields: [] });
    const { fixture, host, toastService } = await render(deleteFn, [stale]);

    expect(deleteButtonOf(host)).toBeDefined();
    fixture.componentInstance.confirmDelete(stale);
    fixture.componentInstance.deleteConfirmed();
    fixture.detectChanges();

    expect(deleteFn).toHaveBeenCalledWith(1);
    expect(toastService.error).toHaveBeenCalledWith(refusal);
    expect(host.textContent).toContain('Widget');
  });
});

/**
 * The Status filter (issue #791, E of #786, replacing the "Show retired" checkbox issue #737
 * added): "Only active" (the default) so the catalogue page still answers the working catalogue,
 * and "All" on demand so a product just deactivated through the Active/Inactive toggle stays
 * reachable to reactivate instead of disappearing from the only screen that can flip it back.
 */
describe('ProductListComponent Status filter (issue #791)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(getAll: jest.Mock) {
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  function statusSelect(host: HTMLElement): HTMLSelectElement {
    return host.querySelector('[name="statusFilter"]') as HTMLSelectElement;
  }

  it('lists only active products by default, offering "Only active" and "All"', async () => {
    const getAll = jest.fn(() => of([]));
    const { host } = await render(getAll);

    expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ includeRetired: false }));
    const options = Array.from(statusSelect(host).querySelectorAll('option')).map((option) => option.textContent?.trim());
    expect(options).toEqual(['Only active', 'All']);
    expect(statusSelect(host).value).toBe('active');
    expect(host.textContent).not.toContain('Show retired');
  });

  it('asks for retired products too once "All" is selected', async () => {
    const getAll = jest.fn(() => of([]));
    const { fixture, host } = await render(getAll);

    statusSelect(host).value = 'all';
    statusSelect(host).dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ includeRetired: true }));
  });

  it('resets the Status filter back to "Only active" along with the other filters', async () => {
    const getAll = jest.fn(() => of([]));
    const { fixture, host } = await render(getAll);

    statusSelect(host).value = 'all';
    statusSelect(host).dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(fixture.componentInstance.statusFilter).toBe('all');

    (Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Reset') as HTMLButtonElement).click();
    fixture.detectChanges();
    // NgModel's model-to-view write for a standalone control is a deferred microtask (to avoid
    // ExpressionChangedAfterItHasBeenCheckedError), so the reset state needs one more tick.
    await Promise.resolve();
    fixture.detectChanges();

    expect(fixture.componentInstance.statusFilter).toBe('active');
    expect(statusSelect(host).value).toBe('active');
  });

  /**
   * The supplier controls All Products used to carry (issue #787, A1 of #786): the "All Suppliers"
   * filter, the Supplier column and its sort button. A product carries no supplier, so none of them
   * can exist - and nothing on the page loads the supplier list either, which is why the TestBed
   * above provides no `SupplierService`.
   *
   * The Price column and its sort went the same way in issue #792 (A2): a product carries no
   * selling price either. Issue #791 (E of #786) then added the Average cost, Last purchase and
   * Cheapest purchase columns, so the header row is exactly these seven columns and the Actions
   * cell when `Dashboard.ViewFinancials` is granted, as the default stub here grants it.
   */
  it('offers no supplier or price filter, column or sort', async () => {
    const { fixture, host } = await render(jest.fn(() => of([product({ id: 7, name: 'Widget' })])));

    expect(host.textContent).not.toContain('All Suppliers');
    // Status is excluded here: under "Only active" (the default) it is hidden entirely (issue #828).
    expect(
      Array.from(host.querySelectorAll('thead th')).map((header) => header.textContent?.trim())
    ).toEqual(['Name', 'Category', 'Stock', 'Average cost', 'Last purchase', 'Cheapest purchase', '']);

    statusSelect(host).value = 'all';
    statusSelect(host).dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(
      Array.from(host.querySelectorAll('thead th')).map((header) => header.textContent?.trim())
    ).toEqual(['Name', 'Category', 'Stock', 'Status', 'Average cost', 'Last purchase', 'Cheapest purchase', '']);
  });
});

/**
 * The Status column is shown only while the Status filter is "All" (issue #828): under "Only
 * active" - the default, including on first load and after Reset - every row would say "Active"
 * anyway, so the column (and a sort on it) would be redundant and is hidden instead. The Status
 * toggle (issue #825) still works exactly as before once "All" is selected, so a just-deactivated
 * product stays reachable to reactivate.
 */
describe('ProductListComponent Status column visibility (issue #828)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[]) {
    const getAll = jest.fn(() => of(products));
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, getAll };
  }

  function statusSelect(host: HTMLElement): HTMLSelectElement {
    return host.querySelector('[name="statusFilter"]') as HTMLSelectElement;
  }

  function selectStatusFilter(fixture: ComponentFixture<ProductListComponent>, host: HTMLElement, value: 'active' | 'all'): void {
    statusSelect(host).value = value;
    statusSelect(host).dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function headerLabels(host: HTMLElement): (string | undefined)[] {
    return Array.from(host.querySelectorAll('thead th')).map((header) => header.textContent?.trim());
  }

  it('hides the Status header and every row\'s Status cell on first load ("Only active")', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: true })]);

    expect(headerLabels(host)).not.toContain('Status');
    expect(Array.from(host.querySelectorAll('tbody button')).find((b) => b.textContent?.trim() === 'Active')).toBeUndefined();
  });

  it('shows the Status header and cells once "All" is selected, with the toggle still usable', async () => {
    const { fixture, host, getAll } = await render([product({ id: 1, name: 'Widget', isActive: true })]);

    selectStatusFilter(fixture, host, 'all');

    expect(headerLabels(host)).toContain('Status');
    const statusButton = Array.from(host.querySelectorAll('tbody button')).find((b) => b.textContent?.trim() === 'Active') as
      | HTMLButtonElement
      | undefined;
    expect(statusButton).toBeDefined();
    expect(statusButton!.disabled).toBe(false);
    expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ includeRetired: true }));
  });

  it('hides the Status column again after Reset', async () => {
    const { fixture, host } = await render([product({ id: 1, name: 'Widget', isActive: true })]);

    selectStatusFilter(fixture, host, 'all');
    expect(headerLabels(host)).toContain('Status');

    (Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Reset') as HTMLButtonElement).click();
    fixture.detectChanges();
    // NgModel's model-to-view write for a standalone control is a deferred microtask (to avoid
    // ExpressionChangedAfterItHasBeenCheckedError), so the reset state needs one more tick.
    await Promise.resolve();
    fixture.detectChanges();

    expect(headerLabels(host)).not.toContain('Status');
  });

  it('keeps the header and row cell counts equal whether the Status column is hidden or shown', async () => {
    const { fixture, host } = await render([product({ id: 1, name: 'Widget', isActive: true })]);

    const headerCount = () => host.querySelectorAll('thead th').length;
    const rowCellCount = () => (host.querySelector('tbody tr') as HTMLTableRowElement).querySelectorAll('td').length;
    expect(headerCount()).toBe(rowCellCount());

    selectStatusFilter(fixture, host, 'all');

    expect(headerCount()).toBe(rowCellCount());
  });

  it('clears a Status sort back to the default order when switching to "Only active"', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'B', isActive: false }),
      product({ id: 2, name: 'A', isActive: true })
    ]);
    const component = fixture.componentInstance;

    selectStatusFilter(fixture, host, 'all');

    const statusHeader = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Status')) as HTMLTableCellElement;
    (statusHeader.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(component.sortColumn).toBe('status');

    selectStatusFilter(fixture, host, 'active');

    expect(component.sortColumn).toBeNull();
    // Back to the order the products were fetched in, not a sort on a now-hidden column.
    expect(Array.from(host.querySelectorAll('tbody tr')).map((row) => row.querySelector('td')?.textContent?.trim())).toEqual([
      'B',
      'A'
    ]);
  });
});

/**
 * Average cost, Last purchase and Cheapest purchase (issue #791, E of #786). Average cost is
 * `averageUnitCostForDisplay` (issue #788) and is shown to every role; Last/Cheapest purchase come
 * from one `ProductPurchaseCostService.getSummaries` call for the listed ids and require
 * `Dashboard.ViewFinancials`. None of these values is computed in Angular - only presented.
 */
describe('ProductListComponent average and purchase cost columns (issue #791)', () => {
  afterEach(() => TestBed.resetTestingModule());

  function summary(productId: number, overrides: Partial<ProductPurchaseCostSummary> = {}): ProductPurchaseCostSummary {
    return {
      productId,
      lastUnitCost: 2.4,
      lastSupplierName: 'Acme Supplies',
      lastPurchaseDate: '2026-03-01T00:00:00',
      lowestUnitCost: 1.1,
      lowestSupplierName: 'Bravo Co',
      lowestPurchaseDate: '2026-01-01T00:00:00',
      ...overrides
    };
  }

  async function render(products: Product[], options: { getSummaries?: jest.Mock; allowed?: boolean } = {}) {
    const getSummaries = options.getSummaries ?? jest.fn(() => of([]));
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of(products) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: accessServiceStub(options.allowed ?? true) },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub(getSummaries) }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    // Status is shown only under "All" (issue #828); switched here so this block's existing
    // column-index assertions keep describing the same cells.
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, getSummaries };
  }

  function rowCells(host: HTMLElement, rowIndex = 0): HTMLTableCellElement[] {
    const row = host.querySelectorAll('tbody tr')[rowIndex] as HTMLTableRowElement;
    return Array.from(row.querySelectorAll('td'));
  }

  it('shows an em dash for an unestablished average cost and $0.00 for a known zero, never reading the legacy default as known', async () => {
    const { host } = await render([
      product({ id: 1, name: 'Unknown', averageUnitCost: 7, averageUnitCostForDisplay: undefined }),
      product({ id: 2, name: 'Zero', averageUnitCost: 0, averageUnitCostForDisplay: 0 }),
      product({ id: 3, name: 'Known', averageUnitCost: 3.5, averageUnitCostForDisplay: 3.5 })
    ]);

    const averageCostCells = Array.from(host.querySelectorAll('tbody tr')).map(
      (row) => (row.querySelectorAll('td')[4] as HTMLTableCellElement).textContent?.trim()
    );
    expect(averageCostCells).toEqual(['—', '$0.00', '$3.50']);
  });

  it('requests last/cheapest purchase costs once for the listed ids and shows each cost with its supplier', async () => {
    const getSummaries = jest.fn(() =>
      of([summary(1), summary(2, { lastSupplierName: null, lowestUnitCost: 0, lowestSupplierName: null })])
    );
    const { host } = await render([product({ id: 1, name: 'A' }), product({ id: 2, name: 'B' })], { getSummaries });

    expect(getSummaries).toHaveBeenCalledTimes(1);
    expect(getSummaries).toHaveBeenCalledWith([1, 2]);

    const rowA = rowCells(host, 0);
    expect(rowA[5].textContent).toContain('$2.40');
    expect(rowA[5].textContent).toContain('Acme Supplies');
    expect(rowA[6].textContent).toContain('$1.10');
    expect(rowA[6].textContent).toContain('Bravo Co');

    const rowB = rowCells(host, 1);
    // No supplier recorded on the last purchase: "None", never omitted and never another purchase's supplier.
    expect(rowB[5].textContent).toContain('$2.40');
    expect(rowB[5].textContent).toContain('None');
    // A genuine $0 cheapest cost, with its own unknown supplier - never an em dash.
    expect(rowB[6].textContent).toContain('$0.00');
    expect(rowB[6].textContent).toContain('None');
  });

  it('shows an em dash and no supplier line for a product absent from the purchase-cost answer', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget' })], { getSummaries: jest.fn(() => of([])) });

    const row = rowCells(host, 0);
    expect(row[5].textContent?.trim()).toBe('—');
    expect(row[6].textContent?.trim()).toBe('—');
  });

  it('hides the Last and Cheapest purchase columns and makes no request without Dashboard.ViewFinancials, while still showing Average cost', async () => {
    const getSummaries = jest.fn(() => of([summary(1)]));
    const { host } = await render([product({ id: 1, name: 'Widget', averageUnitCostForDisplay: 4 })], {
      getSummaries,
      allowed: false
    });

    expect(getSummaries).not.toHaveBeenCalled();
    expect(host.textContent).not.toContain('Last purchase');
    expect(host.textContent).not.toContain('Cheapest purchase');
    expect(host.querySelector('tbody')?.textContent).toContain('$4.00');
  });

  it('shows Unavailable with a Retry control once the purchase-cost read fails, and Retry reloads it', async () => {
    const getSummaries = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('boom')))
      .mockReturnValueOnce(of([summary(1)]));
    const { fixture, host } = await render([product({ id: 1, name: 'Widget' })], { getSummaries });

    let row = rowCells(host, 0);
    expect(row[5].textContent).toContain('Unavailable');
    expect(row[6].textContent).toContain('Unavailable');
    const retryButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Retry') as
      | HTMLButtonElement
      | undefined;
    expect(retryButton).toBeDefined();

    retryButton!.click();
    fixture.detectChanges();

    expect(getSummaries).toHaveBeenCalledTimes(2);
    row = rowCells(host, 0);
    expect(row[5].textContent).toContain('$2.40');
    expect(Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Retry')).toBeUndefined();
  });

  /**
   * Issue #191's page composition boundary: the purchase-cost read is a workflow of its own, so the
   * page hands the ids it displays to `<app-product-purchase-costs>` and lays out what that
   * component publishes, instead of owning the read, its state, its capability gate and its Retry
   * control itself.
   */
  it('composes the purchase-cost read as a feature component, which owns the failure message and Retry', async () => {
    const getSummaries = jest.fn().mockReturnValueOnce(throwError(() => new Error('boom')));
    const { host } = await render([product({ id: 1, name: 'Widget' })], { getSummaries });

    const costs = host.querySelector('app-product-purchase-costs');
    expect(costs).not.toBeNull();
    expect(costs!.textContent).toContain('could not be loaded');
    expect(Array.from(costs!.querySelectorAll('button')).map((b) => b.textContent?.trim())).toContain('Retry');
    // The page's own markup carries neither of them - it only renders the cells.
    expect(host.querySelector('table')?.textContent).not.toContain('Retry');
  });

  it('shows Loading while the read is in flight, distinct from a successful no-history answer', async () => {
    const pending = new Subject<ProductPurchaseCostSummary[]>();
    const { fixture, host } = await render([product({ id: 1, name: 'Widget' })], {
      getSummaries: jest.fn(() => pending.asObservable())
    });

    expect(rowCells(host, 0)[5].textContent).toContain('Loading');

    pending.next([]);
    pending.complete();
    fixture.detectChanges();

    expect(rowCells(host, 0)[5].textContent?.trim()).toBe('—');
  });

  it('does not let a slow or failed purchase-cost response for an earlier product set overwrite a newer one', async () => {
    const firstAnswer = new Subject<ProductPurchaseCostSummary[]>();
    const getAll = jest
      .fn()
      .mockReturnValueOnce(of([product({ id: 1, name: 'First' })]))
      .mockReturnValueOnce(of([product({ id: 2, name: 'Second' })]));
    const getSummaries = jest
      .fn()
      .mockReturnValueOnce(firstAnswer.asObservable())
      .mockReturnValueOnce(of([summary(2)]));

    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub(getSummaries) }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    // Status is shown only under "All" (issue #828); switched here so this test's column-index
    // assertions keep describing the same cells.
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    // A second filter-driven fetch lands (the second product set) while the first set's
    // purchase-cost read is still outstanding.
    fixture.componentInstance.applyFilters();
    fixture.detectChanges();

    // The first set's read is no longer subscribed to, so its late answer must be dropped rather
    // than landing under the second set's row.
    firstAnswer.next([summary(1, { lastUnitCost: 99 })]);
    firstAnswer.complete();
    fixture.detectChanges();

    const row = rowCells(host, 0);
    expect(row[5].textContent).toContain('$2.40');
    expect(row[5].textContent).not.toContain('99');
  });

  /**
   * "Where sortable, missing/unavailable costs sort last in both directions" - a no-history
   * product on Average cost/Last purchase/Cheapest purchase stays last whichever way the column
   * is toggled, exactly like the existing Category column's missing-value rule.
   */
  it('sorts by average cost numerically, with an unestablished cost last in both directions', async () => {
    const { fixture, host } = await render([
      product({ id: 1, name: 'A', averageUnitCostForDisplay: 5 }),
      product({ id: 2, name: 'B', averageUnitCostForDisplay: undefined }),
      product({ id: 3, name: 'C', averageUnitCostForDisplay: 1 })
    ]);

    const header = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Average cost')) as HTMLTableCellElement;
    const button = header.querySelector('button') as HTMLButtonElement;

    button.click();
    fixture.detectChanges();
    let names = Array.from(host.querySelectorAll('tbody tr')).map((row) => row.querySelector('td')?.textContent?.trim());
    expect(names).toEqual(['C', 'A', 'B']);

    button.click();
    fixture.detectChanges();
    names = Array.from(host.querySelectorAll('tbody tr')).map((row) => row.querySelector('td')?.textContent?.trim());
    expect(names).toEqual(['A', 'C', 'B']);
  });

  it('sorts by last purchase cost, with a product absent from the purchase-cost answer last in both directions', async () => {
    const getSummaries = jest.fn(() => of([summary(1, { lastUnitCost: 5 }), summary(3, { lastUnitCost: 1 })]));
    const { fixture, host } = await render(
      [product({ id: 1, name: 'A' }), product({ id: 2, name: 'B' }), product({ id: 3, name: 'C' })],
      { getSummaries }
    );

    const header = Array.from(host.querySelectorAll('thead th')).find((th) => th.textContent?.includes('Last purchase')) as HTMLTableCellElement;
    const button = header.querySelector('button') as HTMLButtonElement;

    button.click();
    fixture.detectChanges();
    let names = Array.from(host.querySelectorAll('tbody tr')).map((row) => row.querySelector('td')?.textContent?.trim());
    expect(names).toEqual(['C', 'A', 'B']);

    button.click();
    fixture.detectChanges();
    names = Array.from(host.querySelectorAll('tbody tr')).map((row) => row.querySelector('td')?.textContent?.trim());
    expect(names).toEqual(['A', 'C', 'B']);
  });
});

/**
 * New Product in the All Products toolbar (issue #825). It used to sit in the shared
 * `products-shell` header, where it also appeared on Needs Ordering - a view that orders existing
 * products and never creates one. It belongs to this page, right-aligned beside the filters that
 * are already here, and it is still hidden while a support session is active for exactly the reason
 * the shell hid it: a session grants `InventoryOperate`, so this write would otherwise render.
 */
describe('ProductListComponent New Product in the toolbar (issue #825)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(sessionActive: Observable<boolean> = of(false)) {
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of([product({ id: 1, name: 'Widget' })]) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: sessionActive } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  function toolbar(host: HTMLElement): HTMLElement {
    return host.querySelector('.card .card-body') as HTMLElement;
  }

  it('renders New Product inside the search/filter toolbar, linking to the create route', async () => {
    const { host } = await render();

    const newProduct = toolbar(host).querySelector('a[href="/products/new"]') as HTMLAnchorElement;
    expect(newProduct).not.toBeNull();
    expect(newProduct.textContent?.trim()).toBe('New Product');
  });

  it('keeps search, category, status, Reset and New Product reachable in that keyboard order', async () => {
    const { host } = await render();

    // Each control named by whatever identifies it: the search placeholder, the category select's
    // only option, the status select's name, then the two action labels.
    const order = Array.from(toolbar(host).querySelectorAll<HTMLElement>('input, select, button, a')).map(
      (control) => control.getAttribute('placeholder') ?? control.getAttribute('name') ?? control.textContent?.trim()
    );
    expect(order).toEqual(['Search name or SKU', 'All Categories', 'statusFilter', 'Reset', 'New Product']);
    // No control is taken out of the tab order or made unreachable to get the layout.
    expect(toolbar(host).querySelector('[tabindex="-1"]')).toBeNull();
  });

  it('lets the toolbar wrap instead of overflowing, and spaces it from the table card', async () => {
    const { host } = await render();

    const toolbarCard = host.querySelector('.card') as HTMLElement;
    expect(Array.from(toolbar(host).classList)).toEqual(expect.arrayContaining(['flex', 'flex-wrap']));
    // One existing Tailwind spacing step (1.5rem) between the toolbar card and the table card.
    expect(Array.from(toolbarCard.classList)).toContain('mb-6');
    // Nothing on the toolbar introduces a horizontal scroll area of its own.
    expect(Array.from(toolbar(host).classList)).not.toContain('overflow-x-auto');
  });

  it('hides New Product while a support session is active', async () => {
    const { host } = await render(of(true));

    expect(host.querySelector('a[href="/products/new"]')).toBeNull();
  });
});

/**
 * Deactivate, with a confirmation (issue #825). Deactivation is reversible and keeps everything,
 * but it does take a product out of the working catalogue, the product pickers and the reorder
 * alerts, so it is confirmed rather than being a single unexplained click on a status badge. It
 * writes through the one existing update flow - `productService.update` with `isActive: false` -
 * and never through a second endpoint or a second rule.
 */
describe('ProductListComponent Deactivate (issue #825)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(products: Product[], options: { update?: jest.Mock; sessionActive?: Observable<boolean> } = {}) {
    const update = options.update ?? jest.fn(() => of(undefined));
    const getAll = jest.fn(() => of(products));
    const toastService = { success: jest.fn(), error: jest.fn() };
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll, update, delete: jest.fn(() => of(undefined)) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: toastService },
        { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } },
        { provide: AccessService, useValue: accessServiceStub() },
        NOT_CONNECTED,
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, update, getAll, toastService };
  }

  function rowButton(host: HTMLElement, label: string): HTMLButtonElement | undefined {
    return Array.from(host.querySelectorAll('tbody button')).find((b) => b.textContent?.trim() === label) as
      | HTMLButtonElement
      | undefined;
  }

  function dialogButton(host: HTMLElement, label: string): HTMLButtonElement | undefined {
    return Array.from(host.querySelectorAll('.confirm-modal button')).find((b) => b.textContent?.trim() === label) as
      | HTMLButtonElement
      | undefined;
  }

  it('offers Delete and Deactivate for a deletable active product', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: true, canDelete: true })]);

    expect(rowButton(host, 'Delete')).toBeDefined();
    expect(rowButton(host, 'Deactivate')).toBeDefined();
  });

  it('offers Delete but no Deactivate for a deletable product that is already inactive', async () => {
    const { host } = await render([product({ id: 1, name: 'Widget', isActive: false, canDelete: true })]);

    expect(rowButton(host, 'Delete')).toBeDefined();
    expect(rowButton(host, 'Deactivate')).toBeUndefined();
  });

  it('names the product and what deactivation keeps, in the shared confirmation dialog', async () => {
    const { fixture, host } = await render([product({ id: 1, name: 'Widget', isActive: true })]);

    rowButton(host, 'Deactivate')!.click();
    fixture.detectChanges();

    const dialog = host.querySelector('.confirm-modal') as HTMLElement;
    expect(dialog).not.toBeNull();
    expect(dialog.textContent).toContain('Widget');
    expect(dialog.textContent).toContain('working catalogue');
    expect(dialog.textContent).toContain('reorder alerts');
    expect(dialog.textContent).toContain('history, stock and costs');
    expect(dialog.textContent).toContain('reactivated');
    expect(dialogButton(host, 'Deactivate')).toBeDefined();
    expect(dialogButton(host, 'Cancel')).toBeDefined();
  });

  it('writes isActive: false through the existing update flow on confirm, and refreshes the list', async () => {
    const { fixture, host, update, getAll } = await render([
      product({
        id: 42,
        name: 'Widget',
        isActive: true,
        sku: 'SKU-1',
        description: 'A widget',
        lowStockThreshold: 3,
        restockTo: 9,
        unit: 'bag',
        categoryId: 2
      })
    ]);

    rowButton(host, 'Deactivate')!.click();
    fixture.detectChanges();
    dialogButton(host, 'Deactivate')!.click();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledWith(42, {
      name: 'Widget',
      sku: 'SKU-1',
      description: 'A widget',
      lowStockThreshold: 3,
      restockTo: 9,
      unit: 'bag',
      categoryId: 2,
      isActive: false
    });
    // The initial load, then the refresh that takes the product out of the "Only active" view.
    expect(getAll).toHaveBeenCalledTimes(2);
    expect(host.querySelector('.confirm-modal')).toBeNull();
  });

  it('writes nothing when the confirmation is cancelled', async () => {
    const { fixture, host, update, getAll } = await render([product({ id: 1, name: 'Widget', isActive: true })]);

    rowButton(host, 'Deactivate')!.click();
    fixture.detectChanges();
    dialogButton(host, 'Cancel')!.click();
    fixture.detectChanges();

    expect(update).not.toHaveBeenCalled();
    expect(getAll).toHaveBeenCalledTimes(1);
    expect(host.querySelector('.confirm-modal')).toBeNull();
    expect(rowButton(host, 'Deactivate')).toBeDefined();
  });

  it('shows the API error and leaves the row alone when the deactivation is refused', async () => {
    const update = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: 'The selected category does not exist.' }))
    );
    const { fixture, host, toastService, getAll } = await render([product({ id: 1, name: 'Widget', isActive: true })], {
      update
    });

    fixture.componentInstance.confirmDeactivate(product({ id: 1, name: 'Widget', isActive: true }));
    fixture.componentInstance.deactivateConfirmed();
    fixture.detectChanges();

    expect(toastService.error).toHaveBeenCalledWith('The selected category does not exist.');
    expect(getAll).toHaveBeenCalledTimes(1);
    expect(host.textContent).toContain('Widget');
    expect(rowButton(host, 'Deactivate')).toBeDefined();
  });

  it('falls back to its own message when the refusal carries no API error', async () => {
    const update = jest.fn(() => throwError(() => new Error('boom')));
    const { fixture, toastService } = await render([product({ id: 1, name: 'Widget', isActive: true })], { update });

    fixture.componentInstance.confirmDeactivate(product({ id: 1, name: 'Widget', isActive: true }));
    fixture.componentInstance.deactivateConfirmed();

    expect(toastService.error).toHaveBeenCalledWith('Failed to deactivate product.');
  });

  it('confirms a deactivation started from the Status toggle, instead of writing it straight away', async () => {
    const { fixture, host, update } = await render([product({ id: 1, name: 'Widget', isActive: true })]);
    // Status is shown only under "All" (issue #828).
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();

    (
      Array.from(host.querySelectorAll('tbody button')).find((b) => b.textContent?.trim() === 'Active') as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    expect(update).not.toHaveBeenCalled();
    expect(host.querySelector('.confirm-modal')?.textContent).toContain('Widget');

    dialogButton(host, 'Deactivate')!.click();

    expect(update).toHaveBeenCalledWith(1, expect.objectContaining({ isActive: false }));
  });

  it('reactivates from the Status toggle in one click, with no confirmation', async () => {
    const { fixture, host, update } = await render([product({ id: 1, name: 'Widget', isActive: false })]);
    // Status is shown only under "All" (issue #828); this product is retired, so viewing it at all
    // on this page already means "All" is selected.
    fixture.componentInstance.statusFilter = 'all';
    fixture.detectChanges();

    (
      Array.from(host.querySelectorAll('tbody button')).find(
        (b) => b.textContent?.trim() === 'Inactive'
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledWith(1, expect.objectContaining({ isActive: true }));
    expect(host.querySelector('.confirm-modal')).toBeNull();
  });

  it('hides Deactivate while a support session is active, and refuses to open its confirmation from code', async () => {
    const active = product({ id: 1, name: 'Widget', isActive: true });
    const { fixture, host, update } = await render([active], { sessionActive: of(true) });

    expect(rowButton(host, 'Deactivate')).toBeUndefined();

    fixture.componentInstance.confirmDeactivate(active);
    fixture.componentInstance.deactivateConfirmed();
    fixture.detectChanges();

    expect(fixture.componentInstance.confirmingDeactivation).toBeNull();
    expect(update).not.toHaveBeenCalled();
    expect(host.querySelector('.confirm-modal')).toBeNull();
  });

  it('refuses to open the confirmation for a product that is already inactive', async () => {
    const inactive = product({ id: 1, name: 'Widget', isActive: false });
    const { fixture, update } = await render([inactive]);

    fixture.componentInstance.confirmDeactivate(inactive);

    expect(fixture.componentInstance.confirmingDeactivation).toBeNull();
    expect(update).not.toHaveBeenCalled();
  });
});

/**
 * Import products beside New Product on All Products (issue #875): reachable only while the
 * member may run imports, the shared provider-availability signal says the provider tools and the
 * `productCatalogue` feature are both offered, and no support session is active. New Product's own
 * visibility (issue #825) is untouched by any of this.
 */
describe('ProductListComponent Import products in the toolbar (issue #875)', () => {
  afterEach(() => TestBed.resetTestingModule());

  function nayaxPermissionNotGranted(): HttpErrorResponse {
    return new HttpErrorResponse({ status: 409, error: { code: 'nayax_permission_not_granted' } });
  }

  async function render(
    options: {
      status?: SpecConnectionStatus;
      importsRunAllowed?: boolean;
      sessionActive?: Observable<boolean>;
      denyProductCatalogue?: boolean;
    } = {}
  ) {
    await TestBed.configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of([product({ id: 1, name: 'Widget' })]) } },
        { provide: CategoryService, useValue: { getAll: () => of([]) } },
        { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
        { provide: SupportSessionService, useValue: { active$: options.sessionActive ?? of(false) } },
        {
          provide: AccessService,
          useValue: accessServiceStubPerCapability({ 'Imports.Run': options.importsRunAllowed ?? true })
        },
        { provide: ProductPurchaseCostService, useValue: purchaseCostServiceStub() },
        provideNayaxConnectionStatus(options.status ?? 'Ready')
      ]
    }).compileComponents();

    const provider = TestBed.inject(ProviderAvailabilityService);
    if (options.denyProductCatalogue) {
      provider.noteRefusal(nayaxPermissionNotGranted(), 'productCatalogue');
    }

    const fixture = TestBed.createComponent(ProductListComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, provider };
  }

  function importLink(host: HTMLElement): HTMLAnchorElement | null {
    return host.querySelector('[data-testid="import-products-link"]');
  }

  function newProductLink(host: HTMLElement): HTMLAnchorElement | null {
    return host.querySelector('a[href="/products/new"]');
  }

  it('shows Import products beside New Product for Imports.Run with a usable connection and productCatalogue offered', async () => {
    const { host } = await render({ status: 'Ready' });

    const link = importLink(host);
    expect(link).not.toBeNull();
    expect(link!.textContent?.trim()).toBe('Import products');
    expect(newProductLink(host)).not.toBeNull();
  });

  it('shows Import products while the connection status and the productCatalogue permission are both still unknown', async () => {
    const { host } = await render({ status: 'unreadable' });

    expect(importLink(host)).not.toBeNull();
    expect(newProductLink(host)).not.toBeNull();
  });

  it('hides Import products, and keeps New Product, when there is no usable connection', async () => {
    const { host } = await render({ status: 'NotConfigured' });

    expect(importLink(host)).toBeNull();
    expect(newProductLink(host)).not.toBeNull();
  });

  it('hides Import products, and keeps New Product, when productCatalogue is permissionNotGranted under PendingPermissions', async () => {
    const { host } = await render({ status: 'PendingPermissions', denyProductCatalogue: true });

    expect(importLink(host)).toBeNull();
    expect(newProductLink(host)).not.toBeNull();
  });

  it('hides Import products, and keeps New Product, for a member without Imports.Run', async () => {
    const { host } = await render({ status: 'Ready', importsRunAllowed: false });

    expect(importLink(host)).toBeNull();
    expect(newProductLink(host)).not.toBeNull();
  });

  it('hides both Import products and New Product during a support session', async () => {
    const { host } = await render({ status: 'Ready', sessionActive: of(true) });

    expect(importLink(host)).toBeNull();
    expect(newProductLink(host)).toBeNull();
  });

  it('appears as the provider state changes to usable, without a reload', async () => {
    const { host, provider, fixture } = await render({ status: 'NotConfigured' });
    expect(importLink(host)).toBeNull();

    provider.accept({ status: 'Ready', machineChecksPending: false });
    fixture.detectChanges();

    expect(importLink(host)).not.toBeNull();
  });

  it('disappears as the provider state changes away from usable, without a reload', async () => {
    const { host, provider, fixture } = await render({ status: 'Ready' });
    expect(importLink(host)).not.toBeNull();

    provider.accept({ status: 'NotConfigured', machineChecksPending: false });
    fixture.detectChanges();

    expect(importLink(host)).toBeNull();
  });

  it('navigates to /admin/imports?review=catalogue', async () => {
    const { host } = await render({ status: 'Ready' });

    expect(importLink(host)!.getAttribute('href')).toBe('/admin/imports?review=catalogue');
  });
});
