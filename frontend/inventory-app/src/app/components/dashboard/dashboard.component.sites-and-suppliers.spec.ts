import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import {
  DashboardSummary,
  Machine,
  MachineRestockSyncReminderResponse,
  Product,
  ProductPriceTrackingStatus,
  ReorderAlertListing,
  Site
} from '../../models/models';
import { CheapestSupplierSummary, SupplierPriceBasis, SupplierStockStatus } from '../../models/cheapest-supplier';
import { BusinessSettingsService } from '../../services/business-settings.service';
import { CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { DashboardService } from '../../services/dashboard.service';
import { MachineService } from '../../services/machine.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { ProductService } from '../../services/product.service';
import { SiteService } from '../../services/site.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  CHEAPEST_SUPPLIERS_FAILED_MESSAGE,
  CHEAPEST_SUPPLIERS_SUPPORT_SESSION_MESSAGE
} from '../shared/cheapest-supplier/cheapest-suppliers.component';
import { DashboardComponent, OTHER_MACHINES_LABEL, groupMachinesBySite } from './dashboard.component';

/**
 * The two Dashboard follow-ups to issue #972: machines listed under their sites (issue #980), and
 * the observed Cheapest supplier column in Reorder priorities (issue #981), which reuses issue
 * #930's shared read and cell unchanged. The read itself, its batching and the cell's popup have
 * their own specs; this file is about the dashboard.
 */

const SUMMARY: DashboardSummary = {
  asOfUtc: '2026-10-28T01:00:00Z',
  businessDate: '2026-10-28T00:00:00Z',
  needsRefill: {
    machinesNeedingRefill: 0,
    machinesWithEmptySelections: 0,
    machinesWithLowSelections: 0,
    emptySelectionCount: 0,
    lowSelectionCount: 0,
    machinesEvaluated: 3,
    selectionsEvaluated: 90
  },
  needsOrdering: { productsNeedingOrdering: 7, productsEvaluated: 71, mode: 'MachineAware' },
  inventory: {
    inventoryValueAtCost: 2155.49,
    isInventoryValueComplete: true,
    productsWithUnknownCost: 0,
    productCount: 71,
    unitsInStorage: 596
  }
};

function machine(machineID: number, machineName: string, siteId: number | null): Machine {
  return {
    machineID,
    localMachineId: 500 + machineID,
    machineName,
    machineNumber: `M${machineID}`,
    todayGrossRevenue: 10,
    currentWeekGrossRevenue: 20,
    lastWeekGrossRevenue: 30,
    twoWeeksAgoGrossRevenue: 30,
    todayDirectProfit: 4,
    twoWeeksAgoDirectProfit: 12,
    lastWeekDirectProfit: 12,
    currentWeekDirectProfit: 8,
    previousComparableWeekGrossRevenue: 15,
    previousComparableWeekDirectProfit: 6,
    monthToDateGrossRevenue: 60,
    monthToDateDirectProfit: 24,
    profitabilityStatus: null,
    siteId
  };
}

function site(siteId: number, siteName: string): Site {
  return {
    siteId,
    siteName,
    machineCount: 1,
    totalStockPercentage: 80,
    lowProductCount: 1,
    emptyProductCount: 0,
    todayRevenue: 10,
    currentWeekRevenue: 20,
    previousComparableWeekRevenue: 15
  };
}

const HARBOUR = site(1, 'Harbour Point Plaza');
const PAVILLION = site(2, 'Pavillion Shopping Centre');

const FLEET: Machine[] = [
  machine(1, 'Pavillion Left', 2),
  machine(2, 'Avenue Hotel VM', 1),
  machine(3, 'Warehouse test unit', null),
  machine(4, 'Pavillion Right', 2),
  machine(5, 'Moved machine', 99)
];

function product(id: number, name: string): Product {
  return {
    id,
    name,
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

const REORDER: Product[] = [1, 2, 3, 4, 5, 6, 7].map((id) => product(id, `Product ${id}`));

function cheapest(productIds: readonly number[]): CheapestSupplierSummary {
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
          lastCheckedAtUtc: '2026-10-28T04:00:00Z',
          productUrl: 'https://example.test/jb-metro/product',
          ineligibility: null,
          isCheapest: true
        }
      ]
    }))
  };
}

