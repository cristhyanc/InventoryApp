import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import {
  DashboardSummary,
  MachineRestockSyncReminderResponse,
  Product,
  ProductPriceTrackingStatus,
  ReorderAlertListing
} from '../../models/models';
import { CheapestSupplierSummary, SupplierPriceBasis, SupplierStockStatus } from '../../models/cheapest-supplier';
import { ProductPurchaseCostSummary } from '../../models/product-purchase-cost-summary';
import { BusinessSettingsService } from '../../services/business-settings.service';
import { CHEAPEST_SUPPLIER_MAX_IDS, CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { ConfigService } from '../../services/config.service';
import { DashboardService } from '../../services/dashboard.service';
import { MachineService } from '../../services/machine.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import {
  PURCHASE_COST_MAX_IDS_PER_REQUEST,
  ProductPurchaseCostService
} from '../../services/product-purchase-cost.service';
import { ProductService } from '../../services/product.service';
import { SiteService } from '../../services/site.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { DashboardComponent, REORDER_PAGE_SIZE } from './dashboard.component';

/**
 * Paging the Dashboard's Reorder priorities card (issue #1017). The card used to show the first
 * five products of the reorder list and link to Needs Ordering for the rest; the owner asked for
 * every product to be reachable here, five rows per page, which supersedes #972's top-five-only
 * requirement for this card.
 *
 * What these tests hold is the whole of that behaviour: the list's own order with no product
 * missing or repeated across the pages, the fixed page size, the pager's states and wording, the
 * one page state the desktop table and the phone cards share, the page a re-read lands on, and -
 * the request rule the issue is most specific about - that the Cheapest supplier and purchase-cost
 * reads happen once per reorder read for **all** the listed ids and that turning a page sends
 * nothing. Nothing here re-ranks, re-filters or re-reads the list.
 */

const SUMMARY: DashboardSummary = {
  asOfUtc: '2026-10-31T01:00:00Z',
  businessDate: '2026-10-31T00:00:00Z',
  needsRefill: {
    machinesNeedingRefill: 0,
    machinesWithEmptySelections: 0,
    machinesWithLowSelections: 0,
    emptySelectionCount: 0,
    lowSelectionCount: 0,
    machinesEvaluated: 3,
    selectionsEvaluated: 90
  },
  needsOrdering: { productsNeedingOrdering: 30, productsEvaluated: 71, mode: 'MachineAware' },
  inventory: {
    inventoryValueAtCost: 2155.49,
    isInventoryValueComplete: true,
    productsWithUnknownCost: 0,
    productCount: 71,
    unitsInStorage: 596
  }
};

function product(id: number): Product {
  return {
    id,
    name: `Product ${id}`,
    sku: null,
    description: null,
    averageUnitCost: 1,
    costingQuantity: null,
    inventoryValue: null,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 4,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 7,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 30,
    needToOrder: 26,
    mdbCode: null,
    unit: null,
    lastEatBefore1: null,
    lastEatBefore2: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId: null,
    category: null,
    isActive: true,
    isLowStock: true,
    isReorderAlert: true
  };
}

/** `count` reorder products, in the order the backend published them. */
function reorderList(count: number): Product[] {
  return Array.from({ length: count }, (_, index) => product(index + 1));
}

/**
 * `count` reorder products whose ids are 15 digits wide, the width real catalogue ids have. The
 * request-size tests need these: short ids hide how long a repeated-query-value URL really gets.
 */
function realisticReorderList(count: number): Product[] {
  return Array.from({ length: count }, (_, index) => product(100_000_000_000_001 + index));
}

/** Kestrel's default `MaxRequestLineSize` (8,192 bytes), which a purchase-cost `GET` must stay under. */
const KESTREL_DEFAULT_MAX_REQUEST_LINE = 8192;

function listing(products: Product[]): ReorderAlertListing {
  return { products, mode: 'MachineAware', machineNeedUnavailableReason: null };
}

