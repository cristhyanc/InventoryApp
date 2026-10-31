import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, Subject, of } from 'rxjs';
import { ProductNeedsOrderingComponent } from './product-needs-ordering.component';
import { AccessService } from '../../access/access.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { ToastService } from '../../services/toast.service';
import { Product, ReorderAlertListing } from '../../models/models';
import { ProductPurchaseCostSummary } from '../../models/product-purchase-cost-summary';
import { SupplierPriceBasis } from '../../models/cheapest-supplier';

/**
 * Products → Needs Ordering: the Google search link on the product name, the removed Price and
 * Status columns, the active-only listing, and the Last purchase/Cheapest purchase columns
 * (issue #790, D of #786).
 *
 * The two cost columns are the issue's financial half: they come from issue #788's bulk
 * `GET /api/products/purchase-cost-summary`, requested once for the listed ids, only with
 * `Dashboard.ViewFinancials`, and they keep "still loading", "loaded and this product has never
 * been bought" and "the request failed" as three distinct answers - a `—` that really means
 * "no purchase history" is published only once a successful response established it.
 */

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Cola 375mL',
    sku: null,
    description: null,
    averageUnitCost: 1,
    costingQuantity: null,
    inventoryValue: null,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 2,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 15,
    needToOrder: 13,
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
    isReorderAlert: true,
    ...overrides
  };
}

function summary(overrides: Partial<ProductPurchaseCostSummary> = {}): ProductPurchaseCostSummary {
  return {
    productId: 1,
    lastUnitCost: 2.5,
    lastSupplierName: 'Acme',
    lastPurchaseDate: '2026-10-01',
    lowestUnitCost: 1.75,
    lowestSupplierName: 'Zenith',
    lowestPurchaseDate: '2026-02-01',
    ...overrides
  };
}

/** One outstanding `getSummaries` call: the ids it asked for and the answer the spec controls. */
interface CostRequest {
  readonly ids: readonly number[];
  readonly response$: Subject<ProductPurchaseCostSummary[]>;
}

interface Harness {
  readonly fixture: ComponentFixture<ProductNeedsOrderingComponent>;
  readonly host: HTMLElement;
  readonly navigate: jest.Mock;
  readonly costRequests: CostRequest[];
  /** Replaces the products the next low-stock load answers with. */
  setProducts(products: Product[]): void;
  /** Triggers the immediate (non-debounced) filter refresh a category change makes. */
  reload(): void;
}

async function render(
  products: Product[],
  options: { canViewFinancials?: boolean } = {}
): Promise<Harness> {
  const navigate = jest.fn();
  const costRequests: CostRequest[] = [];
  let listed = products;

  const getSummaries = (ids: readonly number[]): Observable<ProductPurchaseCostSummary[]> => {
    const response$ = new Subject<ProductPurchaseCostSummary[]>();
    costRequests.push({ ids: [...ids], response$ });
    return response$.asObservable();
  };

  await TestBed.configureTestingModule({
    imports: [ProductNeedsOrderingComponent],
    providers: [
      provideRouter([]),
      {
        provide: ProductService,
        useValue: {
          getLowStock: (): Observable<ReorderAlertListing> =>
            of({ products: listed, mode: 'MachineAware', machineNeedUnavailableReason: null }),
          update: jest.fn(() => of(undefined))
        }
      },
      { provide: CategoryService, useValue: { getAll: () => of([]) } },
      { provide: SupplierService, useValue: { getAll: () => of([]) } },
      { provide: SupplierOrderService, useValue: { create: jest.fn(() => of(undefined)) } },
      { provide: ProductPurchaseCostService, useValue: { getSummaries } },
      // The observed Cheapest supplier column (issue #930) is a separate read with its own specs;
      // here it answers "no product is tracked" so these purchase-cost assertions are about the
      // purchase-cost columns alone.
      {
        provide: CheapestSupplierService,
        useValue: { getSummary: () => of({ displayBasis: SupplierPriceBasis.InclusiveOfGst, products: [] }) }
      },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn() } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      { provide: AccessService, useValue: { allows: () => of(options.canViewFinancials ?? true) } }
    ]
  }).compileComponents();

  jest.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(navigate);

  const fixture = TestBed.createComponent(ProductNeedsOrderingComponent);
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    navigate,
    costRequests,
    setProducts: (next: Product[]) => (listed = next),
    reload: () => {
      fixture.componentInstance.applyFilters();
      fixture.detectChanges();
    }
  };
}

