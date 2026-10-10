import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { StockHistoryPageComponent } from './stock-history-page.component';
import { StockService } from '../../services/stock.service';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import {
  Machine,
  Product,
  StockAdjustmentReason,
  StockAdjustmentSource,
  StockHistoryEntry,
  StockHistoryPage
} from '../../models/models';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Coke',
    unit: 'can',
    unitPrice: 2.5,
    quantityInStock: 17,
    lowStockThreshold: 2,
    ...overrides
  } as Product;
}

function machine(machineID: number, machineName: string): Machine {
  return { machineID, machineName } as Machine;
}

function entry(overrides: Partial<StockHistoryEntry> = {}): StockHistoryEntry {
  return {
    id: 1,
    productId: 1,
    productName: 'Coke',
    quantityChange: 10,
    quantityAfter: 17,
    unitCost: 1.25,
    reason: StockAdjustmentReason.Restock,
    source: StockAdjustmentSource.Manual,
    machineId: null,
    notes: 'weekly restock',
    eatBefore: null,
    createdAt: '2026-01-15T22:30:00Z',
    ...overrides
  };
}

function page(overrides: Partial<StockHistoryPage> = {}): StockHistoryPage {
  return { items: [entry()], page: 1, pageSize: 50, totalCount: 1, hasMore: false, ...overrides };
}

interface Harness {
  component: StockHistoryPageComponent;
  historyPage: jest.Mock;
  getAllProducts: jest.Mock;
}

function routeStub(queryParams: Record<string, string> = {}, params: Record<string, string> = {}): ActivatedRoute {
  return {
    snapshot: {
      queryParamMap: convertToParamMap(queryParams),
      paramMap: convertToParamMap(params)
    }
  } as unknown as ActivatedRoute;
}

function createHarness(options: {
  result?: unknown;
  products?: Product[];
  machines?: Machine[];
  queryParams?: Record<string, string>;
  params?: Record<string, string>;
  init?: boolean;
} = {}): Harness {
  const historyPage = jest.fn(() => (options.result === undefined ? of(page()) : (options.result as never)));
  const getAllProducts = jest.fn(() => of(options.products ?? [product()]));
  const component = new StockHistoryPageComponent(
    routeStub(options.queryParams, options.params),
    { historyPage } as unknown as StockService,
    { getAll: getAllProducts } as unknown as ProductService,
    { getAll: jest.fn(() => of(options.machines ?? [machine(9, 'Lobby')])) } as unknown as MachineService
  );

  if (options.init !== false) component.ngOnInit();
  return { component, historyPage, getAllProducts };
}

describe('StockHistoryPageComponent unfiltered load', () => {
  it('asks for the first page of the all-products history without a product filter', () => {
    const { component, historyPage } = createHarness();

    expect(historyPage).toHaveBeenCalledTimes(1);
    expect(historyPage).toHaveBeenCalledWith({
      productId: null,
      from: null,
      to: null,
      reason: null,
      machineId: null,
      source: null,
      page: 1,
      pageSize: 50
    });
    expect(component.history?.items).toHaveLength(1);
    expect(component.loading).toBe(false);
    expect(component.error).toBe('');
  });

  it('never loads a product list to fan out one history request per product', () => {
    const { historyPage } = createHarness({ products: [product({ id: 1 }), product({ id: 2, name: 'Chips' })] });

    // One bounded request for the whole page, whatever the catalogue size.
    expect(historyPage).toHaveBeenCalledTimes(1);
  });
});

describe('StockHistoryPageComponent filters', () => {
  it('sends every supported filter and restarts at the first page', () => {
    const { component, historyPage } = createHarness();
    component.currentPage = 4;
    component.filters = {
      productId: 7,
      from: '2026-01-01',
      to: '2026-01-31',
      reason: StockAdjustmentReason.MachineRefill,
      machineId: 9,
      source: StockAdjustmentSource.Nayax,
      pageSize: 25
    };

    component.applyFilters();

    expect(historyPage).toHaveBeenLastCalledWith({
      productId: 7,
      from: '2026-01-01',
      to: '2026-01-31',
      reason: StockAdjustmentReason.MachineRefill,
      machineId: 9,
      source: StockAdjustmentSource.Nayax,
      page: 1,
      pageSize: 25
    });
    expect(component.currentPage).toBe(1);
  });

  it('clearing the filters keeps the page size and reloads the all-products history', () => {
    const { component, historyPage } = createHarness();
    component.filters = {
      productId: 7,
      from: '2026-01-01',
      to: '2026-01-31',
      reason: StockAdjustmentReason.Damaged,
      machineId: 9,
      source: StockAdjustmentSource.Manual,
      pageSize: 100
    };
    expect(component.hasFilters).toBe(true);

    component.clearFilters();

    expect(component.hasFilters).toBe(false);
    expect(historyPage).toHaveBeenLastCalledWith(
      expect.objectContaining({ productId: null, from: null, to: null, reason: null, machineId: null, source: null, pageSize: 100 })
    );
  });

  it('sends an empty date as no date rather than as a blank filter value', () => {
    const { component, historyPage } = createHarness();
    component.filters.from = '';
    component.filters.to = '';

    component.applyFilters();

    expect(historyPage).toHaveBeenLastCalledWith(expect.objectContaining({ from: null, to: null }));
  });
});

