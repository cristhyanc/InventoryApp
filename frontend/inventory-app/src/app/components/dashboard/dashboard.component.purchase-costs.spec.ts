import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { DashboardSummary, Product, ReorderAlertListing } from '../../models/models';
import { ProductPurchaseCostSummary } from '../../models/product-purchase-cost-summary';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { DashboardService } from '../../services/dashboard.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { AccessService } from '../../access/access.service';
import { BusinessSettingsService } from '../../services/business-settings.service';
import { SupportSessionService } from '../../support-session/support-session.service';

/**
 * The Reorder Alerts table's "Last purchase" and "Cheapest purchase" columns (issue #789, C of
 * #786): what the business actually last paid and the cheapest it has ever paid, in place of the
 * selling price #792 removed.
 *
 * Every figure comes from `GET /api/products/purchase-cost-summary` (issue #788) through
 * `ProductPurchaseCostService`; nothing here picks a cheapest, sorts a history or derives a saving.
 * The three answers that response can give are deliberately distinct and are what most of these
 * specs are about: still loading, loaded and this product has never been bought (`—`), and the
 * request failed (`Unavailable`, with Retry). A recorded `$0.00` is a real cost and is none of them.
 */

const SUMMARY: DashboardSummary = {
  asOfUtc: '2026-10-21T09:30:00Z',
  businessDate: '2026-10-21T00:00:00Z',
  salesThisWeek: {
    sales: 300,
    transactionCount: 3,
    period: {
      startUtc: '2026-10-19T13:00:00Z',
      endUtc: '2026-10-21T09:30:00Z',
      firstBusinessDate: '2026-10-19T00:00:00Z',
      lastBusinessDate: '2026-10-21T00:00:00Z'
    },
    comparisonPeriod: {
      startUtc: '2026-10-12T13:00:00Z',
      endUtc: '2026-10-14T09:30:00Z',
      firstBusinessDate: '2026-10-12T00:00:00Z',
      lastBusinessDate: '2026-10-14T00:00:00Z'
    },
    isComparisonAvailable: true,
    comparisonSales: 240,
    comparisonTransactionCount: 2,
    changeAmount: 60,
    changePercent: 25,
    comparisonNote: null
  },
  needsRefill: {
    machinesNeedingRefill: 2,
    machinesWithEmptySelections: 1,
    machinesWithLowSelections: 1,
    emptySelectionCount: 1,
    lowSelectionCount: 2,
    machinesEvaluated: 5,
    selectionsEvaluated: 20
  },
  needsOrdering: { productsNeedingOrdering: 4, productsEvaluated: 50, mode: 'MachineAware' },
  inventory: {
    inventoryValueAtCost: 1234.56,
    isInventoryValueComplete: true,
    productsWithUnknownCost: 0,
    productCount: 50,
    unitsInStorage: 320
  }
};

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Cola 375mL',
    sku: 'COLA-375',
    description: null,
    averageUnitCost: 1.2,
    costingQuantity: 10,
    inventoryValue: 12,
    machinePrice: 3.5,
    commissionValue: 0,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 2,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 4,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 30,
    needToOrder: 28,
    mdbCode: null,
    unit: 'unit',
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

function costSummary(overrides: Partial<ProductPurchaseCostSummary> = {}): ProductPurchaseCostSummary {
  return {
    productId: 1,
    lastUnitCost: 1.25,
    lastSupplierName: 'Acme',
    lastPurchaseDate: '2026-10-01',
    lowestUnitCost: 0.95,
    lowestSupplierName: 'Budget Wholesale',
    lowestPurchaseDate: '2026-08-14',
    ...overrides
  };
}

function machineAwareAlerts(products: Product[]): ReorderAlertListing {
  return { products, mode: 'MachineAware', machineNeedUnavailableReason: null };
}

