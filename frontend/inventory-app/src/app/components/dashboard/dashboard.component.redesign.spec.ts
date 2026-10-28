import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import {
  DashboardSummary,
  Machine,
  MachineRestockSyncReminder,
  MachineRestockSyncReminderResponse,
  ReorderAlertListing,
  Site
} from '../../models/models';
import { BusinessSettingsService } from '../../services/business-settings.service';
import { DashboardService } from '../../services/dashboard.service';
import { MachineService } from '../../services/machine.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { ProductService } from '../../services/product.service';
import { SiteService } from '../../services/site.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { DashboardComponent } from './dashboard.component';

/**
 * The Dashboard in the owner-approved Restockroo design (issue #972): the Needs attention panel,
 * which lists only the alerts that actually apply and counts them, and the narrow-screen site and
 * machine cards, which carry the same figures and every period the desktop table shows.
 */

const SUMMARY: DashboardSummary = {
  asOfUtc: '2026-10-28T01:00:00Z',
  businessDate: '2026-10-28T00:00:00Z',
  needsRefill: {
    machinesNeedingRefill: 3,
    machinesWithEmptySelections: 2,
    machinesWithLowSelections: 3,
    emptySelectionCount: 4,
    lowSelectionCount: 23,
    machinesEvaluated: 3,
    selectionsEvaluated: 90
  },
  needsOrdering: { productsNeedingOrdering: 33, productsEvaluated: 71, mode: 'MachineAware' },
  inventory: {
    inventoryValueAtCost: 2155.49,
    isInventoryValueComplete: true,
    productsWithUnknownCost: 0,
    productCount: 71,
    unitsInStorage: 596
  }
};

const QUIET_SUMMARY: DashboardSummary = {
  ...SUMMARY,
  needsRefill: { ...SUMMARY.needsRefill, machinesNeedingRefill: 0, emptySelectionCount: 0, lowSelectionCount: 0 },
  needsOrdering: { productsNeedingOrdering: 0, productsEvaluated: 71, mode: 'MachineAware' }
};

const MACHINE: Machine = {
  machineID: 1,
  localMachineId: 501,
  machineName: 'Avenue Hotel VM',
  machineNumber: 'A1',
  todayGrossRevenue: 57.6,
  currentWeekGrossRevenue: 123.9,
  lastWeekGrossRevenue: 210,
  twoWeeksAgoGrossRevenue: 190,
  todayDirectProfit: 28.56,
  twoWeeksAgoDirectProfit: 80,
  lastWeekDirectProfit: 92.57,
  currentWeekDirectProfit: 62.66,
  previousComparableWeekGrossRevenue: 75.9,
  previousComparableWeekDirectProfit: 30,
  monthToDateGrossRevenue: 400,
  monthToDateDirectProfit: 180,
  profitabilityStatus: null
};

const SITE: Site = {
  siteId: 7,
  siteName: 'Harbour Point Plaza',
  machineCount: 1,
  totalStockPercentage: 80,
  lowProductCount: 8,
  emptyProductCount: 1,
  todayRevenue: 57.6,
  currentWeekRevenue: 123.9,
  previousComparableWeekRevenue: 75.9
};

function reminder(): MachineRestockSyncReminder {
  return {
    applicable: true,
    asOfUtc: '2026-10-28T00:00:00Z',
    businessDate: '2026-10-28T00:00:00',
    overdueAfterDays: 3,
    defaultFromBusinessDate: '2026-10-25T00:00:00',
    pendingLookbackDays: 14,
    pendingFromBusinessDate: '2026-10-14T00:00:00',
    syncAvailability: 'Confirmed',
    machines: [
      {
        localMachineId: 501,
        machineName: 'Avenue Hotel VM',
        lastSuccessfulSyncAtUtc: '2026-10-20T08:00:00Z',
        lastSuccessfulSyncBusinessDate: '2026-10-20T00:00:00',
        daysSinceLastSuccessfulSync: 8,
        isFetchOverdue: true,
        knownPendingEventCount: 2,
        oldestKnownPendingEventAtUtc: '2026-10-16T08:00:00Z',
        oldestKnownPendingEventBusinessDate: '2026-10-16T00:00:00',
        hasPendingWorkBeforeDefaultFrom: true
      }
    ]
  };
}

