import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
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

    // Existing action availability, handlers and accessible names are unchanged by the layout fix.
    const editLink = actionsCell.querySelector('a[href="/products/42/edit"]');
    const stockLink = actionsCell.querySelector('a[href="/products/42/stock"]');
    const deleteButton = Array.from(actionsCell.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Delete');
    expect(editLink?.textContent?.trim()).toBe('Edit');
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