interface Doubles {
  /** The reorder-alert listings, one per `getLowStock` call; the last one repeats. */
  alerts?: Observable<ReorderAlertListing>[];
  /** The purchase-cost answers, one per request; the last one repeats. */
  costs?: Observable<ProductPurchaseCostSummary[]>[];
  /** What the access service answers for `Dashboard.ViewFinancials`. */
  financials?: Observable<boolean>;
}

interface Rendered {
  fixture: ComponentFixture<DashboardComponent>;
  host: HTMLElement;
  getSummaries: jest.Mock<Observable<ProductPurchaseCostSummary[]>, [readonly number[]]>;
}

function queued<T>(answers: Observable<T>[]): () => Observable<T> {
  let call = 0;
  return () => answers[Math.min(call++, answers.length - 1)];
}

async function render(doubles: Doubles = {}): Promise<Rendered> {
  const getLowStock = jest.fn(queued(doubles.alerts ?? [of(machineAwareAlerts([product()]))]));
  const getSummaries = jest.fn<Observable<ProductPurchaseCostSummary[]>, [readonly number[]]>(
    queued(doubles.costs ?? [of([costSummary()])])
  );

  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock } },
      { provide: MachineService, useValue: { getAll: () => of([]) } },
      { provide: SiteService, useValue: { getAll: () => of([]) } },
      {
        provide: NayaxSalesSyncService,
        // The `204` answer the service maps (issue #843); the skip is its own spec's subject.
        useValue: { syncLatest: () => of({ wasRecentlySynced: false, lastSyncedAtUtc: null }) }
      },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      { provide: DashboardService, useValue: { getSummary: () => of(SUMMARY) } },
      { provide: ProductPurchaseCostService, useValue: { getSummaries } },
      { provide: AccessService, useValue: { allows: () => doubles.financials ?? of(true) } },
      { provide: BusinessSettingsService, useValue: { get: () => of(null) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(DashboardComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, getSummaries };
}

function alertsSection(host: HTMLElement): HTMLElement {
  const section = Array.from(host.querySelectorAll('section.card')).find((el) =>
    el.textContent?.includes('Reorder Alerts')
  );
  return section as HTMLElement;
}

function alertHeaders(host: HTMLElement): (string | undefined)[] {
  return Array.from(alertsSection(host).querySelectorAll('th')).map((th) => th.textContent?.trim());
}

/** One array of trimmed cell texts per rendered reorder-alert row. */
function alertRows(host: HTMLElement): (string | undefined)[][] {
  return Array.from(alertsSection(host).querySelectorAll('tbody tr')).map((row) =>
    Array.from(row.querySelectorAll('td')).map((cell) => cell.textContent?.trim())
  );
}

function retryButton(host: HTMLElement): HTMLButtonElement | null {
  const button = Array.from(alertsSection(host).querySelectorAll('button')).find(
    (candidate) => candidate.textContent?.trim() === 'Retry'
  );
  return button ?? null;
}

describe('Reorder Alerts purchase-cost columns (issue #789)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('adds Last purchase and Cheapest purchase after Need to Order and removes Stock after machine need', async () => {
    const { host } = await render();

    expect(alertHeaders(host)).toEqual([
      'Product',
      'Stock',
      'Low Stock Threshold',
      'Machine need',
      'Restock To',
      'On order',
      'Need to Order',
      'Last purchase',
      'Cheapest purchase'
    ]);
  });

  it('keeps the Unit Price column absent', async () => {
    const { host } = await render();

    expect(alertHeaders(host)).not.toContain('Unit Price');
  });

  it('shows the supplier and unit cost of the last and the cheapest purchase', async () => {
    const { host } = await render();

    expect(alertRows(host)[0].slice(-2)).toEqual(['Acme · $1.25', 'Budget Wholesale · $0.95']);
  });

  it('names an unrecorded supplier None, independently of the cost beside it', async () => {
    const { host } = await render({
      costs: [of([costSummary({ lastSupplierName: null, lowestSupplierName: null })])]
    });

    expect(alertRows(host)[0].slice(-2)).toEqual(['None · $1.25', 'None · $0.95']);
  });

  it('shows a genuine zero cost as $0.00 with its supplier, never as no history', async () => {
    const { host } = await render({
      costs: [of([costSummary({ lowestUnitCost: 0, lowestSupplierName: 'Acme' })])]
    });

    expect(alertRows(host)[0].slice(-2)).toEqual(['Acme · $1.25', 'Acme · $0.00']);
  });

  it('shows an em dash for a product the answer carries no purchase history for', async () => {
    const { host } = await render({
      alerts: [of(machineAwareAlerts([product(), product({ id: 2, name: 'Water 600mL' })]))],
      costs: [of([costSummary()])]
    });

    const rows = alertRows(host);
    expect(rows[0].slice(-2)).toEqual(['Acme · $1.25', 'Budget Wholesale · $0.95']);
    expect(rows[1].slice(-2)).toEqual(['—', '—']);
  });

  it('requests the costs once, for the alert product ids only', async () => {
    const { getSummaries } = await render({
      alerts: [of(machineAwareAlerts([product(), product({ id: 2, name: 'Water 600mL' })]))]
    });

    expect(getSummaries).toHaveBeenCalledTimes(1);
    expect(getSummaries).toHaveBeenCalledWith([1, 2]);
  });

  it('asks for nothing when the alert list is empty, rather than for the whole catalogue', async () => {
    const { getSummaries } = await render({ alerts: [of(machineAwareAlerts([]))] });

    expect(getSummaries).not.toHaveBeenCalled();
  });

  it('shows Loading while the request is pending, never an em dash', async () => {
    const { host } = await render({ costs: [new Subject<ProductPurchaseCostSummary[]>()] });

    expect(alertRows(host)[0].slice(-2)).toEqual(['Loading', 'Loading']);
    expect(alertsSection(host).textContent).not.toContain('—');
    expect(retryButton(host)).toBeNull();
  });
});

