import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { InventoryValuationSummary, Machine, Product, Site } from '../../models/models';
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

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 1,
    name: 'Cola 375mL',
    sku: 'COLA-375',
    description: null,
    unitPrice: 3,
    averageUnitCost: 1.2,
    costingQuantity: 10,
    inventoryValue: 12,
    machinePrice: 3.5,
    commissionValue: 0,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 10,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 4,
    onOrderQuantity: 0,
    lowStockThreshold: 5,
    restockTo: 30,
    needToOrder: 0,
    mdbCode: null,
    unit: 'unit',
    lastEatBefore1: null,
    lastEatBefore2: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    categoryId: null,
    category: null,
    supplierId: null,
    supplier: { id: 1, name: 'Acme Supplies' },
    isActive: true,
    isLowStock: true,
    isReorderAlert: true,
    ...overrides
  };
}

describe('DashboardComponent reorder alerts - stock after machine need', () => {
  it('is positive when stock exceeds machine need', () => {
    const component = createComponent(of(SUMMARY));
    const p = product({ quantityInStock: 46, machineReplenishmentNeed: 16 });

    expect(component.stockAfterMachineNeed(p)).toBe(30);
    expect(component.stockAfterMachineNeedClass(p)).not.toContain('value-negative');
  });

  it('is zero when stock exactly covers machine need', () => {
    const component = createComponent(of(SUMMARY));
    const p = product({ quantityInStock: 16, machineReplenishmentNeed: 16 });

    expect(component.stockAfterMachineNeed(p)).toBe(0);
    expect(component.stockAfterMachineNeedClass(p)).not.toContain('value-negative');
  });

  it('keeps the signed negative value and flags it with the negative value class when machine need exceeds stock', () => {
    const component = createComponent(of(SUMMARY));
    const p = product({ quantityInStock: 10, machineReplenishmentNeed: 16 });

    expect(component.stockAfterMachineNeed(p)).toBe(-6);
    expect(component.stockAfterMachineNeedClass(p)).toContain('value-negative');
  });
});

describe('DashboardComponent reorder alerts table rendering', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function renderTable(products: Product[]) {
    await TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        provideRouter([]),
        { provide: ProductService, useValue: { getAll: () => of([]), getLowStock: () => of(products), getInventoryValuationSummary: () => of(SUMMARY) } },
        { provide: MachineService, useValue: { getAll: () => of([]) } },
        { provide: SiteService, useValue: { getAll: () => of([]) } },
        { provide: NayaxSalesSyncService, useValue: { syncLatest: () => of(undefined) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(DashboardComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('no longer shows the Adjust Stock action', async () => {
    const host = await renderTable([product()]);

    expect(host.textContent).not.toContain('Adjust Stock');
  });

  it('shows the Stock after machine need column between On order and Need to Order', async () => {
    const host = await renderTable([product()]);

    const headers = Array.from(host.querySelectorAll('th')).map((th) => th.textContent?.trim());
    const onOrderIndex = headers.indexOf('On order');
    const needToOrderIndex = headers.indexOf('Need to Order');
    const stockAfterIndex = headers.indexOf('Stock after machine need');

    expect(stockAfterIndex).toBeGreaterThan(onOrderIndex);
    expect(stockAfterIndex).toBeLessThan(needToOrderIndex);
  });

  it('renders a negative Stock after machine need with the negative value class', async () => {
    const host = await renderTable([product({ quantityInStock: 10, machineReplenishmentNeed: 16 })]);

    const cell = Array.from(host.querySelectorAll('td')).find((td) => td.textContent?.trim() === '-6');
    expect(cell).toBeDefined();
    expect(cell?.className).toContain('value-negative');
  });
});

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