interface Options {
  machines?: Machine[];
  sites?: Observable<Site[]>;
  getSummary?: jest.Mock;
  supportSession?: boolean;
  canViewFinancials?: boolean;
}

async function render(options: Options = {}): Promise<{ fixture: ComponentFixture<DashboardComponent>; host: HTMLElement }> {
  const listing: ReorderAlertListing = { products: REORDER, mode: 'MachineAware', machineNeedUnavailableReason: null };
  const financials = options.canViewFinancials ?? true;
  const getSummary = options.getSummary ?? jest.fn((ids: readonly number[]) => of(cheapest(ids)));
  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock: () => of(listing) } },
      {
        provide: MachineService,
        useValue: {
          getAll: () => of(options.machines ?? FLEET),
          getRestockSyncReminder: () => of({ applicable: false } as MachineRestockSyncReminderResponse)
        }
      },
      { provide: SiteService, useValue: { getAll: () => options.sites ?? of([HARBOUR, PAVILLION]) } },
      { provide: NayaxSalesSyncService, useValue: { syncLatest: () => of({ wasRecentlySynced: false, lastSyncedAtUtc: null }) } },
      { provide: SupportSessionService, useValue: { active$: of(options.supportSession ?? false) } },
      { provide: DashboardService, useValue: { getSummary: () => of(SUMMARY) } },
      { provide: ProductPurchaseCostService, useValue: { getSummaries: () => of([]) } },
      { provide: CheapestSupplierService, useValue: { getSummary } },
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

function machineSection(host: HTMLElement): HTMLElement {
  return host.querySelector('section[aria-labelledby="machine-performance-title"]') as HTMLElement;
}

function reorderSection(host: HTMLElement): HTMLElement {
  return host.querySelector('section[aria-labelledby="reorder-priorities-title"]') as HTMLElement;
}

/** The desktop table's groups, as [heading, machine names] in rendered order. */
function tableGroups(host: HTMLElement): [string | null, string[]][] {
  return Array.from(machineSection(host).querySelectorAll<HTMLElement>('tbody[data-testid^="machine-group-"]')).map((body) => [
    body.querySelector('th[scope="rowgroup"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? null,
    Array.from(body.querySelectorAll('a.table-row-anchor')).map((anchor) => anchor.textContent?.trim() ?? '')
  ]);
}

describe('groupMachinesBySite (issue #980)', () => {
  it('lists each site with its own machines, in the site order, then the rest under Other machines', () => {
    const groups = groupMachinesBySite(FLEET, [HARBOUR, PAVILLION]);

    expect(groups.map((group) => [group.label, group.machines.map((m) => m.machineID)])).toEqual([
      ['Harbour Point Plaza', [2]],
      ['Pavillion Shopping Centre', [1, 4]],
      [OTHER_MACHINES_LABEL, [3, 5]]
    ]);
  });

  it('places every machine exactly once', () => {
    const placed = groupMachinesBySite(FLEET, [HARBOUR, PAVILLION]).flatMap((group) => group.machines);

    expect(placed).toHaveLength(FLEET.length);
    expect(new Set(placed).size).toBe(FLEET.length);
  });

  it('gives a site with no listed machine no group', () => {
    const groups = groupMachinesBySite([machine(2, 'Avenue Hotel VM', 1)], [HARBOUR, PAVILLION]);

    expect(groups.map((group) => group.label)).toEqual(['Harbour Point Plaza']);
  });

  it('leaves the list unheaded when no machine is at a listed site', () => {
    const groups = groupMachinesBySite(FLEET, []);

    expect(groups).toHaveLength(1);
    expect(groups[0].label).toBeNull();
    expect(groups[0].machines).toHaveLength(FLEET.length);
  });
});

describe('Dashboard machines under their sites (issue #980)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('heads each site row group with the site and its machine count', async () => {
    const { host } = await render();

    expect(tableGroups(host)).toEqual([
      ['Harbour Point Plaza · 1 machine', ['Avenue Hotel VM']],
      ['Pavillion Shopping Centre · 2 machines', ['Pavillion Left', 'Pavillion Right']],
      ['Other machines · 2 machines', ['Warehouse test unit', 'Moved machine']]
    ]);
  });

  it('groups the narrow-screen cards the same way', async () => {
    const { host } = await render();
    const headings = Array.from(machineSection(host).querySelectorAll('[data-testid^="machine-card-group-"] h3')).map((h) =>
      h.textContent?.replace(/\s+/g, ' ').trim()
    );

    expect(headings).toEqual([
      'Harbour Point Plaza · 1 machine',
      'Pavillion Shopping Centre · 2 machines',
      'Other machines · 2 machines'
    ]);
  });

  it('keeps the fleet totals over every machine', async () => {
    const { host } = await render();

    expect(machineSection(host).querySelector('tfoot')?.textContent).toContain('Totals (5 machines)');
    expect(machineSection(host).querySelector('tfoot')?.textContent).toContain('$100.00 sales');
  });

  it('shows the machines unheaded when the site list could not be read', async () => {
    const { host } = await render({ sites: throwError(() => new Error('down')) });

    expect(tableGroups(host)).toEqual([
      [null, ['Pavillion Left', 'Avenue Hotel VM', 'Warehouse test unit', 'Pavillion Right', 'Moved machine']]
    ]);
  });
});