function cheapestSummary(productIds: readonly number[]): CheapestSupplierSummary {
  return {
    displayBasis: SupplierPriceBasis.InclusiveOfGst,
    products: productIds.map((productId) => ({
      productId,
      trackingStatus: ProductPriceTrackingStatus.Matched,
      cheapestUnitPrice: 0.95,
      suppliers: [
        {
          supplierId: 1,
          supplierName: 'JB Metro',
          hasListing: true,
          hasPrice: true,
          packQuantity: 24,
          packPrice: 22.8,
          packPriceBasis: SupplierPriceBasis.InclusiveOfGst,
          unitPrice: 0.95,
          unitPriceExGst: 0.8636,
          unitPriceIncGst: 0.95,
          isSpecial: false,
          promotionEndsAtUtc: null,
          stockStatus: SupplierStockStatus.InStock,
          lastCheckedAtUtc: '2026-10-31T04:00:00Z',
          productUrl: null,
          ineligibility: null,
          isCheapest: true
        }
      ]
    }))
  };
}

function costSummary(productId: number): ProductPurchaseCostSummary {
  return {
    productId,
    lastUnitCost: 1.25,
    lastSupplierName: 'Acme',
    lastPurchaseDate: '2026-10-01',
    lowestUnitCost: 0.95,
    lowestSupplierName: 'Budget Wholesale',
    lowestPurchaseDate: '2026-08-14'
  };
}

function queued<T>(answers: Observable<T>[]): () => Observable<T> {
  let call = 0;
  return () => answers[Math.min(call++, answers.length - 1)];
}

interface Doubles {
  /** The reorder listings, one per `getLowStock` call; the last one repeats. */
  alerts?: Observable<ReorderAlertListing>[];
  /** What the access service answers for `Dashboard.ViewFinancials`. */
  financials?: Observable<boolean>;
}

interface Rendered {
  fixture: ComponentFixture<DashboardComponent>;
  host: HTMLElement;
  getSummary: jest.Mock<Observable<CheapestSupplierSummary>, [readonly number[]]>;
  getSummaries: jest.Mock<Observable<ProductPurchaseCostSummary[]>, [readonly number[]]>;
}