describe('Reorder Alerts purchase costs without Dashboard.ViewFinancials (issue #789)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows neither column and makes no request', async () => {
    const { host, getSummaries } = await render({ financials: of(false) });

    expect(alertHeaders(host)).not.toContain('Last purchase');
    expect(alertHeaders(host)).not.toContain('Cheapest purchase');
    expect(getSummaries).not.toHaveBeenCalled();
    expect(alertsSection(host).textContent).not.toContain('Unavailable');
  });

  it('still shows the rest of the Reorder Alerts row', async () => {
    const { host } = await render({ financials: of(false) });

    expect(alertHeaders(host)).toEqual([
      'Product',
      'Stock',
      'Low Stock Threshold',
      'Machine need',
      'Restock To',
      'On order',
      'Need to Order'
    ]);
    expect(alertRows(host)[0]).toEqual(['Cola 375mL', '2', '5', '4', '30', '0', '28']);
  });

  it('requests the costs once the capability answer arrives after the alert list', async () => {
    const financials = new Subject<boolean>();
    const { fixture, host, getSummaries } = await render({ financials });

    expect(getSummaries).not.toHaveBeenCalled();

    financials.next(true);
    fixture.detectChanges();

    expect(getSummaries).toHaveBeenCalledTimes(1);
    expect(alertRows(host)[0].slice(-2)).toEqual(['Acme · $1.25', 'Budget Wholesale · $0.95']);
  });
});

