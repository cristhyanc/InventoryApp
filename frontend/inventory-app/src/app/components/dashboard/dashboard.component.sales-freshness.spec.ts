import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { DashboardSummary, Machine, ReorderAlertListing, Site } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncResult, NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { DashboardService } from '../../services/dashboard.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { AccessService } from '../../access/access.service';
import { BusinessSettingsService } from '../../services/business-settings.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { SupportSessionService } from '../../support-session/support-session.service';

/**
 * The Dashboard's side of the recent-sync skip (issue #843, part of parent #839).
 *
 * The page calls `POST api/nayax-sales-sync` on arrival, and the server now has three answers to it:
 * the run happened (`204`), the sales were already synchronized moments ago (`200`), or the run limit
 * refused it (`429`). The first two are both "these figures are current" and must read as that; only
 * the third and a real failure carry a notice. The operator can still ask for a fresh read, and the
 * Refresh control is the only thing that forces one.
 */

const SUMMARY: DashboardSummary = {
  asOfUtc: '2026-10-23T09:30:00Z',
  businessDate: '2026-10-23T00:00:00Z',
  needsRefill: {
    machinesNeedingRefill: 0,
    machinesWithEmptySelections: 0,
    machinesWithLowSelections: 0,
    emptySelectionCount: 0,
    lowSelectionCount: 0,
    machinesEvaluated: 3,
    selectionsEvaluated: 12
  },
  needsOrdering: { productsNeedingOrdering: 0, productsEvaluated: 10, mode: 'MachineAware' },
  inventory: {
    inventoryValueAtCost: 100,
    isInventoryValueComplete: true,
    productsWithUnknownCost: 0,
    productCount: 10,
    unitsInStorage: 50
  }
};

/** The business with no usable Nayax connection: its refill card carries the reason instead. */
const NOT_CONNECTED_SUMMARY: DashboardSummary = {
  ...SUMMARY,
  needsRefill: { available: false, reason: 'NayaxNotConnected' }
};

const RAN: NayaxSalesSyncResult = { wasRecentlySynced: false, lastSyncedAtUtc: null };

function recentlySynced(lastSyncedAtUtc: string): NayaxSalesSyncResult {
  return { wasRecentlySynced: true, lastSyncedAtUtc };
}