const NOT_APPLICABLE: MachineRestockSyncReminderResponse = { applicable: false } as MachineRestockSyncReminderResponse;

interface Options {
  summary?: DashboardSummary;
  reminder?: Observable<MachineRestockSyncReminderResponse>;
  canViewFinancials?: boolean;
  machines?: Machine[];
  sites?: Site[];
}

async function render(options: Options = {}): Promise<{ fixture: ComponentFixture<DashboardComponent>; host: HTMLElement }> {
  const listing: ReorderAlertListing = { products: [], mode: 'MachineAware', machineNeedUnavailableReason: null };
  const financials = options.canViewFinancials ?? true;
  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock: () => of(listing) } },
      {
        provide: MachineService,
        useValue: {
          getAll: () => of(options.machines ?? [MACHINE]),
          getRestockSyncReminder: () => options.reminder ?? of(NOT_APPLICABLE)
        }
      },
      { provide: SiteService, useValue: { getAll: () => of(options.sites ?? [SITE]) } },
      { provide: NayaxSalesSyncService, useValue: { syncLatest: () => of({ wasRecentlySynced: false, lastSyncedAtUtc: null }) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      { provide: DashboardService, useValue: { getSummary: () => of(options.summary ?? SUMMARY) } },
      { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
      {
        provide: AccessService,
        useValue: { allows: (capability: string) => of(capability === 'Dashboard.ViewFinancials' ? financials : true) }
      },
      { provide: BusinessSettingsService, useValue: { get: () => of(null) } },
      { provide: BusinessTimeZoneService, useValue: { timeZoneId: 'Australia/Sydney' } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(DashboardComponent);
  fixture.detectChanges();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function panel(host: HTMLElement): HTMLElement {
  return host.querySelector('[data-testid="needs-attention"]') as HTMLElement;
}

function alertIds(host: HTMLElement): string[] {
  return Array.from(panel(host).querySelectorAll('[data-testid^="attention-"]'))
    .map((element) => element.getAttribute('data-testid') ?? '')
    .filter((id) => !id.startsWith('attention-open') && !id.startsWith('attention-view'));
}

describe('Dashboard Needs attention (issue #972)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('lists empty selections, machines needing refill and products needing ordering from the summary', async () => {
    const { host } = await render();

    expect(panel(host).hidden).toBe(false);
    expect(alertIds(host)).toEqual(['attention-empty-selections', 'attention-machines-need-refill', 'attention-products-need-ordering']);
    expect(panel(host).textContent).toContain('4 empty selections');
    expect(panel(host).textContent).toContain('3 machines need refill');
    expect(panel(host).textContent).toContain('33 products need ordering');
    expect(host.querySelector('[data-testid="needs-attention-count"]')?.textContent?.trim()).toBe('3 items');
  });

  it('puts the existing restock-sync reminder inside the panel and counts it', async () => {
    const { host } = await render({ reminder: of(reminder()) });
    const tile = panel(host).querySelector('[data-testid="restock-sync-reminder"]');

    expect(tile?.textContent).toContain('Restock sync pending');
    expect(tile?.textContent).toContain("Recorded refills haven't been synced yet.");
    expect(host.querySelector('[data-testid="needs-attention-count"]')?.textContent?.trim()).toBe('4 items');
    // Opening Sync Restock still starts at the oldest pending date (PR #949), from the machine's page.
    const link = tile?.querySelector<HTMLAnchorElement>('[data-testid="restock-sync-reminder-link-501"]');
    expect(link?.getAttribute('href')).toBe('/machines/501?restockFrom=2026-10-16');
    expect(link?.textContent).toContain('Open Sync Restock');
    expect(host.querySelectorAll('[data-testid="restock-sync-reminder"]')).toHaveLength(1);
  });

  it('counts only the reminder when it is the one alert that applies', async () => {
    const { host } = await render({ summary: QUIET_SUMMARY, reminder: of(reminder()) });

    expect(panel(host).hidden).toBe(false);
    expect(alertIds(host)).toEqual([]);
    expect(host.querySelector('[data-testid="needs-attention-count"]')?.textContent?.trim()).toBe('1 item');
  });

  it('says out loud that the reminder failed, inside the panel, rather than hiding it', async () => {
    const { host } = await render({ summary: QUIET_SUMMARY, reminder: throwError(() => new Error('down')) });

    expect(panel(host).hidden).toBe(false);
    expect(panel(host).querySelector('[data-testid="restock-sync-reminder-unavailable"]')).not.toBeNull();
  });

  it('hides the panel while nothing applies', async () => {
    const { host } = await render({ summary: QUIET_SUMMARY });

    expect(panel(host).hidden).toBe(true);
  });

  it('raises no alert from a withheld section, whose own card says it is unavailable', async () => {
    const { host } = await render({
      summary: {
        ...SUMMARY,
        needsRefill: { available: false, reason: 'SupportView' },
        needsOrdering: { available: false, reason: 'SupportView' }
      } as DashboardSummary
    });

    expect(alertIds(host)).toEqual([]);
    expect(panel(host).hidden).toBe(true);
  });

  it('offers the existing Pick List and Needs Ordering destinations, and no new action', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="attention-open-pick-list"]')?.getAttribute('href')).toBe('/pick-list');
    expect(host.querySelector('[data-testid="attention-view-needs-ordering"]')?.getAttribute('href')).toBe('/products/needs-ordering');
    expect(panel(host).querySelectorAll('button')).toHaveLength(0);
  });
});