function headers(host: HTMLElement): string[] {
  return Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.trim() ?? '');
}

function cells(host: HTMLElement, rowIndex = 0): string[] {
  const row = host.querySelectorAll('tbody tr')[rowIndex];
  return Array.from(row.querySelectorAll('td')).map((td) => td.textContent?.replace(/\s+/g, ' ').trim() ?? '');
}

/** The cell under `header`, which is how the specs below stay independent of the column order. */
function cell(host: HTMLElement, header: string, rowIndex = 0): string {
  const index = headers(host).indexOf(header);
  expect(index).toBeGreaterThan(-1);
  return cells(host, rowIndex)[index];
}

function nameLink(host: HTMLElement, rowIndex = 0): HTMLAnchorElement {
  return host.querySelectorAll('tbody tr')[rowIndex].querySelector('a.table-row-anchor') as HTMLAnchorElement;
}

function sortHeaderButton(host: HTMLElement, header: string): HTMLButtonElement {
  const th = Array.from(host.querySelectorAll('thead th')).find((candidate) =>
    candidate.textContent?.trim().startsWith(header)
  ) as HTMLTableCellElement;
  return th.querySelector('button') as HTMLButtonElement;
}

function rowNames(host: HTMLElement): (string | undefined)[] {
  return Array.from(host.querySelectorAll('tbody tr')).map((row) =>
    row.querySelectorAll('td')[1]?.textContent?.trim()
  );
}

/** `routerLink` navigates through `navigateByUrl`, so that is where a followed link shows up. */
function spyOnNavigateByUrl() {
  return jest.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
}

function retryButton(host: HTMLElement): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find(
    (button) => button.textContent?.trim() === 'Retry'
  );
}

describe('Needs Ordering: the product name searches Google (issue #790)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('links the name to a Google search for it, in a new tab, with an accessible label', async () => {
    const { host } = await render([product({ id: 9, name: 'Cola 375mL' })]);

    const link = nameLink(host);
    expect(link.getAttribute('href')).toBe('https://www.google.com/search?q=Cola%20375mL');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link.getAttribute('aria-label')).toBe('Search Google for Cola 375mL');
    expect(link.textContent?.trim()).toBe('Cola 375mL');
  });

  it.each([
    ['an ampersand', 'Salt & Vinegar Chips', 'https://www.google.com/search?q=Salt%20%26%20Vinegar%20Chips'],
    ['accents', 'Café Crème 250mL', 'https://www.google.com/search?q=Caf%C3%A9%20Cr%C3%A8me%20250mL'],
    ['a plus and a question mark', 'Red Bull + Energy?', 'https://www.google.com/search?q=Red%20Bull%20%2B%20Energy%3F']
  ])('URL-encodes a name containing %s', async (_label, name, expected) => {
    const { host } = await render([product({ id: 9, name })]);

    expect(nameLink(host).getAttribute('href')).toBe(expected);
  });

  it('does not open the product editor when the name link is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);

    nameLink(host).dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));

    expect(navigate).not.toHaveBeenCalled();
  });

  it('does not open the product editor when the rest of the row is clicked', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);
    const navigateByUrl = spyOnNavigateByUrl();

    (host.querySelectorAll('tbody tr td')[2] as HTMLTableCellElement).click();

    expect(navigate).not.toHaveBeenCalled();
    expect(navigateByUrl).not.toHaveBeenCalled();
  });
});

/**
 * Needs Ordering has no route to the product editor. Issue #790 restored an Edit link here because
 * the row click was the only other way in; the owner has since removed both, so products are
 * edited from All products. The Name anchor still only searches Google.
 */