describe('StockHistoryPageComponent product preselection', () => {
  it('preselects the product named in the query string', () => {
    const { component, historyPage } = createHarness({ queryParams: { productId: '7' } });

    expect(component.filters.productId).toBe(7);
    expect(historyPage).toHaveBeenCalledWith(expect.objectContaining({ productId: 7 }));
  });

  it('preselects the product of the legacy /products/:id/stock entry point', () => {
    const { component, historyPage } = createHarness({
      params: { id: '123' },
      products: [product({ id: 123, name: 'Chips' })]
    });

    expect(component.filters.productId).toBe(123);
    expect(historyPage).toHaveBeenCalledWith(expect.objectContaining({ productId: 123 }));
    expect(component.selectedProduct?.name).toBe('Chips');
  });

  it('ignores an unusable product parameter and shows the all-products view', () => {
    const { component, historyPage } = createHarness({ params: { id: 'not-a-product' } });

    expect(component.filters.productId).toBeNull();
    expect(historyPage).toHaveBeenCalledWith(expect.objectContaining({ productId: null }));
  });

  it('still lists a preselected product that is not in the product list', () => {
    const { component } = createHarness({ queryParams: { productId: '999' }, products: [product({ id: 1 })] });

    expect(component.filters.productId).toBe(999);
    expect(component.selectedProduct).toBeNull();
  });
});

describe('StockHistoryPageComponent paging', () => {
  it('moves to the next page only while the server says more remain', () => {
    const { component, historyPage } = createHarness({
      result: of(page({ page: 1, pageSize: 50, totalCount: 120, hasMore: true }))
    });

    component.nextPage();

    expect(historyPage).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 }));
    expect(component.totalPages).toBe(3);
  });

  it('does not page past the end or before the first page', () => {
    const { component, historyPage } = createHarness({
      result: of(page({ page: 1, totalCount: 1, hasMore: false }))
    });

    component.nextPage();
    component.previousPage();

    expect(historyPage).toHaveBeenCalledTimes(1);
  });

  it('adopts the page size the server actually served when the request exceeded the maximum', () => {
    const { component } = createHarness({
      result: of(page({ page: 1, pageSize: 200, totalCount: 500, hasMore: true }))
    });

    expect(component.filters.pageSize).toBe(200);
    expect(component.totalPages).toBe(3);
  });
});

describe('StockHistoryPageComponent states', () => {
  it('reports loading until the history arrives', () => {
    const pending = new Subject<StockHistoryPage>();
    const { component } = createHarness({ result: pending.asObservable() });

    expect(component.loading).toBe(true);
    expect(component.history).toBeNull();

    pending.next(page());
    pending.complete();

    expect(component.loading).toBe(false);
    expect(component.history?.items).toHaveLength(1);
  });

  it('clears a previous error while the next request is in flight', () => {
    const pending = new Subject<StockHistoryPage>();
    const { component, historyPage } = createHarness({
      result: throwError(() => ({ error: { message: 'Temporarily unavailable.' } }))
    });
    expect(component.error).toBe('Temporarily unavailable.');

    historyPage.mockReturnValue(pending.asObservable() as never);
    component.load();

    expect(component.error).toBe('');
    expect(component.loading).toBe(true);
  });

  it('surfaces the API error message and keeps no stale history', () => {
    const { component } = createHarness({
      result: throwError(() => ({ error: { title: 'Stock history is unavailable.' } }))
    });

    expect(component.error).toBe('Stock history is unavailable.');
    expect(component.history).toBeNull();
    expect(component.loading).toBe(false);
  });

  it('falls back to a readable message when the failure carries no detail', () => {
    const { component } = createHarness({ result: throwError(() => ({})) });

    expect(component.error).toBe('Failed to load stock history.');
  });

  it('keeps an empty page as an empty result, not an error', () => {
    const { component } = createHarness({ result: of(page({ items: [], totalCount: 0, hasMore: false })) });

    expect(component.error).toBe('');
    expect(component.history?.items).toEqual([]);
  });
});