const MACHINE: Machine = {
  machineID: 1,
  localMachineId: 501,
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

const NO_ALERTS: ReorderAlertListing = { products: [], mode: 'MachineAware', machineNeedUnavailableReason: null };

interface Doubles {
  summary$?: Observable<DashboardSummary>;
  syncLatest?: (force?: boolean) => Observable<NayaxSalesSyncResult>;
  sessionActive?: Observable<boolean>;
}

function createComponent(doubles: Doubles = {}): DashboardComponent {
  return new DashboardComponent(
    { getLowStock: () => of(NO_ALERTS) } as unknown as ProductService,
    { getAll: () => of([MACHINE]) } as unknown as MachineService,
    { getAll: () => of([SITE]) } as unknown as SiteService,
    { syncLatest: doubles.syncLatest ?? (() => of(RAN)) } as unknown as NayaxSalesSyncService,
    { getSummary: () => doubles.summary$ ?? of(SUMMARY) } as unknown as DashboardService,
    { getSummaries: () => of([]) } as unknown as ProductPurchaseCostService,
    { active$: doubles.sessionActive ?? of(false) } as unknown as SupportSessionService,
    { allows: () => of(true) } as unknown as AccessService
  );
}

async function renderDashboard(doubles: Doubles = {}): Promise<HTMLElement> {
  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock: () => of(NO_ALERTS) } },
      { provide: MachineService, useValue: { getAll: () => of([MACHINE]) } },
      { provide: SiteService, useValue: { getAll: () => of([SITE]) } },
      {
        provide: NayaxSalesSyncService,
        useValue: { syncLatest: doubles.syncLatest ?? (() => of(RAN)) }
      },
      { provide: DashboardService, useValue: { getSummary: () => doubles.summary$ ?? of(SUMMARY) } },
      { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
      { provide: AccessService, useValue: { allows: () => of(false) } },
      { provide: SupportSessionService, useValue: { active$: doubles.sessionActive ?? of(false) } },
      // The composed legacy-business-settings prompt is its own component's subject, and stays
      // hidden here, exactly as `dashboard.component.spec.ts` keeps it.
      { provide: BusinessSettingsService, useValue: { get: () => of(null), saved$: of(null) } },
      // The business's own timezone, which is what a displayed clock time is rendered in.
      { provide: BusinessTimeZoneService, useValue: { timeZoneId: 'Australia/Sydney' } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(DashboardComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

/** An instant `minutes` ago, so a rendering test needs no control over the clock. */
function minutesAgo(minutes: number): string {
  return new Date(Date.now() - minutes * 60_000).toISOString();
}

function refreshButton(host: HTMLElement): HTMLButtonElement | null {
  return host.querySelector('[data-testid="refresh-sales"]');
}

describe('DashboardComponent recent sales sync (issue #843)', () => {
  it('reports when the sales were synced from the instant a skipped run carried', () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-10-23T09:05:00Z'));
    try {
      const component = createComponent({
        syncLatest: () => of(recentlySynced('2026-10-23T09:03:00Z'))
      });

      component.ngOnInit();

      expect(component.salesSyncedAtUtc).toBe('2026-10-23T09:03:00Z');
      expect(component.salesSyncedLabel).toBe('Sales synced 2 min ago');
      // Not a failure, and not the run limit's notice either.
      expect(component.isSalesSyncFailed).toBe(false);
      expect(component.isSalesSyncRecentlyRefreshed).toBe(false);
    } finally {
      jest.useRealTimers();
    }
  });

  it('still loads the persisted Sites and Machines on a skipped run', () => {
    const component = createComponent({ syncLatest: () => of(recentlySynced('2026-10-23T09:03:00Z')) });

    component.ngOnInit();

    expect(component.machines).toEqual([MACHINE]);
    expect(component.sites).toEqual([SITE]);
  });

  it('says the sales were just synced when the run actually happened', () => {
    const component = createComponent();

    component.ngOnInit();

    expect(component.salesSyncedAtUtc).not.toBeNull();
    expect(component.salesSyncedLabel).toBe('Sales synced just now');
  });

  it('rounds a whole minute down and never reports a negative age', () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-10-23T09:05:30Z'));
    try {
      const component = createComponent({ syncLatest: () => of(recentlySynced('2026-10-23T09:04:00Z')) });
      component.ngOnInit();
      expect(component.salesSyncedLabel).toBe('Sales synced 1 min ago');

      // A browser clock behind the server's would otherwise read as a future sync.
      const ahead = createComponent({ syncLatest: () => of(recentlySynced('2026-10-23T09:09:00Z')) });
      ahead.ngOnInit();
      expect(ahead.salesSyncedLabel).toBe('Sales synced just now');
    } finally {
      jest.useRealTimers();
    }
  });

  it('knows nothing about the freshness when the sync failed, and keeps the failure notice', () => {
    const component = createComponent({ syncLatest: () => throwError(() => new Error('Nayax unavailable')) });

    component.ngOnInit();

    expect(component.isSalesSyncFailed).toBe(true);
    expect(component.salesSyncedAtUtc).toBeNull();
    expect(component.salesSyncedLabel).toBeNull();
  });

  it("shows the business's run limit exactly as today, and claims no freshness from it (issue #681)", () => {
    const component = createComponent({
      syncLatest: () =>
        throwError(() => new HttpErrorResponse({ status: 429, headers: new HttpHeaders({ 'Retry-After': '90' }) }))
    });

    component.ngOnInit();

    expect(component.isSalesSyncRecentlyRefreshed).toBe(true);
    expect(component.isSalesSyncFailed).toBe(false);
    expect(component.salesSyncedAtUtc).toBeNull();
    expect(component.machines).toEqual([MACHINE]);
  });

  it('forces the run when Refresh is pressed, and only then', () => {
    const syncLatest = jest.fn((_force?: boolean) => of(recentlySynced('2026-10-23T09:03:00Z')));
    const component = createComponent({ syncLatest });

    component.ngOnInit();
    expect(syncLatest).toHaveBeenLastCalledWith(false);

    component.refreshSales();
    expect(syncLatest).toHaveBeenLastCalledWith(true);
    expect(syncLatest).toHaveBeenCalledTimes(2);
  });

  it('reloads Sites and Machines from the forced run', () => {
    const component = createComponent();
    component.ngOnInit();
    component.machines = [];
    component.sites = [];

    component.refreshSales();

    expect(component.machines).toEqual([MACHINE]);
    expect(component.sites).toEqual([SITE]);
  });

  it('offers no Refresh inside a support session, where the call could only be refused', () => {
    const component = createComponent({ sessionActive: of(true) });

    component.ngOnInit();

    expect(component.isSalesRefreshShown).toBe(false);
    expect(component.isSalesSyncSkippedInSupportView).toBe(true);
  });

  it('offers no Refresh for a business with no usable sales provider', () => {
    const component = createComponent({ summary$: of(NOT_CONNECTED_SUMMARY) });

    component.ngOnInit();

    expect(component.isSalesRefreshShown).toBe(false);
  });
});

describe('DashboardComponent sales freshness rendering (issue #843)', () => {
  it('renders the age of the sales and a Refresh control', async () => {
    const host = await renderDashboard({ syncLatest: () => of(recentlySynced(minutesAgo(4))) });

    expect(host.textContent).toContain('Sales synced 4 min ago');
    expect(refreshButton(host)).not.toBeNull();
    // A skip is not a failure and not "not synchronized".
    expect(host.textContent).not.toContain('Latest Nayax sales could not be synchronized.');
  });

  /**
   * The clock time behind the relative label is the business's own wall-clock time (issue #499's
   * `businessDateTime`), so an operator in Sydney reads a Sydney time rather than the browser's
   * accidental zone or UTC.
   */
  it('carries the business wall-clock time of the sync beside the relative label', async () => {
    const host = await renderDashboard({ syncLatest: () => of(recentlySynced('2026-10-23T09:01:00Z')) });

    const label = host.querySelector('[data-testid="sales-synced-at"]');
    // 09:01 UTC is 20:01 on 23/10/2026 in Sydney (AEDT, UTC+11).
    expect(label?.getAttribute('title')).toContain('23/10/2026');
    expect(label?.getAttribute('title')).toContain('08:01 pm');
  });

  it('renders no freshness label at all when the synchronization failed', async () => {
    const host = await renderDashboard({ syncLatest: () => throwError(() => new Error('Nayax unavailable')) });

    expect(host.querySelector('[data-testid="sales-synced-at"]')).toBeNull();
    expect(host.textContent).toContain('Latest Nayax sales could not be synchronized.');
    // The operator can still ask for a fresh read, which is the point of the control.
    expect(refreshButton(host)).not.toBeNull();
  });

  it('forces a run when the rendered Refresh control is pressed', async () => {
    const syncLatest = jest.fn((_force?: boolean) => of(RAN));
    const host = await renderDashboard({ syncLatest });

    refreshButton(host)?.click();

    expect(syncLatest).toHaveBeenLastCalledWith(true);
  });
});