/** The dashboard with every service doubled: these tests are about the card, not the transport. */
async function render(doubles: Doubles = {}): Promise<Rendered> {
  const getLowStock = jest.fn(queued(doubles.alerts ?? [of(listing(reorderList(30)))]));
  const getSummary = jest.fn<Observable<CheapestSupplierSummary>, [readonly number[]]>((ids) =>
    of(cheapestSummary(ids))
  );
  const getSummaries = jest.fn<Observable<ProductPurchaseCostSummary[]>, [readonly number[]]>((ids) =>
    of(ids.map((id) => costSummary(id)))
  );

  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock } },
      {
        provide: MachineService,
        useValue: {
          getAll: () => of([]),
          getRestockSyncReminder: () => of({ applicable: false } as MachineRestockSyncReminderResponse)
        }
      },
      { provide: SiteService, useValue: { getAll: () => of([]) } },
      { provide: NayaxSalesSyncService, useValue: { syncLatest: () => of({ wasRecentlySynced: false, lastSyncedAtUtc: null }) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      { provide: DashboardService, useValue: { getSummary: () => of(SUMMARY) } },
      { provide: ProductPurchaseCostService, useValue: { getSummariesBatched: getSummaries } },
      { provide: CheapestSupplierService, useValue: { getSummary } },
      {
        provide: AccessService,
        useValue: {
          allows: (capability: string) =>
            capability === 'Dashboard.ViewFinancials' ? doubles.financials ?? of(true) : of(true)
        }
      },
      { provide: BusinessSettingsService, useValue: { get: () => of(null) } },
      { provide: BusinessTimeZoneService, useValue: { timeZoneId: 'Australia/Sydney' } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(DashboardComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, getSummary, getSummaries };
}

function reorderSection(host: HTMLElement): HTMLElement {
  return host.querySelector('section[aria-labelledby="reorder-priorities-title"]') as HTMLElement;
}

function text(element: Element | null | undefined): string | null {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
}

/** The product names of the rendered desktop rows, in rendered order. */
function rowNames(host: HTMLElement): (string | null)[] {
  return Array.from(reorderSection(host).querySelectorAll('tbody tr')).map((row) => text(row.querySelector('td')));
}

/** The product names of the rendered narrow-screen cards, in rendered order. */
function cardNames(host: HTMLElement): (string | null)[] {
  return Array.from(reorderSection(host).querySelectorAll('[data-testid="reorder-cards"] > li')).map((card) =>
    text(card.querySelector('p'))
  );
}

function range(host: HTMLElement): string | null {
  return text(reorderSection(host).querySelector('[data-testid="reorder-range"]'));
}

function indicator(host: HTMLElement): string | null {
  return text(reorderSection(host).querySelector('[data-testid="reorder-page-indicator"]'));
}

function previousButton(host: HTMLElement): HTMLButtonElement | null {
  return reorderSection(host).querySelector<HTMLButtonElement>('[data-testid="reorder-previous-page"]');
}

function nextButton(host: HTMLElement): HTMLButtonElement | null {
  return reorderSection(host).querySelector<HTMLButtonElement>('[data-testid="reorder-next-page"]');
}

function goNext(rendered: { fixture: ComponentFixture<DashboardComponent>; host: HTMLElement }): void {
  nextButton(rendered.host)?.click();
  rendered.fixture.detectChanges();
}

function goPrevious(rendered: { fixture: ComponentFixture<DashboardComponent>; host: HTMLElement }): void {
  previousButton(rendered.host)?.click();
  rendered.fixture.detectChanges();
}

describe('Dashboard Reorder priorities paging (issue #1017)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('starts on page 1 with the first five products of the list, in the published order', async () => {
    const { host } = await render();

    expect(REORDER_PAGE_SIZE).toBe(5);
    expect(rowNames(host)).toEqual(['Product 1', 'Product 2', 'Product 3', 'Product 4', 'Product 5']);
    expect(range(host)).toBe('Showing 1–5 of 30 products');
    expect(indicator(host)).toBe('Page 1 of 6');
  });

  it('reaches every product through its pages once, in order, with no gap and no repeat', async () => {
    const rendered = await render();
    const seen: (string | null)[] = [...rowNames(rendered.host)];

    for (let page = 2; page <= 6; page++) {
      goNext(rendered);
      expect(indicator(rendered.host)).toBe(`Page ${page} of 6`);
      seen.push(...rowNames(rendered.host));
    }

    expect(seen).toEqual(reorderList(30).map((p) => p.name));
    expect(new Set(seen).size).toBe(30);
  });

  it('gives 30 products six pages and 7 products two, the second holding products 6 and 7', async () => {
    const thirty = await render();
    expect(indicator(thirty.host)).toBe('Page 1 of 6');
    TestBed.resetTestingModule();

    const seven = await render({ alerts: [of(listing(reorderList(7)))] });
    expect(indicator(seven.host)).toBe('Page 1 of 2');

    goNext(seven);

    expect(rowNames(seven.host)).toEqual(['Product 6', 'Product 7']);
    expect(range(seven.host)).toBe('Showing 6–7 of 7 products');
    expect(indicator(seven.host)).toBe('Page 2 of 2');
  });

  it('goes back to the previous page, and ends where it started', async () => {
    const rendered = await render({ alerts: [of(listing(reorderList(7)))] });

    goNext(rendered);
    goPrevious(rendered);

    expect(rowNames(rendered.host)).toEqual(['Product 1', 'Product 2', 'Product 3', 'Product 4', 'Product 5']);
    expect(range(rendered.host)).toBe('Showing 1–5 of 7 products');
  });

  it('disables Previous on the first page and Next on the last one', async () => {
    const rendered = await render({ alerts: [of(listing(reorderList(7)))] });

    expect(previousButton(rendered.host)?.disabled).toBe(true);
    expect(nextButton(rendered.host)?.disabled).toBe(false);

    goNext(rendered);

    expect(previousButton(rendered.host)?.disabled).toBe(false);
    expect(nextButton(rendered.host)?.disabled).toBe(true);
  });

  it('shows the desktop table and the phone cards on the same page from one page state', async () => {
    const rendered = await render();

    expect(cardNames(rendered.host)).toEqual(rowNames(rendered.host));

    goNext(rendered);

    expect(rowNames(rendered.host)).toEqual(['Product 6', 'Product 7', 'Product 8', 'Product 9', 'Product 10']);
    expect(cardNames(rendered.host)).toEqual(rowNames(rendered.host));
  });

  it('shows the range text and no Previous or Next with exactly five products', async () => {
    const { host } = await render({ alerts: [of(listing(reorderList(5)))] });

    expect(rowNames(host)).toHaveLength(5);
    expect(range(host)).toBe('Showing 1–5 of 5 products');
    expect(previousButton(host)).toBeNull();
    expect(nextButton(host)).toBeNull();
    expect(indicator(host)).toBeNull();
  });

  it('names a single product in the singular, with no pager controls', async () => {
    const { host } = await render({ alerts: [of(listing(reorderList(1)))] });

    expect(range(host)).toBe('Showing 1–1 of 1 product');
    expect(previousButton(host)).toBeNull();
    expect(nextButton(host)).toBeNull();
  });

  it('offers Previous and Next from six products, where the fifth is no longer the last', async () => {
    const rendered = await render({ alerts: [of(listing(reorderList(6)))] });

    expect(range(rendered.host)).toBe('Showing 1–5 of 6 products');
    expect(indicator(rendered.host)).toBe('Page 1 of 2');

    goNext(rendered);

    expect(rowNames(rendered.host)).toEqual(['Product 6']);
    expect(range(rendered.host)).toBe('Showing 6–6 of 6 products');
  });

  it('keeps the existing empty state and shows no pager for an empty list', async () => {
    const { host } = await render({ alerts: [of(listing([]))] });

    expect(reorderSection(host).textContent).toContain('No reorder alerts');
    expect(reorderSection(host).querySelector('[data-testid="reorder-pagination"]')).toBeNull();
    expect(range(host)).toBeNull();
  });

  it('shows no pager while the list is still loading', async () => {
    const { host } = await render({ alerts: [new Subject<ReorderAlertListing>()] });

    expect(reorderSection(host).querySelector('[data-testid="reorder-pagination"]')).toBeNull();
    expect(reorderSection(host).textContent).not.toContain('No reorder alerts');
  });

  it('shows no pager for a failed read, and never presents it as an empty list', async () => {
    const { host } = await render({ alerts: [throwError(() => new Error('down'))] });

    expect(reorderSection(host).querySelector('[data-testid="reorder-pagination"]')).toBeNull();
    expect(reorderSection(host).textContent).toContain('The reorder-alert list could not be loaded');
    expect(reorderSection(host).textContent).not.toContain('No reorder alerts');
  });

  it('stays on the current page when the list is read again and that page still exists', async () => {
    const rendered = await render({ alerts: [of(listing(reorderList(30))), of(listing(reorderList(30)))] });

    goNext(rendered);
    goNext(rendered);
    expect(indicator(rendered.host)).toBe('Page 3 of 6');

    rendered.fixture.componentInstance.refreshLowStock();
    rendered.fixture.detectChanges();

    expect(indicator(rendered.host)).toBe('Page 3 of 6');
    expect(rowNames(rendered.host)).toEqual([
      'Product 11',
      'Product 12',
      'Product 13',
      'Product 14',
      'Product 15'
    ]);
  });

  it('moves to the new last page when a re-read shrinks the list past the current page', async () => {
    const rendered = await render({ alerts: [of(listing(reorderList(30))), of(listing(reorderList(7)))] });

    for (let page = 2; page <= 5; page++) goNext(rendered);
    expect(indicator(rendered.host)).toBe('Page 5 of 6');

    rendered.fixture.componentInstance.refreshLowStock();
    rendered.fixture.detectChanges();

    expect(indicator(rendered.host)).toBe('Page 2 of 2');
    expect(rowNames(rendered.host)).toEqual(['Product 6', 'Product 7']);
    expect(range(rendered.host)).toBe('Showing 6–7 of 7 products');
  });

  it('shows the first page again when a re-read leaves a single page', async () => {
    const rendered = await render({ alerts: [of(listing(reorderList(30))), of(listing(reorderList(3)))] });

    goNext(rendered);
    rendered.fixture.componentInstance.refreshLowStock();
    rendered.fixture.detectChanges();

    expect(rowNames(rendered.host)).toEqual(['Product 1', 'Product 2', 'Product 3']);
    expect(range(rendered.host)).toBe('Showing 1–3 of 3 products');
    expect(nextButton(rendered.host)).toBeNull();
  });

  it('says the card pages the whole list, and keeps the View all link and its count', async () => {
    const { host } = await render();
    const link = host.querySelector<HTMLAnchorElement>('[data-testid="reorder-view-all"]');

    expect(text(reorderSection(host).querySelector('.card-header p'))).toBe(
      'Every product on the reorder list, five per page'
    );
    expect(link?.textContent).toContain('View all 30 products needing ordering');
    expect(link?.getAttribute('href')).toBe('/products/needs-ordering');
  });

  it('offers no rows-per-page selector: the page size is fixed', async () => {
    const { host } = await render();

    expect(reorderSection(host).querySelector('select')).toBeNull();
    expect(reorderSection(host).textContent).not.toContain('Rows per page');
  });

  it('makes the pager controls real buttons with accessible names, reachable from the keyboard', async () => {
    const { host } = await render();
    const next = nextButton(host) as HTMLButtonElement;
    const previous = previousButton(host) as HTMLButtonElement;

    for (const button of [previous, next]) {
      expect(button.tagName).toBe('BUTTON');
      expect(button.getAttribute('type')).toBe('button');
    }
    expect(text(previous)).toBe('Previous');
    expect(text(next)).toBe('Next');
    // The range text is announced as the live region the pattern of issue #886 established.
    expect(reorderSection(host).querySelector('[data-testid="reorder-range"]')?.tagName).toBe('OUTPUT');

    next.focus();
    expect(document.activeElement).toBe(next);
    expect(next.tabIndex).toBe(0);
  });
});