describe('StockHistoryPageComponent adjustment workflow', () => {
  it('reloads the history and the product stock after an adjustment was applied', () => {
    const { component, historyPage, getAllProducts } = createHarness();
    component.currentPage = 3;

    component.onAdjustmentApplied();

    expect(component.currentPage).toBe(1);
    expect(historyPage).toHaveBeenCalledTimes(2);
    expect(getAllProducts).toHaveBeenCalledTimes(2);
  });
});

describe('StockHistoryPageComponent labels', () => {
  it('labels reason, source, machine and an unavailable unit cost', () => {
    const { component } = createHarness();

    expect(component.reasonLabel(StockAdjustmentReason.Correction)).toBe('Correction');
    expect(component.sourceLabel(StockAdjustmentSource.Nayax)).toBe('Nayax Sync Restock');
    expect(component.sourceLabel(StockAdjustmentSource.Manual)).toBe('Manual');
    expect(component.machineLabel(9)).toBe('Lobby');
    expect(component.machineLabel(404)).toBe('404');
    expect(component.machineLabel(null)).toBe('—');
    expect(component.unitCostLabel(null)).toBe('—');
    expect(component.unitCostLabel(undefined)).toBe('—');
    expect(component.unitCostLabel(1.5)).toBe('$1.50');
  });
});

describe('StockHistoryPageComponent rendering', () => {
  async function render(options: { result?: unknown; queryParams?: Record<string, string> } = {}) {
    const historyPage = jest.fn(() => (options.result === undefined ? of(page()) : (options.result as never)));

    await TestBed.configureTestingModule({
      imports: [StockHistoryPageComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: routeStub(options.queryParams ?? {}) },
        { provide: StockService, useValue: { historyPage } },
        { provide: ProductService, useValue: { getAll: jest.fn(() => of([product()])) } },
        { provide: MachineService, useValue: { getAll: jest.fn(() => of([machine(9, 'Lobby')])) } }
      ]
    }).compileComponents();

    // The business time zone the shell loads at sign-in (issue #499); the movement instants are
    // rendered in it, and the application's existing business is in Sydney.
    TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

    const fixture = TestBed.createComponent(StockHistoryPageComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('renders a movement row with the product name, the business-time instant and the unit cost', async () => {
    const { host } = await render();

    const rows = host.querySelectorAll('[data-testid="history-rows"] tr');
    expect(rows).toHaveLength(1);
    const cells = Array.from(rows[0].querySelectorAll('td')).map((cell) => cell.textContent?.trim());
    expect(cells[1]).toBe('Coke');
    expect(cells[2]).toBe('+10');
    expect(cells[4]).toBe('Restock');
    expect(cells[5]).toBe('Manual');
    expect(cells[7]).toBe('$1.25');
    expect(cells[9]).toBe('weekly restock');
    // 2026-01-15T22:30:00Z is 16 January 2026 in business time, not 15 January as the raw UTC
    // clock value would read.
    expect(cells[0]).toContain('16/01/2026');
  });

  it('shows the empty state and no table when nothing matches', async () => {
    const { host } = await render({ result: of(page({ items: [], totalCount: 0, hasMore: false })) });

    expect(host.querySelector('[data-testid="history-empty"]')?.textContent).toContain('No stock adjustments yet.');
    expect(host.querySelector('table')).toBeNull();
  });

  it('shows the error state instead of the table when the request fails', async () => {
    const { host } = await render({ result: throwError(() => ({ error: 'Stock history is unavailable.' })) });

    const error = host.querySelector('[data-testid="history-error"]');
    expect(error?.textContent).toContain('Stock history is unavailable.');
    expect(error?.getAttribute('role')).toBe('alert');
    expect(host.querySelector('table')).toBeNull();
  });

  it('offers the adjustment form only once a product is selected', async () => {
    const withoutProduct = await render();
    expect(withoutProduct.host.querySelector('app-stock-adjustment-form')).toBeNull();
    expect(withoutProduct.host.querySelector('[data-testid="select-product-hint"]')).not.toBeNull();
    TestBed.resetTestingModule();

    const withProduct = await render({ queryParams: { productId: '1' } });
    expect(withProduct.host.querySelector('app-stock-adjustment-form')).not.toBeNull();
    expect(withProduct.host.querySelector('[data-testid="selected-product-summary"]')).not.toBeNull();
  });
});
