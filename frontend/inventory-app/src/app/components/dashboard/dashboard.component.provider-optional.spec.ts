import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import {
  DashboardSalesThisWeek,
  DashboardSummary,
  Machine,
  Product,
  ReorderAlertListing,
  Site
} from '../../models/models';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncResult, NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { DashboardService } from '../../services/dashboard.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { AccessService } from '../../access/access.service';
import { BusinessSettingsService } from '../../services/business-settings.service';
import { SupportSessionService } from '../../support-session/support-session.service';

/**
 * The Dashboard for a business whose Nayax connection is not usable, or whose Nayax account has not
 * granted one of the fleet reads (issue #727, child D2 of issue #329; the API half is #726).
 *
 * The page learns the provider's availability from its own summary - `needsRefill` is published as
 * `{ available: false, reason }` with `NayaxNotConnected` or `PermissionNotGranted`, and
 * `needsOrdering` carries the `mode` its count was produced in - and there is deliberately no
 * second probe for it. The shared availability signal is issue #728.
 */

/** What the synchronization answers when it ran: the `204` the service maps (issue #843). */
const SYNC_RAN: NayaxSalesSyncResult = { wasRecentlySynced: false, lastSyncedAtUtc: null };

const SALES_THIS_WEEK: DashboardSalesThisWeek = {
  sales: 300,
  transactionCount: 3,
  period: {
    startUtc: '2026-10-12T14:00:00Z',
    endUtc: '2026-10-15T09:30:00Z',
    firstBusinessDate: '2026-10-12T00:00:00Z',
    lastBusinessDate: '2026-10-15T00:00:00Z'
  },
  comparisonPeriod: {
    startUtc: '2026-10-05T14:00:00Z',
    endUtc: '2026-10-08T09:30:00Z',
    firstBusinessDate: '2026-10-05T00:00:00Z',
    lastBusinessDate: '2026-10-08T00:00:00Z'
  },
  isComparisonAvailable: true,
  comparisonSales: 240,
  comparisonTransactionCount: 2,
  changeAmount: 60,
  changePercent: 25,
  comparisonNote: null
};