/**
 * The request rule, over the real `CheapestSupplierService` and `ProductPurchaseCostService` and a
 * mocked transport, because the number and the shape of the requests is exactly what is at stake:
 * one logical enrichment read per reorder read for every listed id, bounded by the endpoints' own
 * limits, and nothing at all when a page turns.
 */
describe('Dashboard Reorder priorities enrichment reads (issue #1017)', () => {
  const suppliersUrl = 'http://localhost/api/cheapest-suppliers/summary';
  const costsUrl = 'http://localhost/api/products/purchase-cost-summary';

  afterEach(() => TestBed.resetTestingModule());

  async function renderOverHttp(
    products: Product[],
    financials = true
  ): Promise<{ fixture: ComponentFixture<DashboardComponent>; host: HTMLElement; http: HttpTestingController }> {
    await TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        CheapestSupplierService,
        ProductPurchaseCostService,
        { provide: ConfigService, useValue: { apiBaseUrl: 'http://localhost/api/' } },
        { provide: ProductService, useValue: { getLowStock: () => of(listing(products)) } },
        {
          provide: MachineService,
          useValue: {
            getAll: () => of([]),
            getRestockSyncReminder: () => of({ applicable: false } as MachineRestockSyncReminderResponse)
          }
        },
        { provide: SiteService, useValue: { getAll: () => of([]) } },
        { provide: NayaxSalesSyncService, useValue: { syncLatest: () => of({ wasRecentlySynced: false, lastSyncedAtUtc: null }) } },
        { provide: SupportSessionService, useValue: { active$: of(false) } },
        { provide: DashboardService, useValue: { getSummary: () => of(SUMMARY) } },
        {
          provide: AccessService,
          useValue: {
            allows: (capability: string) => of(capability === 'Dashboard.ViewFinancials' ? financials : true)
          }
        },
        { provide: BusinessSettingsService, useValue: { get: () => of(null) } },
        { provide: BusinessTimeZoneService, useValue: { timeZoneId: 'Australia/Sydney' } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(DashboardComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, http: TestBed.inject(HttpTestingController) };
  }

  function supplierRequests(http: HttpTestingController) {
    return http.match((request) => request.url === suppliersUrl);
  }

  function costRequests(http: HttpTestingController) {
    return http.match((request) => request.url === costsUrl);
  }

  it('reads the cheapest suppliers for every listed id, in batches within the endpoint bound', async () => {
    const ids = CHEAPEST_SUPPLIER_MAX_IDS + 30;
    const { http } = await renderOverHttp(reorderList(ids));
    const requests = supplierRequests(http);
    const batches = requests.map((request) => (request.request.body as { productIds: number[] }).productIds);

    expect(batches).toHaveLength(2);
    expect(batches.map((batch) => batch.length)).toEqual([CHEAPEST_SUPPLIER_MAX_IDS, 30]);
    for (const batch of batches) expect(batch.length).toBeLessThanOrEqual(CHEAPEST_SUPPLIER_MAX_IDS);
    // Every listed product is asked about exactly once, over the batches together.
    expect(batches.flat()).toEqual(reorderList(ids).map((p) => p.id));

    for (const request of requests) {
      request.flush(cheapestSummary((request.request.body as { productIds: number[] }).productIds));
    }
    for (const request of costRequests(http)) request.flush([]);
    http.verify();
  });

  // The requirement is one logical purchase-cost load per reorder read, not one HTTP request: one
  // unbounded GET carrying 310 real 15-digit ids already exceeds Kestrel's default request line
  // (review of 1 November 2026), so the load is split into bounded GETs and their answers joined.
  it('reads the purchase costs for every listed id once, in bounded GETs safe for real id lengths', async () => {
    const products = realisticReorderList(1000);
    const { fixture, host, http } = await renderOverHttp(products);
    const requests = costRequests(http);

    expect(requests).toHaveLength(Math.ceil(1000 / PURCHASE_COST_MAX_IDS_PER_REQUEST));
    const batches = requests.map((request) => request.request.params.getAll('productIds') ?? []);
    for (const request of requests) {
      expect(request.request.method).toBe('GET');
      const requestLine = `GET ${request.request.urlWithParams} HTTP/1.1`;
      expect(requestLine.length).toBeLessThan(KESTREL_DEFAULT_MAX_REQUEST_LINE);
    }
    for (const batch of batches) {
      expect(batch.length).toBeGreaterThan(0);
      expect(batch.length).toBeLessThanOrEqual(PURCHASE_COST_MAX_IDS_PER_REQUEST);
    }
    // Every listed id is asked for exactly once, over the batches together, in the list's order.
    expect(batches.flat()).toEqual(products.map((p) => String(p.id)));

    for (const request of requests) {
      request.flush((request.request.params.getAll('productIds') ?? []).map((id) => costSummary(Number(id))));
    }
    for (const request of supplierRequests(http)) {
      request.flush(cheapestSummary((request.request.body as { productIds: number[] }).productIds));
    }
    fixture.detectChanges();

    // A page whose products sit in a later batch shows their costs, and turning there sends nothing.
    const rendered = { fixture, host };
    for (let page = 2; page <= 200; page++) goNext(rendered);
    expect(indicator(host)).toBe('Page 200 of 200');
    expect(rowNames(host)).toEqual(products.slice(995).map((p) => p.name));
    const rows = Array.from(reorderSection(host).querySelectorAll('tbody tr'));
    for (const row of rows) expect(row.textContent).toContain('Acme · $1.25');
    http.verify();
  });

  it('shows the reorder rows and no costs when one purchase-cost batch fails, with Retry', async () => {
    const products = realisticReorderList(310);
    const { fixture, host, http } = await renderOverHttp(products);
    const requests = costRequests(http);
    expect(requests.length).toBeGreaterThan(1);

    // Every batch but the last answers; the last fails. No partial set of costs may be published,
    // because a product missing from a partial answer would read as "no purchases recorded".
    for (const request of requests.slice(0, -1)) {
      request.flush((request.request.params.getAll('productIds') ?? []).map((id) => costSummary(Number(id))));
    }
    requests[requests.length - 1].flush('boom', { status: 500, statusText: 'Server Error' });
    for (const request of supplierRequests(http)) {
      request.flush(cheapestSummary((request.request.body as { productIds: number[] }).productIds));
    }
    fixture.detectChanges();

    expect(rowNames(host)).toEqual(products.slice(0, REORDER_PAGE_SIZE).map((p) => p.name));
    expect(range(host)).toBe('Showing 1–5 of 310 products');
    const section = reorderSection(host);
    expect(section.textContent).not.toContain('Acme · $1.25');
    expect(section.textContent).toContain('Unavailable');
    expect(section.textContent).toContain('The last and cheapest purchase costs could not be loaded.');
    const retry = Array.from(section.querySelectorAll('button')).find((button) => text(button) === 'Retry');
    expect(retry).toBeDefined();

    // Retry is again one logical load over bounded batches, and its success fills the costs in.
    retry?.click();
    fixture.detectChanges();
    const retried = costRequests(http);
    expect(retried).toHaveLength(requests.length);
    for (const request of retried) {
      request.flush((request.request.params.getAll('productIds') ?? []).map((id) => costSummary(Number(id))));
    }
    fixture.detectChanges();
    expect(reorderSection(host).textContent).toContain('Acme · $1.25');
    http.verify();
  });

  it('sends no request when the page changes, and enriches the rows of a later page from the one read', async () => {
    const ids = CHEAPEST_SUPPLIER_MAX_IDS + 30;
    const rendered = await renderOverHttp(reorderList(ids));
    for (const request of supplierRequests(rendered.http)) {
      request.flush(cheapestSummary((request.request.body as { productIds: number[] }).productIds));
    }
    for (const request of costRequests(rendered.http)) {
      request.flush((request.request.params.getAll('productIds') ?? []).map((id) => costSummary(Number(id))));
    }
    rendered.fixture.detectChanges();

    // The last page: its products were in the second batch, and it has never been rendered before.
    for (let page = 2; page <= 46; page++) goNext(rendered);

    expect(indicator(rendered.host)).toBe('Page 46 of 46');
    expect(rowNames(rendered.host)).toEqual(['Product 226', 'Product 227', 'Product 228', 'Product 229', 'Product 230']);
    const cells = Array.from(
      reorderSection(rendered.host).querySelectorAll('table [data-testid="cheapest-supplier-cell"]')
    );
    expect(cells).toHaveLength(5);
    for (const cell of cells) expect(cell.textContent).toContain('JB Metro');
    expect(reorderSection(rendered.host).textContent).toContain('Acme · $1.25');

    rendered.http.verify();
  });

  it('asks for no purchase cost at all without Dashboard.ViewFinancials, and still reads the suppliers', async () => {
    const { http } = await renderOverHttp(reorderList(7), false);

    expect(costRequests(http)).toHaveLength(0);
    const requests = supplierRequests(http);
    expect(requests).toHaveLength(1);
    expect((requests[0].request.body as { productIds: number[] }).productIds).toEqual([1, 2, 3, 4, 5, 6, 7]);

    requests[0].flush(cheapestSummary([1, 2, 3, 4, 5, 6, 7]));
    http.verify();
  });
});