describe('Dashboard narrow-screen site and machine cards (issue #972)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('gives each site a card with the same figures and comparison as its table row', async () => {
    const { host } = await render();
    const card = host.querySelector('[data-testid="site-cards"] li');

    expect(card?.textContent).toContain('Harbour Point Plaza');
    expect(card?.textContent).toContain('80% stock');
    expect(card?.textContent).toContain('8 low · 1 empty');
    expect(card?.textContent).toContain('$123.90');
    expect(card?.textContent).toContain('↑ 63.2% vs same time last week');
  });

  it('switches the machine cards and the fleet total between every period the table shows', async () => {
    const { fixture, host } = await render();
    const cards = () => host.querySelector('[data-testid="machine-cards"]') as HTMLElement;
    const periodButton = (label: string) =>
      Array.from(cards().querySelectorAll<HTMLButtonElement>('[data-testid="machine-period"] button')).find(
        (button) => button.textContent?.trim() === label
      ) as HTMLButtonElement;

    expect(periodButton('This week').getAttribute('aria-pressed')).toBe('true');
    expect(cards().textContent).toContain('$123.90 sales');
    expect(cards().textContent).toContain('vs same time last week');

    for (const [label, sales] of [
      ['Today', '$57.60 sales'],
      ['Last week', '$210.00 sales'],
      ['Month to date', '$400.00 sales']
    ]) {
      periodButton(label).click();
      fixture.detectChanges();
      expect(periodButton(label).getAttribute('aria-pressed')).toBe('true');
      expect(cards().textContent).toContain(sales);
      expect(host.querySelector('[data-testid="machine-totals-card"]')?.textContent).toContain(sales);
      expect(cards().textContent).not.toContain('vs same time last week');
    }
  });

  it('shows an Operator no period control, sales, profit or total', async () => {
    const { host } = await render({ canViewFinancials: false });
    const cards = host.querySelector('[data-testid="machine-cards"]') as HTMLElement;

    expect(cards.querySelector('[data-testid="machine-period"]')).toBeNull();
    expect(cards.textContent).not.toContain('$');
    expect(host.querySelector('[data-testid="machine-totals-card"]')).toBeNull();
    expect(host.querySelector('[data-testid="site-cards"]')?.textContent).not.toContain('$');
  });

  it('never presents an unknown machine figure as zero, in a card or in the total', async () => {
    const { host } = await render({
      machines: [MACHINE, { ...MACHINE, machineID: 2, localMachineId: 502, currentWeekGrossRevenue: null, currentWeekDirectProfit: null }]
    });
    const cards = host.querySelector('[data-testid="machine-cards"]') as HTMLElement;

    expect(cards.textContent).toContain('Unavailable');
    expect(host.querySelector('[data-testid="machine-totals-card"]')?.textContent).toContain('Unavailable');
    expect(cards.textContent).not.toContain('$0.00');
  });
});