describe('Needs Ordering offers no route to the product editor', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('renders no Edit link on any row', async () => {
    const { host } = await render([product({ id: 9, name: 'Widget' }), product({ id: 10, name: 'Gadget' })]);

    expect(host.querySelector('tbody tr a[href$="/edit"]')).toBeNull();
    const labels = Array.from(host.querySelectorAll('tbody tr a, tbody tr button')).map((el) => el.textContent?.trim());
    expect(labels).not.toContain('Edit');
  });

  it('keeps the Name link a Google search only, with no in-app navigation', async () => {
    const { host, navigate } = await render([product({ id: 9, name: 'Widget' })]);
    const navigateByUrl = spyOnNavigateByUrl();

    const name = nameLink(host);
    name.focus();
    expect(document.activeElement).toBe(name);
    name.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    name.click();

    expect(name.getAttribute('href')).toBe('https://www.google.com/search?q=Widget');
    expect(navigateByUrl).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
  });
});

describe('Needs Ordering lists only active products (issue #790)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('drops a retired product the response still carried', async () => {
    const { host } = await render([
      product({ id: 1, name: 'Active cola' }),
      product({ id: 2, name: 'Retired cola', isActive: false })
    ]);

    expect(rowNames(host)).toEqual(['Active cola']);
    expect(host.textContent).not.toContain('Retired cola');
  });

  it('asks for purchase costs only for the products it actually lists', async () => {
    const { costRequests } = await render([
      product({ id: 1, name: 'Active cola' }),
      product({ id: 2, name: 'Retired cola', isActive: false })
    ]);

    expect(costRequests).toHaveLength(1);
    expect(costRequests[0].ids).toEqual([1]);
  });
});

describe('Needs Ordering has no Price or Status column (issue #790)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('offers no Status column, Status sort or Active/Inactive toggle', async () => {
    const { host } = await render([product({ id: 9, name: 'Widget' })]);

    expect(headers(host).some((header) => header.startsWith('Status'))).toBe(false);
    expect(
      Array.from(host.querySelectorAll('tbody button')).some((button) =>
        ['Active', 'Inactive'].includes(button.textContent?.trim() ?? '')
      )
    ).toBe(false);
  });

  it('keeps the Price column and its sort absent (issue #792)', async () => {
    const { host } = await render([product({ id: 9, name: 'Widget' })]);

    expect(headers(host).some((header) => header.startsWith('Price'))).toBe(false);
  });
});