/** A business with a usable connection and the fleet reads granted: today's dashboard. */
const CONNECTED_SUMMARY: DashboardSummary = {
  asOfUtc: '2026-10-15T09:30:00Z',
  businessDate: '2026-10-15T00:00:00Z',
  salesThisWeek: SALES_THIS_WEEK,
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

/** The summary of a business with no usable Nayax connection (issue #726). */
const NOT_CONNECTED_SUMMARY: DashboardSummary = {
  ...CONNECTED_SUMMARY,
  needsRefill: { available: false, reason: 'NayaxNotConnected' },
  needsOrdering: { productsNeedingOrdering: 4, productsEvaluated: 50, mode: 'StorageOnly' }
};

/** `PendingPermissions`: the credentials work, and Nayax has not granted the fleet reads. */
const PERMISSION_NOT_GRANTED_SUMMARY: DashboardSummary = {
  ...CONNECTED_SUMMARY,
  needsRefill: { available: false, reason: 'PermissionNotGranted' },
  needsOrdering: { productsNeedingOrdering: 4, productsEvaluated: 50, mode: 'StorageOnly' }
};

/** A read-only support session's own shape, unchanged since #552/#554. */
const SUPPORT_VIEW_SUMMARY: DashboardSummary = {
  ...CONNECTED_SUMMARY,
  needsRefill: { available: false, reason: 'SupportView' },
  needsOrdering: { available: false, reason: 'SupportView' }
};

const MACHINE: Machine = {
  machineID: 1,
  machineName: 'Machine A',
  machineNumber: 'A1',
  todayGrossRevenue: 12,
  currentWeekGrossRevenue: 34,
  lastWeekGrossRevenue: 56,
  twoWeeksAgoGrossRevenue: 78,
  todayDirectProfit: 1,
  twoWeeksAgoDirectProfit: 2,
  lastWeekDirectProfit: 3,
  currentWeekDirectProfit: 4,
  previousComparableWeekGrossRevenue: 90,
  previousComparableWeekDirectProfit: 5,
  monthToDateGrossRevenue: 100,
  monthToDateDirectProfit: 6,
  profitabilityStatus: null
};

const SITE: Site = {
  siteId: 1,
  siteName: 'Site A',
  machineCount: 1,
  totalStockPercentage: 50,
  lowProductCount: 0,
  emptyProductCount: 0,
  todayRevenue: 12,
  currentWeekRevenue: 34,
  previousComparableWeekRevenue: 90
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
    machineReplenishmentNeed: 0,
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

function storageOnlyAlerts(products: Product[]): ReorderAlertListing {
  return { products, mode: 'StorageOnly', machineNeedUnavailableReason: 'NayaxNotConnected' };
}

function machineAwareAlerts(products: Product[]): ReorderAlertListing {
  return { products, mode: 'MachineAware', machineNeedUnavailableReason: null };
}

interface Doubles {
  summary$?: Observable<DashboardSummary>;
  alerts$?: Observable<ReorderAlertListing>;
  syncLatest?: () => Observable<NayaxSalesSyncResult>;
  machines$?: Observable<Machine[]>;
  sites$?: Observable<Site[]>;
  sessionActive?: Observable<boolean>;
  canViewFinancials?: boolean;
}

function createComponent(doubles: Doubles = {}): DashboardComponent {
  return new DashboardComponent(
    { getLowStock: () => doubles.alerts$ ?? of(machineAwareAlerts([])) } as unknown as ProductService,
    { getAll: () => doubles.machines$ ?? of([MACHINE]) } as unknown as MachineService,
    { getAll: () => doubles.sites$ ?? of([SITE]) } as unknown as SiteService,
    { syncLatest: doubles.syncLatest ?? (() => of(SYNC_RAN)) } as unknown as NayaxSalesSyncService,
    { getSummary: () => doubles.summary$ ?? of(CONNECTED_SUMMARY) } as unknown as DashboardService,
    // Issue #789's purchase-cost columns are `dashboard.component.purchase-costs.spec.ts`'s
    // subject; this file is about the provider-optional states of the page around them.
    { getSummaries: () => of([]) } as unknown as ProductPurchaseCostService,
    { active$: doubles.sessionActive ?? of(false) } as unknown as SupportSessionService,
    { allows: () => of(doubles.canViewFinancials ?? true) } as unknown as AccessService
  );
}

async function renderDashboard(doubles: Doubles = {}): Promise<HTMLElement> {
  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      {
        provide: ProductService,
        useValue: { getLowStock: () => doubles.alerts$ ?? of(machineAwareAlerts([])) }
      },
      { provide: MachineService, useValue: { getAll: () => doubles.machines$ ?? of([MACHINE]) } },
      { provide: SiteService, useValue: { getAll: () => doubles.sites$ ?? of([SITE]) } },
      { provide: NayaxSalesSyncService, useValue: { syncLatest: doubles.syncLatest ?? (() => of(SYNC_RAN)) } },
      { provide: SupportSessionService, useValue: { active$: doubles.sessionActive ?? of(false) } },
      { provide: DashboardService, useValue: { getSummary: () => doubles.summary$ ?? of(CONNECTED_SUMMARY) } },
      { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
      { provide: AccessService, useValue: { allows: () => of(doubles.canViewFinancials ?? true) } },
      { provide: BusinessSettingsService, useValue: { get: () => of(null) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(DashboardComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function cardLabels(host: HTMLElement): (string | undefined)[] {
  return Array.from(host.querySelectorAll('.stat-card-label')).map((el) => el.textContent?.trim());
}

function sectionTitles(host: HTMLElement): (string | undefined)[] {
  return Array.from(host.querySelectorAll('section.card .card-title')).map((el) => el.textContent?.trim());
}

describe('Dashboard without a usable provider (issue #727)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('hides the Sales this week and Needs refill cards and keeps Needs ordering and Inventory', async () => {
    const host = await renderDashboard({
      summary$: of(NOT_CONNECTED_SUMMARY),
      alerts$: of(storageOnlyAlerts([product()]))
    });

    expect(cardLabels(host)).toEqual(['Needs ordering', 'Inventory']);
  });

  it('hides the Sites and Machines sections and leaves reorder alerts as the main section', async () => {
    const host = await renderDashboard({
      summary$: of(NOT_CONNECTED_SUMMARY),
      alerts$: of(storageOnlyAlerts([product()]))
    });

    expect(sectionTitles(host)).toEqual(['⚠️ Reorder Alerts']);
    expect(host.textContent).not.toContain('No sites available yet');
    expect(host.textContent).not.toContain('No machines available yet');
  });

  it('never calls the sales sync and shows no sync warning', async () => {
    const syncLatest = jest.fn(() => of(SYNC_RAN));
    const component = createComponent({ summary$: of(NOT_CONNECTED_SUMMARY), syncLatest });

    component.ngOnInit();

    expect(syncLatest).not.toHaveBeenCalled();
    expect(component.isSalesSyncFailed).toBe(false);
    expect(component.isSalesSyncRecentlyRefreshed).toBe(false);
    expect(component.isSalesSyncSkippedInSupportView).toBe(false);

    const host = await renderDashboard({ summary$: of(NOT_CONNECTED_SUMMARY) });
    expect(host.textContent).not.toContain('could not be synchronized');
    expect(host.textContent).not.toContain('Nayax is not available in support view');
  });

  it('asks for neither the sites nor the machines it is not showing', () => {
    const sites = jest.fn(() => of([SITE]));
    const machines = jest.fn(() => of([MACHINE]));
    const component = new DashboardComponent(
      { getLowStock: () => of(storageOnlyAlerts([])) } as unknown as ProductService,
      { getAll: machines } as unknown as MachineService,
      { getAll: sites } as unknown as SiteService,
      { syncLatest: () => of(SYNC_RAN) } as unknown as NayaxSalesSyncService,
      { getSummary: () => of(NOT_CONNECTED_SUMMARY) } as unknown as DashboardService,
      { getSummaries: () => of([]) } as unknown as ProductPurchaseCostService,
      { active$: of(false) } as unknown as SupportSessionService,
      { allows: () => of(true) } as unknown as AccessService
    );

    component.ngOnInit();

    expect(sites).not.toHaveBeenCalled();
    expect(machines).not.toHaveBeenCalled();
  });

  it('labels the storage-only Needs ordering count rather than presenting it as machine-aware', async () => {
    const host = await renderDashboard({
      summary$: of(NOT_CONNECTED_SUMMARY),
      alerts$: of(storageOnlyAlerts([product()]))
    });

    expect(host.textContent).toContain(
      "Storage stock only: these figures do not include the machines' current replenishment need, because Nayax is not connected."
    );
  });

  it('shows the real storage-only ordering count, not "Unavailable"', () => {
    const component = createComponent({ summary$: of(NOT_CONNECTED_SUMMARY) });
    component.ngOnInit();

    expect(component.orderingValueDisplay(component.summary!.needsOrdering)).toBe('4');
  });

  it('leaves no machine-need column in the reorder alerts table, where the need was never read', async () => {
    const host = await renderDashboard({
      summary$: of(NOT_CONNECTED_SUMMARY),
      alerts$: of(storageOnlyAlerts([product()]))
    });

    const headers = Array.from(host.querySelectorAll('th')).map((th) => th.textContent?.trim());
    expect(headers).not.toContain('Machine need');
    expect(headers).toContain('Need to Order');
  });
});

describe('Dashboard for a feature Nayax has not granted (issue #727)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('keeps every card and section, because the connection itself is usable', async () => {
    const host = await renderDashboard({ summary$: of(PERMISSION_NOT_GRANTED_SUMMARY) });

    expect(cardLabels(host)).toEqual(['Sales this week', 'Needs refill', 'Needs ordering', 'Inventory']);
    expect(sectionTitles(host)).toEqual(['📍 Sites', '🛠️ Machines', '⚠️ Reorder Alerts']);
  });

  it('shows the permission message on the Needs refill card, not the support-view sentence', async () => {
    const host = await renderDashboard({ summary$: of(PERMISSION_NOT_GRANTED_SUMMARY) });

    expect(host.textContent).toContain(
      'Machine refill status is unavailable because Nayax has not granted this application permission to read it.'
    );
    expect(host.textContent).not.toContain('Nayax is not available in support view');
  });

  it('shows the permission message for a section whose own request was refused for permission', async () => {
    const refused = throwError(
      () => new HttpErrorResponse({ status: 409, error: { code: 'nayax_permission_not_granted' } })
    );
    const host = await renderDashboard({
      summary$: of(PERMISSION_NOT_GRANTED_SUMMARY),
      machines$: refused as Observable<Machine[]>
    });

    expect(host.textContent).toContain(
      'The Machines section is unavailable because Nayax has not granted this application permission to read it.'
    );
    expect(host.textContent).not.toContain('No machines available yet');
  });

  it('still synchronizes the latest sales, which the connection is usable for', () => {
    const syncLatest = jest.fn(() => of(SYNC_RAN));
    const component = createComponent({ summary$: of(PERMISSION_NOT_GRANTED_SUMMARY), syncLatest });

    component.ngOnInit();

    expect(syncLatest).toHaveBeenCalledTimes(1);
  });
});

describe('Dashboard failed requests are never an empty state (issue #727)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('reports a failed reorder-alert load as an error, never as "Everything looks good"', async () => {
    const host = await renderDashboard({
      alerts$: throwError(() => new HttpErrorResponse({ status: 500 })) as Observable<ReorderAlertListing>
    });

    expect(host.textContent).not.toContain('No reorder alerts. Everything looks good!');
    expect(host.textContent).toContain('The reorder-alert list could not be loaded. Reload the page to try again.');
  });

  it('reports a failed Sites load as an error, never as "No sites available yet"', async () => {
    const host = await renderDashboard({
      sites$: throwError(() => new HttpErrorResponse({ status: 500 })) as Observable<Site[]>
    });

    expect(host.textContent).not.toContain('No sites available yet');
    expect(host.textContent).toContain('The Sites section could not be loaded. Reload the page to try again.');
  });

  it('reports a failed Machines load as an error, never as "No machines available yet"', async () => {
    const host = await renderDashboard({
      machines$: throwError(() => new HttpErrorResponse({ status: 500 })) as Observable<Machine[]>
    });

    expect(host.textContent).not.toContain('No machines available yet');
    expect(host.textContent).toContain('The Machines section could not be loaded. Reload the page to try again.');
  });

  it('still distinguishes a genuinely empty result from a failed one', async () => {
    const host = await renderDashboard({ alerts$: of(machineAwareAlerts([])), sites$: of([]), machines$: of([]) });

    expect(host.textContent).toContain('No reorder alerts. Everything looks good!');
    expect(host.textContent).toContain('No sites available yet');
    expect(host.textContent).toContain('No machines available yet');
  });

  /**
   * A summary that failed says nothing about the provider, so the page keeps today's behaviour
   * rather than hiding a connected business's live sections on a transient failure.
   */
  it('keeps every section and still synchronizes when the summary itself failed', async () => {
    const syncLatest = jest.fn(() => of(SYNC_RAN));
    const component = createComponent({
      summary$: throwError(() => new Error('network error')) as Observable<DashboardSummary>,
      syncLatest
    });
    component.ngOnInit();

    expect(syncLatest).toHaveBeenCalledTimes(1);

    const host = await renderDashboard({
      summary$: throwError(() => new Error('network error')) as Observable<DashboardSummary>
    });
    expect(sectionTitles(host)).toEqual(['📍 Sites', '🛠️ Machines', '⚠️ Reorder Alerts']);
    expect(host.textContent).toContain('The Dashboard summary could not be loaded');
  });

  it('shows the Sites and Machines sections as loading, not as empty, while the summary is pending', async () => {
    const host = await renderDashboard({ summary$: new Subject<DashboardSummary>() });

    expect(host.textContent).not.toContain('No sites available yet');
    expect(host.textContent).not.toContain('No machines available yet');
  });
});

