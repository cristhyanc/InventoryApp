import { Observable, of, throwError } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { InventoryValuationSummary, Machine, Site } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';

function createComponent(
  summary$: Observable<InventoryValuationSummary>,
  overrides: {
    syncLatest?: () => Observable<void>;
    getAllMachines?: () => Observable<Machine[]>;
    getAllSites?: () => Observable<Site[]>;
  } = {}
): DashboardComponent {
  const productService = {
    getAll: () => of([]),
    getLowStock: () => of([]),
    getInventoryValuationSummary: () => summary$
  } as unknown as ProductService;
  const machineService = { getAll: overrides.getAllMachines ?? (() => of([])) } as unknown as MachineService;
  const siteService = { getAll: overrides.getAllSites ?? (() => of([])) } as unknown as SiteService;
  const nayaxSalesSyncService = {
    syncLatest: overrides.syncLatest ?? (() => of(undefined))
  } as unknown as NayaxSalesSyncService;

  return new DashboardComponent(productService, machineService, siteService, nayaxSalesSyncService);
}

describe('DashboardComponent inventory value tile', () => {
  it('shows the authoritative cost-basis total when costing is complete', () => {
    const summary: InventoryValuationSummary = {
      totalInventoryValue: 123.45,
      isComplete: true,
      productsWithUnknownCost: 0,
      totalProducts: 5
    };
    const component = createComponent(of(summary));
    component.ngOnInit();

    expect(component.inventoryValueDisplay).toBe('$123.45');
    expect(component.inventoryValueHelpText).toBe('Business-owned inventory at cost');
  });

  it('shows a known zero total distinct from unavailable', () => {
    const summary: InventoryValuationSummary = {
      totalInventoryValue: 0,
      isComplete: true,
      productsWithUnknownCost: 0,
      totalProducts: 0
    };
    const component = createComponent(of(summary));
    component.ngOnInit();

    expect(component.inventoryValueDisplay).toBe('$0.00');
  });

  it('shows unavailable, not $0.00, when any product has unknown cost', () => {
    const summary: InventoryValuationSummary = {
      totalInventoryValue: null,
      isComplete: false,
      productsWithUnknownCost: 2,
      totalProducts: 5
    };
    const component = createComponent(of(summary));
    component.ngOnInit();

    expect(component.inventoryValueDisplay).toBe('Unavailable');
    expect(component.inventoryValueHelpText).toBe('2 of 5 products missing cost data');
  });

  it('shows unavailable when the backend request fails', () => {
    const component = createComponent(throwError(() => new Error('network error')) as Observable<InventoryValuationSummary>);
    component.ngOnInit();

    expect(component.inventoryValueDisplay).toBe('Unavailable');
  });
});

const SUMMARY: InventoryValuationSummary = {
  totalInventoryValue: 0,
  isComplete: true,
  productsWithUnknownCost: 0,
  totalProducts: 0
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

describe('DashboardComponent coordinated sales refresh', () => {
  it('synchronizes latest sales before loading Sites and Machines', () => {
    const calls: string[] = [];
    const component = createComponent(of(SUMMARY), {
      syncLatest: () => {
        calls.push('sync');
        return of(undefined);
      },
      getAllMachines: () => {
        calls.push('machines');
        return of([MACHINE]);
      },
      getAllSites: () => {
        calls.push('sites');
        return of([SITE]);
      }
    });

    component.ngOnInit();

    expect(calls).toEqual(['sync', 'machines', 'sites']);
    expect(component.machines).toEqual([MACHINE]);
    expect(component.sites).toEqual([SITE]);
    expect(component.isSalesSyncFailed).toBe(false);
  });

  it('does not import sales independently for Sites and Machines - one sync call for both', () => {
    let syncCalls = 0;
    const component = createComponent(of(SUMMARY), {
      syncLatest: () => {
        syncCalls++;
        return of(undefined);
      }
    });

    component.ngOnInit();

    expect(syncCalls).toBe(1);
  });

  it('surfaces the sync failure and still loads existing persisted Sites/Machines, never fabricating zero sales', () => {
    const component = createComponent(of(SUMMARY), {
      syncLatest: () => throwError(() => new Error('Nayax unavailable')),
      getAllMachines: () => of([MACHINE]),
      getAllSites: () => of([SITE])
    });

    component.ngOnInit();

    expect(component.isSalesSyncFailed).toBe(true);
    expect(component.machines).toEqual([MACHINE]);
    expect(component.sites).toEqual([SITE]);
  });
});