describe('Reorder Alerts purchase costs when the request fails (issue #789)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows Unavailable in both columns with a Retry control, and renders the rest of the dashboard', async () => {
    const { host } = await render({
      costs: [throwError(() => new HttpErrorResponse({ status: 500 }))]
    });

    expect(alertRows(host)[0].slice(-2)).toEqual(['Unavailable', 'Unavailable']);
    expect(retryButton(host)).not.toBeNull();
    // The dashboard around the two columns is untouched by their failure.
    expect(alertRows(host)[0].slice(0, 7)).toEqual(['Cola 375mL', '2', '5', '4', '30', '0', '28']);
    expect(host.textContent).toContain('Sales this week');
    expect(host.textContent).toContain('$1,234.56');
  });

  it('distinguishes a failure from a product with no purchase history', async () => {
    const { host } = await render({
      costs: [throwError(() => new HttpErrorResponse({ status: 500 }))]
    });

    expect(alertsSection(host).textContent).not.toContain('—');
  });

  it('reloads the costs when Retry is pressed and then shows them', async () => {
    const { fixture, host, getSummaries } = await render({
      costs: [throwError(() => new HttpErrorResponse({ status: 500 })), of([costSummary()])]
    });

    retryButton(host)?.click();
    fixture.detectChanges();

    expect(getSummaries).toHaveBeenCalledTimes(2);
    expect(alertRows(host)[0].slice(-2)).toEqual(['Acme · $1.25', 'Budget Wholesale · $0.95']);
    expect(retryButton(host)).toBeNull();
  });

  it('reports a refused request as unavailable rather than as no purchase history', async () => {
    const { host } = await render({
      costs: [throwError(() => new HttpErrorResponse({ status: 403 }))]
    });

    expect(alertRows(host)[0].slice(-2)).toEqual(['Unavailable', 'Unavailable']);
  });
});

describe('Reorder Alerts purchase costs across a changed product set (issue #789)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('clears the costs already read when the displayed product set changes', async () => {
    const pending = new Subject<ProductPurchaseCostSummary[]>();
    const { fixture, host } = await render({
      alerts: [
        of(machineAwareAlerts([product()])),
        of(machineAwareAlerts([product(), product({ id: 2, name: 'Water 600mL' })]))
      ],
      costs: [of([costSummary()]), pending]
    });

    expect(alertRows(host)[0].slice(-2)).toEqual(['Acme · $1.25', 'Budget Wholesale · $0.95']);

    fixture.componentInstance.refreshLowStock();
    fixture.detectChanges();

    // The same product, read again: its previous answer must not stand in for the pending one.
    expect(alertRows(host)[0].slice(-2)).toEqual(['Loading', 'Loading']);
    expect(alertRows(host)[1].slice(-2)).toEqual(['Loading', 'Loading']);
  });

  it('never lets a stale answer overwrite the costs of the product set now displayed', async () => {
    const stale = new Subject<ProductPurchaseCostSummary[]>();
    const { fixture, host } = await render({
      alerts: [
        of(machineAwareAlerts([product()])),
        of(machineAwareAlerts([product({ id: 2, name: 'Water 600mL' })]))
      ],
      costs: [stale, of([costSummary({ productId: 2, lastSupplierName: 'Fresh Supply', lastUnitCost: 2.5 })])]
    });

    fixture.componentInstance.refreshLowStock();
    fixture.detectChanges();

    expect(alertRows(host)[0].slice(-2)).toEqual(['Fresh Supply · $2.50', 'Budget Wholesale · $0.95']);

    // The first request answers last, for a product set that is no longer displayed.
    stale.next([costSummary({ productId: 2, lastSupplierName: 'Stale Supply', lastUnitCost: 9.99 })]);
    fixture.detectChanges();

    expect(alertRows(host)[0].slice(-2)).toEqual(['Fresh Supply · $2.50', 'Budget Wholesale · $0.95']);
  });

  it('never lets a stale failure replace the costs of the product set now displayed with Unavailable', async () => {
    const stale = new Subject<ProductPurchaseCostSummary[]>();
    const { fixture, host } = await render({
      alerts: [
        of(machineAwareAlerts([product()])),
        of(machineAwareAlerts([product({ id: 2, name: 'Water 600mL' })]))
      ],
      costs: [stale, of([costSummary({ productId: 2 })])]
    });

    fixture.componentInstance.refreshLowStock();
    fixture.detectChanges();

    stale.error(new HttpErrorResponse({ status: 500 }));
    fixture.detectChanges();

    expect(alertRows(host)[0].slice(-2)).toEqual(['Acme · $1.25', 'Budget Wholesale · $0.95']);
    expect(retryButton(host)).toBeNull();
  });
});