describe('Needs Ordering purchase-cost columns (issue #790)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('adds Last purchase and Cheapest purchase immediately after Need to Order', async () => {
    const { host } = await render([product({ id: 1 })]);

    const all = headers(host);
    const needToOrder = all.findIndex((header) => header.startsWith('Need to Order'));
    expect(all[needToOrder + 1]).toContain('Last purchase');
    expect(all[needToOrder + 2]).toContain('Cheapest purchase');
  });

  it('requests the listed ids once, not one request per product', async () => {
    const { costRequests } = await render([product({ id: 1 }), product({ id: 4 }), product({ id: 7 })]);

    expect(costRequests).toHaveLength(1);
    expect(costRequests[0].ids).toEqual([1, 4, 7]);
  });

  it('shows Loading while the cost request is outstanding, and never a dash', async () => {
    const { host } = await render([product({ id: 1 })]);

    expect(cell(host, 'Last purchase')).toContain('Loading');
    expect(cell(host, 'Cheapest purchase')).toContain('Loading');
    expect(cell(host, 'Last purchase')).not.toContain('—');
    expect(retryButton(host)).toBeUndefined();
  });

  it('shows each cost to two decimals with the supplier it was paid to', async () => {
    const { fixture, host, costRequests } = await render([product({ id: 1 })]);

    costRequests[0].response$.next([
      summary({ productId: 1, lastUnitCost: 2.5, lastSupplierName: 'Acme', lowestUnitCost: 1.7, lowestSupplierName: 'Zenith' })
    ]);
    fixture.detectChanges();

    expect(cell(host, 'Last purchase')).toContain('$2.50');
    expect(cell(host, 'Last purchase')).toContain('Acme');
    expect(cell(host, 'Cheapest purchase')).toContain('$1.70');
    expect(cell(host, 'Cheapest purchase')).toContain('Zenith');
  });

  it('shows a dash once a successful response establishes that the product has no purchase history', async () => {
    const { fixture, host, costRequests } = await render([product({ id: 1 })]);

    costRequests[0].response$.next([]);
    fixture.detectChanges();

    expect(cell(host, 'Last purchase')).toBe('—');
    expect(cell(host, 'Cheapest purchase')).toBe('—');
    expect(host.textContent).not.toContain('Unavailable');
  });

  it('shows a recorded zero cost as $0.00 and never as a dash', async () => {
    const { fixture, host, costRequests } = await render([product({ id: 1 })]);

    costRequests[0].response$.next([
      summary({ productId: 1, lastUnitCost: 0, lastSupplierName: 'Acme', lowestUnitCost: 0, lowestSupplierName: 'Acme' })
    ]);
    fixture.detectChanges();

    expect(cell(host, 'Last purchase')).toContain('$0.00');
    expect(cell(host, 'Cheapest purchase')).toContain('$0.00');
    expect(cell(host, 'Last purchase')).not.toContain('—');
  });

  it('names an unknown supplier None, independently of the cost beside it', async () => {
    const { fixture, host, costRequests } = await render([product({ id: 1 })]);

    costRequests[0].response$.next([
      summary({ productId: 1, lastUnitCost: 0, lastSupplierName: null, lowestUnitCost: 3.2, lowestSupplierName: null })
    ]);
    fixture.detectChanges();

    expect(cell(host, 'Last purchase')).toContain('$0.00');
    expect(cell(host, 'Last purchase')).toContain('None');
    expect(cell(host, 'Cheapest purchase')).toContain('$3.20');
    expect(cell(host, 'Cheapest purchase')).toContain('None');
  });

  it('distinguishes a failed request from no history, and offers a Retry that loads the costs', async () => {
    const { fixture, host, costRequests } = await render([product({ id: 1 })]);

    costRequests[0].response$.error(new HttpErrorResponse({ status: 500 }));
    fixture.detectChanges();

    expect(cell(host, 'Last purchase')).toContain('Unavailable');
    expect(cell(host, 'Cheapest purchase')).toContain('Unavailable');
    expect(cell(host, 'Last purchase')).not.toContain('—');
    // The page still works: the reorder rows and their Mark as ordered flow are unaffected.
    expect(rowNames(host)).toEqual(['Cola 375mL']);
    expect(cell(host, 'Need to Order')).toBe('13');

    const retry = retryButton(host);
    expect(retry).toBeDefined();
    retry!.click();
    fixture.detectChanges();

    expect(costRequests).toHaveLength(2);
    expect(costRequests[1].ids).toEqual([1]);
    expect(cell(host, 'Last purchase')).toContain('Loading');

    costRequests[1].response$.next([summary({ productId: 1, lastUnitCost: 4, lastSupplierName: 'Acme' })]);
    fixture.detectChanges();

    expect(cell(host, 'Last purchase')).toContain('$4.00');
    expect(retryButton(host)).toBeUndefined();
  });

  it('clears the previous products’ costs when the displayed product set changes', async () => {
    const { fixture, host, costRequests, setProducts, reload } = await render([product({ id: 1, name: 'Cola' })]);

    costRequests[0].response$.next([summary({ productId: 1, lastUnitCost: 2.5, lastSupplierName: 'Acme' })]);
    fixture.detectChanges();
    expect(cell(host, 'Last purchase')).toContain('$2.50');

    setProducts([product({ id: 2, name: 'Chips' })]);
    reload();

    expect(rowNames(host)).toEqual(['Chips']);
    expect(host.textContent).not.toContain('$2.50');
    expect(host.textContent).not.toContain('Acme');
    expect(cell(host, 'Last purchase')).toContain('Loading');
    expect(costRequests[1].ids).toEqual([2]);
  });

  it('ignores a superseded response instead of showing it under the newer product set', async () => {
    const { fixture, host, costRequests, setProducts, reload } = await render([product({ id: 1, name: 'Cola' })]);

    setProducts([product({ id: 2, name: 'Chips' })]);
    reload();

    // The first request's answer arrives late, for a product that is no longer listed.
    costRequests[0].response$.next([summary({ productId: 1, lastUnitCost: 9.99, lastSupplierName: 'Stale' })]);
    fixture.detectChanges();

    expect(host.textContent).not.toContain('$9.99');
    expect(host.textContent).not.toContain('Stale');
    expect(cell(host, 'Last purchase')).toContain('Loading');

    costRequests[1].response$.next([summary({ productId: 2, lastUnitCost: 1.25, lastSupplierName: 'Acme' })]);
    fixture.detectChanges();

    expect(cell(host, 'Last purchase')).toContain('$1.25');
  });

  it('ignores a superseded failure instead of reporting the newer request as unavailable', async () => {
    const { fixture, host, costRequests, setProducts, reload } = await render([product({ id: 1, name: 'Cola' })]);

    setProducts([product({ id: 2, name: 'Chips' })]);
    reload();

    costRequests[0].response$.error(new HttpErrorResponse({ status: 500 }));
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Unavailable');
    expect(retryButton(host)).toBeUndefined();
    expect(cell(host, 'Last purchase')).toContain('Loading');
  });
});

