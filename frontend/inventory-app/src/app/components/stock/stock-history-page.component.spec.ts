import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { StockHistoryPageComponent } from './stock-history-page.component';
import { StockService } from '../../services/stock.service';
import { ProductService } from '../../services/product.service';
import { LocalMachineService } from '../../services/local-machine.service';
import {
  LocalMachine,
  Product,
  StockAdjustmentReason,
  StockAdjustmentSource,
  StockHistoryEntry,
  StockHistoryPage
} from '../../models/models';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import {
  provideNayaxConnectionStatus,
  providerAvailabilityDouble
} from '../../provider/provider-availability.spec-helpers';
import { SupportSessionService } from '../../support-session/support-session.service';

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Coke',
    unit: 'can',
    quantityInStock: 17,
    lowStockThreshold: 2,
    ...overrides
  } as Product;
}

function machine(id: number, name: string): LocalMachine {
  // The business's own machine record (issue #746): its id is what a movement records, what the
  // filter sends and what the machine column is named from.
  return { id, name, isActive: true, isProviderNamed: false };
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
  machines?: LocalMachine[];
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
    { getAll: jest.fn(() => of(options.machines ?? [machine(41, 'Lobby')])) } as unknown as LocalMachineService,
    providerAvailabilityDouble()
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

describe('StockHistoryPageComponent overlapping requests', () => {
  it('lets only the latest request update the ledger when an older one answers last', () => {
    const older = new Subject<StockHistoryPage>();
    const newer = new Subject<StockHistoryPage>();
    const { component, historyPage } = createHarness({ result: older.asObservable() });
    historyPage.mockReturnValueOnce(newer.asObservable() as never);

    component.filters.reason = StockAdjustmentReason.Damaged;
    component.applyFilters();

    newer.next(page({ items: [entry({ id: 2, reason: StockAdjustmentReason.Damaged })], page: 1, pageSize: 25, totalCount: 1 }));
    newer.complete();
    expect(component.loading).toBe(false);

    // The superseded unfiltered request answers afterwards, with a different page and size.
    older.next(page({ items: [entry({ id: 1 }), entry({ id: 3 })], page: 2, pageSize: 100, totalCount: 300, hasMore: true }));
    older.complete();

    expect(component.history?.items.map((item) => item.id)).toEqual([2]);
    expect(component.currentPage).toBe(1);
    expect(component.filters.pageSize).toBe(25);
    expect(component.loading).toBe(false);
    expect(component.error).toBe('');
  });

  it('ignores a superseded request that fails after the latest one succeeded', () => {
    const older = new Subject<StockHistoryPage>();
    const newer = new Subject<StockHistoryPage>();
    const { component, historyPage } = createHarness({ result: older.asObservable() });
    historyPage.mockReturnValueOnce(newer.asObservable() as never);

    component.applyFilters();
    newer.next(page());
    newer.complete();
    older.error({ error: 'Stock history is unavailable.' });

    expect(component.error).toBe('');
    expect(component.history?.items).toHaveLength(1);
  });

  it('cancels the request in flight when the page is left', () => {
    const pending = new Subject<StockHistoryPage>();
    const { component } = createHarness({ result: pending.asObservable() });
    expect(pending.observed).toBe(true);

    component.ngOnDestroy();

    expect(pending.observed).toBe(false);
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

  it('closes the panel and keeps every filter when the adjustment was applied (issue #1156)', () => {
    const { component, historyPage } = createHarness({ queryParams: { productId: '1' } });
    component.filters.reason = StockAdjustmentReason.Damaged;
    component.filters.from = '2026-10-01';
    component.openAdjustment();
    expect(component.adjusting).toBe(true);

    component.onAdjustmentApplied();

    expect(component.adjusting).toBe(false);
    expect(component.adjustmentMessage).toBe('Stock adjustment recorded for Coke.');
    // The new movement is a Restock, say: with Damaged still applied it stays outside the results,
    // because the reload asks for exactly the filters that were applied.
    expect(historyPage).toHaveBeenLastCalledWith(
      expect.objectContaining({ productId: 1, reason: StockAdjustmentReason.Damaged, from: '2026-10-01', page: 1 })
    );
  });

  it('opens Adjust stock only for a product the catalogue resolved', () => {
    const { component } = createHarness({ queryParams: { productId: '999' } });

    component.openAdjustment();

    expect(component.adjusting).toBe(false);
  });

  it('returns focus to Adjust stock when the panel closes', async () => {
    jest.useFakeTimers();
    try {
      const { component } = createHarness({ queryParams: { productId: '1' } });
      const button = document.createElement('button');
      document.body.appendChild(button);
      (component as unknown as { adjustButton: { nativeElement: HTMLButtonElement } }).adjustButton = { nativeElement: button };
      component.openAdjustment();

      component.closeAdjustment();
      jest.runAllTimers();

      expect(component.adjusting).toBe(false);
      expect(document.activeElement).toBe(button);
      button.remove();
    } finally {
      jest.useRealTimers();
    }
  });
});

describe('StockHistoryPageComponent labels', () => {
  it('labels reason, source, machine and an unavailable unit cost', () => {
    const { component } = createHarness();

    expect(component.reasonLabel(StockAdjustmentReason.Correction)).toBe('Correction');
    expect(component.sourceLabel(StockAdjustmentSource.Nayax)).toBe('Nayax Sync Restock');
    expect(component.sourceLabel(StockAdjustmentSource.Manual)).toBe('Manual');
    expect(component.machineLabel(41)).toBe('Lobby');
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
        { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of([machine(41, 'Lobby')])) } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        provideNayaxConnectionStatus()
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

  it('renders a movement row with the product name, the business-time instant and its details on demand', async () => {
    const { host, fixture } = await render();

    const headers = Array.from(host.querySelectorAll('[data-testid="history-table"] th')).map((th) => th.textContent?.trim());
    expect(headers).toEqual(['Date and time', 'Product', 'Change', 'Stock after', 'Reason', 'Source', 'Machine', 'Details']);
    const rows = host.querySelectorAll('[data-testid="history-rows"] tr');
    expect(rows).toHaveLength(1);
    const cells = Array.from(rows[0].querySelectorAll('td')).map((cell) => cell.textContent?.trim());
    expect(cells[1]).toBe('Coke');
    expect(cells[2]).toBe('+10');
    expect(cells[3]).toBe('17');
    expect(cells[4]).toBe('Restock');
    expect(cells[5]).toBe('Manual');
    expect(cells[6]).toBe('—');
    // 2026-01-15T22:30:00Z is 16 January 2026 in business time, not 15 January as the raw UTC
    // clock value would read.
    expect(cells[0]).toContain('16/01/2026');

    // Unit cost, eat before and notes are behind the row's own disclosure (issue #1156).
    expect(host.querySelector('[data-testid="movement-details"]')).toBeNull();
    const toggle = host.querySelector<HTMLButtonElement>('[data-testid="movement-details-toggle"]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const details = host.querySelector('[data-testid="movement-details"]')!;
    expect(details.id).toBe(toggle.getAttribute('aria-controls'));
    expect(details.querySelector('[data-testid="movement-unit-cost"]')?.textContent?.trim()).toBe('$1.25');
    expect(details.querySelector('[data-testid="movement-eat-before"]')?.textContent?.trim()).toBe('—');
    expect(details.querySelector('[data-testid="movement-notes"]')?.textContent?.trim()).toBe('weekly restock');

    toggle.click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="movement-details"]')).toBeNull();
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

  it('offers Adjust stock only once a product is selected, and opens the form in the panel', async () => {
    const withoutProduct = await render();
    expect(withoutProduct.host.querySelector('[data-testid="open-adjustment"]')).toBeNull();
    expect(withoutProduct.host.querySelector('app-stock-adjustment-form')).toBeNull();
    expect(withoutProduct.host.querySelector('[data-testid="select-product-hint"]')).not.toBeNull();
    TestBed.resetTestingModule();

    const withProduct = await render({ queryParams: { productId: '1' } });
    expect(withProduct.host.querySelector('[data-testid="selected-product-summary"]')).not.toBeNull();
    expect(withProduct.host.querySelector('app-stock-adjustment-form')).toBeNull();

    withProduct.host.querySelector<HTMLButtonElement>('[data-testid="open-adjustment"]')!.click();
    withProduct.fixture.detectChanges();

    expect(withProduct.host.querySelector('app-stock-adjustment-panel app-stock-adjustment-form')).not.toBeNull();
    // The ledger, its filters and its selection stay on the page behind the panel.
    expect(withProduct.host.querySelector('[data-testid="history-rows"]')).not.toBeNull();
    expect(withProduct.fixture.componentInstance.filters.productId).toBe(1);
  });

  it('leaves the Product column out when one product is listed, and shows it in the cards otherwise', async () => {
    const single = await render({ queryParams: { productId: '1' } });
    const headers = Array.from(single.host.querySelectorAll('[data-testid="history-table"] th')).map((th) => th.textContent?.trim());
    expect(headers).not.toContain('Product');
    expect(single.host.querySelector('[data-testid="history-card"]')?.textContent).not.toContain('Coke');
    TestBed.resetTestingModule();

    const all = await render();
    expect(all.host.querySelector('[data-testid="history-card"]')?.textContent).toContain('Coke');
  });

  it('renders each movement as a card with change and reason first, for narrow screens', async () => {
    const { host } = await render({
      result: of(
        page({
          items: [entry({ quantityChange: -4, quantityAfter: 0, reason: StockAdjustmentReason.Correction, machineId: null })]
        })
      )
    });

    const card = host.querySelector('[data-testid="history-card"]')!;
    const heading = Array.from(card.firstElementChild!.children).map((el) => el.textContent?.trim());
    expect(heading).toEqual(['−4', 'Correction']);
    const fields = Object.fromEntries(
      Array.from(card.querySelectorAll('dl > div')).map((div) => [
        div.querySelector('dt')?.textContent?.trim(),
        div.querySelector('dd')?.textContent?.trim()
      ])
    );
    // A correction that ended at zero shows the recorded zero, not a missing value.
    expect(fields['Stock after']).toBe('0');
    expect(fields['Source']).toBe('Manual');
    expect(fields['Date and time']).toContain('16/01/2026');
    // No machine was recorded, so the card does not invent a Machine line.
    expect(fields['Machine']).toBeUndefined();
    expect(card.querySelector('[data-testid="movement-card-details-toggle"]')).not.toBeNull();
  });

  it('puts Rows per page with the pager under the results, offering the existing sizes', async () => {
    const { host } = await render({ result: of(page({ page: 2, pageSize: 25, totalCount: 60, hasMore: true })) });

    const pager = host.querySelector('[data-testid="list-pagination"]')!;
    expect(pager.textContent).toContain('Rows per page');
    expect(pager.textContent).toContain('Page 2 of 3');
    const sizes = Array.from(pager.querySelectorAll('[data-testid="list-pagination-size"] option')).map((o) => o.getAttribute('value'));
    expect(sizes).toEqual(['25', '50', '100', '200']);
    expect(host.querySelector('[data-testid="history-toolbar"] #filter-page-size')).toBeNull();
  });

  it('shows the product summary in units, apart from the unit label, including a recorded zero', async () => {
    TestBed.overrideProvider(ProductService, {
      useValue: { getAll: jest.fn(() => of([product({ quantityInStock: 0, lowStockThreshold: 0, unit: '375 mL' })])) }
    });
    const { host } = await render({ queryParams: { productId: '1' } });

    const summary = host.querySelector('[data-testid="selected-product-summary"]')!;
    expect(summary.querySelector('[data-testid="summary-current-stock"]')?.textContent?.trim()).toBe('0');
    expect(summary.querySelector('[data-testid="summary-low-stock-threshold"]')?.textContent?.trim()).toBe('0');
    expect(summary.querySelector('[data-testid="summary-unit-label"]')?.textContent?.trim()).toBe('375 mL');
    expect(summary.textContent).toContain('units');
  });

  it('keeps Reason, Machine and Source under More filters, and lists the applied ones', async () => {
    const { host, fixture } = await render();
    const panel = host.querySelector<HTMLElement>('[data-testid="more-filters-panel"]')!;
    const toggle = host.querySelector<HTMLButtonElement>('[data-testid="more-filters"]')!;

    expect(panel.hidden).toBe(true);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(host.querySelector('[data-testid="clear-filters"]')).toBeNull();

    toggle.click();
    fixture.detectChanges();
    expect(panel.hidden).toBe(false);

    const reason = host.querySelector<HTMLSelectElement>('#filter-reason')!;
    reason.value = reason.options[3].value; // Damaged
    reason.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="more-filters-count"]')?.textContent).toContain('1');
    expect(host.querySelector('[data-testid="applied-filters"]')?.textContent).toContain('Reason: Damaged');
    expect(host.querySelector('[data-testid="clear-filters"]')).not.toBeNull();
  });
});

describe('StockHistoryPageComponent toolbar and More filters (issue #1156)', () => {
  it('applies a toolbar filter as soon as it changes, from page 1, with the same request', () => {
    const { component, historyPage } = createHarness();
    component.currentPage = 3;
    component.filters.from = '2026-10-01';

    component.applyFilters();

    expect(historyPage).toHaveBeenLastCalledWith(expect.objectContaining({ from: '2026-10-01', page: 1, pageSize: 50 }));
  });

  it('names the applied More filters, and removing one reloads without it', () => {
    const { component, historyPage } = createHarness();
    component.filters.reason = StockAdjustmentReason.MachineRefill;
    component.filters.machineId = 41;
    component.filters.source = StockAdjustmentSource.Nayax;
    component.filters.from = '2026-10-01';

    expect(component.appliedMoreFilters.map((f) => f.label)).toEqual([
      'Reason: Machine refill',
      'Machine: Lobby',
      'Source: Nayax Sync Restock'
    ]);

    component.removeFilter('machineId');

    expect(component.filters.machineId).toBeNull();
    expect(historyPage).toHaveBeenLastCalledWith(
      expect.objectContaining({ reason: StockAdjustmentReason.MachineRefill, machineId: null, source: StockAdjustmentSource.Nayax, from: '2026-10-01', page: 1 })
    );
  });

  it('keeps the current stock as it is when a historical filter changes', () => {
    const { component, getAllProducts } = createHarness({ queryParams: { productId: '1' } });
    const before = component.selectedProduct?.quantityInStock;

    component.filters.from = '2025-01-01';
    component.filters.to = '2025-01-31';
    component.applyFilters();

    expect(getAllProducts).toHaveBeenCalledTimes(1);
    expect(component.selectedProduct?.quantityInStock).toBe(before);
  });
});

describe('StockHistoryPageComponent pager (issue #1156)', () => {
  it('turns to the page the pager asks for, keeping the filters', () => {
    const { component, historyPage } = createHarness({
      result: of(page({ page: 1, pageSize: 50, totalCount: 120, hasMore: true }))
    });
    component.filters.reason = StockAdjustmentReason.Damaged;

    component.goToPage(1);

    expect(historyPage).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2, reason: StockAdjustmentReason.Damaged }));
  });

  it('does not turn past the last page the server reported', () => {
    const { component, historyPage } = createHarness({ result: of(page({ page: 1, totalCount: 1, hasMore: false })) });

    component.goToPage(1);

    expect(historyPage).toHaveBeenCalledTimes(1);
  });

  it('changes rows per page to an offered size only, starting again at page 1', () => {
    const { component, historyPage } = createHarness();
    component.currentPage = 2;

    component.onPageSizeChange(37);
    expect(historyPage).toHaveBeenCalledTimes(1);

    component.onPageSizeChange(100);
    expect(historyPage).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, pageSize: 100 }));
  });

  it('collapses every expanded movement when another page is read', () => {
    const { component } = createHarness();
    component.toggleDetails(1);
    expect(component.isExpanded(1)).toBe(true);

    component.load();

    expect(component.isExpanded(1)).toBe(false);
  });
});

describe('StockHistoryPageComponent value labels (issue #1156)', () => {
  it('signs a change and keeps a recorded zero apart from a missing value', () => {
    const { component } = createHarness();

    expect(component.changeLabel(12)).toBe('+12');
    expect(component.changeLabel(-3)).toBe('\u22123');
    expect(component.changeLabel(0)).toBe('0');
    expect(component.quantityLabel(0)).toBe('0');
    expect(component.quantityLabel(null)).toBe('—');
    expect(component.quantityLabel(undefined)).toBe('—');
  });

  it('names a blank unit label as not set rather than as a quantity', () => {
    const { component } = createHarness();

    expect(component.unitLabel(product({ unit: '  ' }))).toBe('Not set');
    expect(component.unitLabel(product({ unit: null }))).toBe('Not set');
    expect(component.unitLabel(product({ unit: '45 g' }))).toBe('45 g');
  });
});