describe('Dashboard Reorder priorities Cheapest supplier column (issue #981)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('reads the cheapest supplier once, for the five products shown only', async () => {
    const getSummary = jest.fn((ids: readonly number[]) => of(cheapest(ids)));
    await render({ getSummary });

    expect(getSummary).toHaveBeenCalledTimes(1);
    expect(getSummary).toHaveBeenCalledWith([1, 2, 3, 4, 5]);
  });

  it('shows each product cheapest supplier after the historical purchase columns', async () => {
    const { host } = await render();
    const headers = Array.from(reorderSection(host).querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    const cells = reorderSection(host).querySelectorAll('table [data-testid="cheapest-supplier-cell"]');

    expect(headers.slice(-3)).toEqual(['Last purchase', 'Cheapest purchase', 'Cheapest supplier']);
    expect(cells).toHaveLength(5);
    expect(cells[0].textContent).toContain('JB Metro');
  });

  it('says the supplier prices are observed, apart from the recorded purchase costs', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="supplier-price-note"]')?.textContent).toContain('not costs you have paid');
    expect(host.querySelector('[data-testid="purchase-cost-note"]')?.textContent).toContain('historical costs');
  });

  it('shows the supplier on the narrow-screen cards too', async () => {
    const { host } = await render();
    const lines = reorderSection(host).querySelectorAll('[data-testid="reorder-card-cheapest-supplier"]');

    expect(lines).toHaveLength(5);
    expect(lines[0].textContent).toContain('JB Metro');
  });

  it('reports a failed read with Retry rather than as no supplier, and keeps the rows', async () => {
    const { host } = await render({ getSummary: jest.fn(() => throwError(() => new Error('down'))) });

    expect(reorderSection(host).textContent).toContain(CHEAPEST_SUPPLIERS_FAILED_MESSAGE);
    expect(reorderSection(host).textContent).toContain('Product 1');
    expect(reorderSection(host).querySelector('[data-testid="cheapest-supplier-cell"]')?.textContent).not.toContain('JB Metro');
  });

  it('asks nothing in a support session and says the prices are unavailable there', async () => {
    const getSummary = jest.fn((ids: readonly number[]) => of(cheapest(ids)));
    const { host } = await render({ getSummary, supportSession: true });

    expect(getSummary).not.toHaveBeenCalled();
    expect(reorderSection(host).textContent).toContain(CHEAPEST_SUPPLIERS_SUPPORT_SESSION_MESSAGE);
  });

  it('shows the column without Dashboard.ViewFinancials, which withholds only the purchase costs', async () => {
    const { host } = await render({ canViewFinancials: false });
    const headers = Array.from(reorderSection(host).querySelectorAll('thead th')).map((th) => th.textContent?.trim());

    expect(headers).toContain('Cheapest supplier');
    expect(headers).not.toContain('Last purchase');
  });
});