describe('Needs Ordering purchase-cost sorting (issue #790)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function renderSorted() {
    const harness = await render([
      product({ id: 1, name: 'Cola' }),
      product({ id: 2, name: 'Chips' }),
      product({ id: 3, name: 'Water' })
    ]);

    harness.costRequests[0].response$.next([
      summary({ productId: 1, lastUnitCost: 3, lowestUnitCost: 2 }),
      summary({ productId: 2, lastUnitCost: 1, lowestUnitCost: 0.5 })
      // Water (id 3) has never been bought, so it is absent from the answer.
    ]);
    harness.fixture.detectChanges();
    return harness;
  }

  it('sorts by last cost ascending then descending, with the no-history row last in both', async () => {
    const { fixture, host } = await renderSorted();
    const button = sortHeaderButton(host, 'Last purchase');

    button.click();
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Chips', 'Cola', 'Water']);

    button.click();
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Cola', 'Chips', 'Water']);
  });

  it('sorts by cheapest cost ascending then descending, with the no-history row last in both', async () => {
    const { fixture, host } = await renderSorted();
    const button = sortHeaderButton(host, 'Cheapest purchase');

    button.click();
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Chips', 'Cola', 'Water']);

    button.click();
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Cola', 'Chips', 'Water']);
  });

  it('sorts every row last while the costs are unavailable, keeping a deterministic order', async () => {
    const { fixture, host, costRequests } = await render([
      product({ id: 2, name: 'Chips' }),
      product({ id: 1, name: 'Cola' })
    ]);

    costRequests[0].response$.error(new HttpErrorResponse({ status: 500 }));
    fixture.detectChanges();

    const button = sortHeaderButton(host, 'Last purchase');
    button.click();
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Cola', 'Chips']);

    button.click();
    fixture.detectChanges();
    expect(rowNames(host)).toEqual(['Cola', 'Chips']);
  });
});

describe('Needs Ordering purchase costs without Dashboard.ViewFinancials (issue #790)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('renders neither column and asks for nothing', async () => {
    const { host, costRequests } = await render([product({ id: 1 })], { canViewFinancials: false });

    expect(headers(host).some((header) => header.includes('Last purchase'))).toBe(false);
    expect(headers(host).some((header) => header.includes('Cheapest purchase'))).toBe(false);
    expect(costRequests).toHaveLength(0);
    expect(host.textContent).not.toContain('Unavailable');
    expect(retryButton(host)).toBeUndefined();
  });

  it('still lists the reorder alerts, which need no financial capability', async () => {
    const { host } = await render([product({ id: 1, name: 'Cola 375mL' })], { canViewFinancials: false });

    expect(rowNames(host)).toEqual(['Cola 375mL']);
    expect(cell(host, 'Need to Order')).toBe('13');
  });
});