describe('Dashboard in a support session keeps its existing behaviour (issue #727)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('keeps every card and section and the support-view sentence', async () => {
    const host = await renderDashboard({ summary$: of(SUPPORT_VIEW_SUMMARY), sessionActive: of(true) });

    expect(cardLabels(host)).toEqual(['Sales this week', 'Needs refill', 'Needs ordering', 'Inventory']);
    expect(sectionTitles(host)).toEqual(['📍 Sites', '🛠️ Machines', '⚠️ Reorder Alerts']);
    expect(host.textContent).toContain('Nayax is not available in support view');
  });

  it('still skips the sales sync as a session skip, not as a missing provider', () => {
    const syncLatest = jest.fn(() => of(SYNC_RAN));
    const component = createComponent({
      summary$: of(SUPPORT_VIEW_SUMMARY),
      sessionActive: of(true),
      syncLatest
    });

    component.ngOnInit();

    expect(syncLatest).not.toHaveBeenCalled();
    expect(component.isSalesSyncSkippedInSupportView).toBe(true);
  });
});

describe('Dashboard with a usable provider is unchanged (issue #727)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows all four cards, all three sections and the machine-need column', async () => {
    const host = await renderDashboard({ alerts$: of(machineAwareAlerts([product()])) });

    expect(cardLabels(host)).toEqual(['Sales this week', 'Needs refill', 'Needs ordering', 'Inventory']);
    expect(sectionTitles(host)).toEqual(['📍 Sites', '🛠️ Machines', '⚠️ Reorder Alerts']);

    const headers = Array.from(host.querySelectorAll('th')).map((th) => th.textContent?.trim());
    expect(headers).toContain('Machine need');
    // "Stock after machine need" was removed by issue #789; the machine need itself is unchanged.
    expect(headers).not.toContain('Stock after machine need');
    expect(host.textContent).not.toContain('Storage stock only');

    // Reorder Alerts carries no Supplier column since issue #787 (A1 of #786): a product has no
    // supplier, so a column of em-dashes would say nothing. Every other column is unchanged.
    expect(headers).not.toContain('Supplier');
  });

  it('synchronizes the latest sales once, then loads Machines and Sites', () => {
    const calls: string[] = [];
    const component = new DashboardComponent(
      { getLowStock: () => of(machineAwareAlerts([])) } as unknown as ProductService,
      {
        getAll: () => {
          calls.push('machines');
          return of([MACHINE]);
        }
      } as unknown as MachineService,
      {
        getAll: () => {
          calls.push('sites');
          return of([SITE]);
        }
      } as unknown as SiteService,
      {
        syncLatest: () => {
          calls.push('sync');
          return of(SYNC_RAN);
        }
      } as unknown as NayaxSalesSyncService,
      { getSummary: () => of(CONNECTED_SUMMARY) } as unknown as DashboardService,
      { getSummaries: () => of([]) } as unknown as ProductPurchaseCostService,
      { active$: of(false) } as unknown as SupportSessionService,
      { allows: () => of(true) } as unknown as AccessService
    );

    component.ngOnInit();

    expect(calls).toEqual(['sync', 'machines', 'sites']);
  });
});
