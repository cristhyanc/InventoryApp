import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { DashboardSalesThisWeek, DashboardSummary, Machine, Product, Site } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { DashboardService } from '../../services/dashboard.service';

/**
 * Declared on its own rather than inline, because `DashboardSummary.salesThisWeek` is optional
 * since issue #527 - a role without `Dashboard.ViewFinancials` is not given it - and spreading an
 * optional property would make every field of a derived fixture optional too.
 */
const SALES_THIS_WEEK: DashboardSalesThisWeek = {
  sales: 300,
  transactionCount: 3,
  period: { startUtc: '2026-10-05T14:00:00Z', endUtc: '2026-10-08T09:30:00Z', firstBusinessDate: '2026-10-05T00:00:00Z', lastBusinessDate: '2026-10-08T00:00:00Z' },
  comparisonPeriod: { startUtc: '2026-09-28T14:00:00Z', endUtc: '2026-10-01T09:30:00Z', firstBusinessDate: '2026-09-28T00:00:00Z', lastBusinessDate: '2026-10-01T00:00:00Z' },
  isComparisonAvailable: true,
  comparisonSales: 240,
  comparisonTransactionCount: 2,
  changeAmount: 60,
  changePercent: 25,
  comparisonNote: null
};

const SUMMARY: DashboardSummary = {
  asOfUtc: '2026-10-08T09:30:00Z',
  businessDate: '2026-10-08T00:00:00Z',
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
  needsOrdering: {
    productsNeedingOrdering: 4,
    productsEvaluated: 50
  },
  inventory: {
    inventoryValueAtCost: 1234.56,
    isInventoryValueComplete: true,
    productsWithUnknownCost: 0,
    productCount: 50,
    unitsInStorage: 320
  }
};

function createComponent(
  summary$: Observable<DashboardSummary>,
  overrides: {
    syncLatest?: () => Observable<void>;
    getAllMachines?: () => Observable<Machine[]>;
    getAllSites?: () => Observable<Site[]>;
    getLowStock?: () => Observable<Product[]>;
  } = {}
): DashboardComponent {
  const productService = {
    getLowStock: overrides.getLowStock ?? (() => of([]))
  } as unknown as ProductService;
  const machineService = { getAll: overrides.getAllMachines ?? (() => of([])) } as unknown as MachineService;
  const siteService = { getAll: overrides.getAllSites ?? (() => of([])) } as unknown as SiteService;
  const nayaxSalesSyncService = {
    syncLatest: overrides.syncLatest ?? (() => of(undefined))
  } as unknown as NayaxSalesSyncService;
  const dashboardService = { getSummary: () => summary$ } as unknown as DashboardService;

  return new DashboardComponent(productService, machineService, siteService, nayaxSalesSyncService, dashboardService);
}

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

describe('DashboardComponent sales this week comparison', () => {
  it('shows the backend percentage and amount unchanged, with no frontend recalculation', () => {
    const component = createComponent(of(SUMMARY));
    component.ngOnInit();

    expect(component.salesComparisonText(SALES_THIS_WEEK)).toBe('↑ 25.0% vs same time last week');
    expect(component.salesComparisonClass(SALES_THIS_WEEK)).toBe('value-positive');
  });

  it('shows a negative comparison distinctly', () => {
    const week = { ...SALES_THIS_WEEK, changePercent: -10 };
    const component = createComponent(of(SUMMARY));

    expect(component.salesComparisonText(week)).toBe('↓ 10.0% vs same time last week');
    expect(component.salesComparisonClass(week)).toBe('value-negative');
  });

  it('shows the backend note instead of a percentage when the comparison is unavailable', () => {
    const week = {
      ...SALES_THIS_WEEK,
      isComparisonAvailable: false,
      comparisonSales: null,
      changeAmount: null,
      changePercent: null,
      comparisonNote: 'Recorded completed sales do not cover the comparable period last week, so a week-on-week comparison is unavailable.'
    };
    const component = createComponent(of(SUMMARY));

    expect(component.salesComparisonText(week)).toBe(
      'Recorded completed sales do not cover the comparable period last week, so a week-on-week comparison is unavailable.'
    );
    expect(component.salesComparisonClass(week)).toBe('value-muted');
  });

  it('shows the backend note instead of a misleading percentage for a zero-baseline prior period', () => {
    const week = {
      ...SALES_THIS_WEEK,
      isComparisonAvailable: true,
      comparisonSales: 0,
      changeAmount: 300,
      changePercent: null,
      comparisonNote: 'There were no completed sales in the comparable period last week, so a percentage change is undefined.'
    };
    const component = createComponent(of(SUMMARY));

    expect(component.salesComparisonText(week)).toBe(
      'There were no completed sales in the comparable period last week, so a percentage change is undefined.'
    );
    expect(component.salesComparisonClass(week)).toBe('value-muted');
  });
});

describe('DashboardComponent needs refill supporting detail', () => {
  it('reports nothing to evaluate when the fleet has no machines', () => {
    const component = createComponent(of(SUMMARY));
    const refill = { ...SUMMARY.needsRefill, machinesEvaluated: 0, selectionsEvaluated: 0, machinesNeedingRefill: 0 };

    expect(component.refillFooterText(refill)).toBe('No machines to evaluate yet');
    expect(component.refillFooterClass(refill)).toBe('value-muted');
  });

  it('reports every machine adequately stocked as a distinct zero from nothing to evaluate', () => {
    const component = createComponent(of(SUMMARY));
    const refill = { ...SUMMARY.needsRefill, machinesEvaluated: 5, machinesNeedingRefill: 0 };

    expect(component.refillFooterText(refill)).toBe('All 5 machines adequately stocked');
    expect(component.refillFooterClass(refill)).toBe('value-positive');
  });

  it('shows the low/empty selection counts as supporting detail when machines need refilling', () => {
    const component = createComponent(of(SUMMARY));

    expect(component.refillFooterText(SUMMARY.needsRefill)).toBe('2 low · 1 empty selections across 5 machines evaluated');
    expect(component.refillFooterClass(SUMMARY.needsRefill)).toBe('value-muted');
  });
});

describe('DashboardComponent needs ordering supporting detail', () => {
  it('shows the evaluated catalogue size as supporting detail', () => {
    const component = createComponent(of(SUMMARY));

    expect(component.orderingFooterText(SUMMARY.needsOrdering)).toBe('of 50 products in the catalogue');
    expect(component.orderingFooterClass(SUMMARY.needsOrdering)).toBe('value-muted');
  });

  it('reports every product adequately stocked distinctly from zero products', () => {
    const component = createComponent(of(SUMMARY));
    const ordering = { productsNeedingOrdering: 0, productsEvaluated: 50 };

    expect(component.orderingFooterText(ordering)).toBe('All 50 products adequately stocked');
    expect(component.orderingFooterClass(ordering)).toBe('value-positive');
  });

  it('reports an empty catalogue distinctly', () => {
    const component = createComponent(of(SUMMARY));
    const ordering = { productsNeedingOrdering: 0, productsEvaluated: 0 };

    expect(component.orderingFooterText(ordering)).toBe('No products in the catalogue yet');
    expect(component.orderingFooterClass(ordering)).toBe('value-muted');
  });
});

describe('DashboardComponent inventory card', () => {
  it('shows the authoritative cost-basis total and scope detail when costing is complete', () => {
    const component = createComponent(of(SUMMARY));

    expect(component.inventoryValueDisplay(SUMMARY.inventory)).toBe('$1,234.56');
    expect(component.inventoryValueHelpText(SUMMARY.inventory)).toBe('Business-owned inventory at cost');
    expect(component.inventoryScopeText(SUMMARY.inventory)).toBe('50 products · 320 units in storage (excludes machines)');
  });

  it('shows unavailable, not $0.00, when any product has unknown cost', () => {
    const component = createComponent(of(SUMMARY));
    const inventory = { ...SUMMARY.inventory, inventoryValueAtCost: null, isInventoryValueComplete: false, productsWithUnknownCost: 2 };

    expect(component.inventoryValueDisplay(inventory)).toBe('Unavailable');
    expect(component.inventoryValueHelpText(inventory)).toBe('2 of 50 products missing cost data');
  });
});

describe('DashboardComponent dashboard summary loading/failure', () => {
  it('surfaces the summary request failure without fabricating a zero', () => {
    const component = createComponent(throwError(() => new Error('network error')) as Observable<DashboardSummary>);
    component.ngOnInit();

    expect(component.summary).toBeNull();
    expect(component.isSummaryFailed).toBe(true);
    expect(component.isLoadingSummary).toBe(false);
  });

  it('loads the summary once on init', () => {
    const component = createComponent(of(SUMMARY));
    component.ngOnInit();

    expect(component.summary).toEqual(SUMMARY);
    expect(component.isSummaryFailed).toBe(false);
    expect(component.isLoadingSummary).toBe(false);
  });
});

/**
 * The redacted summary a role without `Dashboard.ViewFinancials` receives (issue #527): no
 * `salesThisWeek` object and no `inventory.inventoryValueAtCost`, both **absent** rather than
 * `null` or `0`. The page hides the two money cards instead of rendering a figure it was not
 * given (issue #528).
 */
const REDACTED_SUMMARY: DashboardSummary = {
  asOfUtc: SUMMARY.asOfUtc,
  businessDate: SUMMARY.businessDate,
  needsRefill: SUMMARY.needsRefill,
  needsOrdering: SUMMARY.needsOrdering,
  inventory: {
    isInventoryValueComplete: SUMMARY.inventory.isInventoryValueComplete,
    productsWithUnknownCost: SUMMARY.inventory.productsWithUnknownCost,
    productCount: SUMMARY.inventory.productCount,
    unitsInStorage: SUMMARY.inventory.unitsInStorage
  }
};

describe('DashboardComponent redacted financial figures (issues #527 and #528)', () => {
  it('hides the sales and inventory-value cards when the summary carries neither figure', () => {
    const component = createComponent(of(REDACTED_SUMMARY));
    component.ngOnInit();

    expect(component.isSalesCardShown).toBe(false);
    expect(component.isInventoryCardShown).toBe(false);
    expect(component.salesThisWeek).toBeNull();
  });

  it('keeps both cards for a summary that carries the figures', () => {
    const component = createComponent(of(SUMMARY));
    component.ngOnInit();

    expect(component.isSalesCardShown).toBe(true);
    expect(component.isInventoryCardShown).toBe(true);
    expect(component.salesThisWeek).toEqual(SUMMARY.salesThisWeek);
  });

  /**
   * `null` is already a meaningful answer for `inventoryValueAtCost` - the valuation is incomplete
   * - so it must stay distinguishable from a withheld figure, which is absent. An incomplete
   * valuation keeps its card and reports "Unavailable" exactly as before.
   */
  it('keeps the inventory card for an incomplete valuation, which is null rather than absent', () => {
    const incomplete: DashboardSummary = {
      ...SUMMARY,
      inventory: { ...SUMMARY.inventory, inventoryValueAtCost: null, isInventoryValueComplete: false }
    };
    const component = createComponent(of(incomplete));
    component.ngOnInit();

    expect(component.isInventoryCardShown).toBe(true);
    expect(component.inventoryValueDisplay(incomplete.inventory)).toBe('Unavailable');
  });

  it('keeps both cards while the summary is loading or after it failed, reporting them as unavailable', () => {
    const loading = createComponent(new Subject<DashboardSummary>());
    loading.ngOnInit();
    const failed = createComponent(throwError(() => new Error('network error')) as Observable<DashboardSummary>);
    failed.ngOnInit();

    expect(loading.isSalesCardShown).toBe(true);
    expect(loading.isInventoryCardShown).toBe(true);
    expect(failed.isSalesCardShown).toBe(true);
    expect(failed.isInventoryCardShown).toBe(true);
  });

  it('reads no withheld figure as a zero week or an empty valuation', () => {
    const component = createComponent(of(REDACTED_SUMMARY));
    component.ngOnInit();

    expect(component.summary?.salesThisWeek).toBeUndefined();
    expect(component.summary?.inventory.inventoryValueAtCost).toBeUndefined();
  });
});

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

async function renderDashboard(
  overrides: { products?: Product[]; machines?: Machine[]; summary$?: Observable<DashboardSummary> } = {}
): Promise<HTMLElement> {
  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock: () => of(overrides.products ?? []) } },
      { provide: MachineService, useValue: { getAll: () => of(overrides.machines ?? []) } },
      { provide: SiteService, useValue: { getAll: () => of([]) } },
      { provide: NayaxSalesSyncService, useValue: { syncLatest: () => of(undefined) } },
      { provide: DashboardService, useValue: { getSummary: () => overrides.summary$ ?? of(SUMMARY) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(DashboardComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

/** `routerLink` navigates through `navigateByUrl`, so that is where a followed link shows up. */
function spyOnNavigateByUrl() {
  return jest.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
}

function navigatedUrls(spy: ReturnType<typeof spyOnNavigateByUrl>): string[] {
  const router = TestBed.inject(Router);
  return spy.mock.calls.map(([url]) => (typeof url === 'string' ? url : router.serializeUrl(url)));
}

describe('DashboardComponent template rendering', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('no longer shows the Adjust Stock action', async () => {
    const host = await renderDashboard({ products: [product()] });

    expect(host.textContent).not.toContain('Adjust Stock');
  });

  it('shows the Stock after machine need column between On order and Need to Order', async () => {
    const host = await renderDashboard({ products: [product()] });

    const headers = Array.from(host.querySelectorAll('th')).map((th) => th.textContent?.trim());
    const onOrderIndex = headers.indexOf('On order');
    const needToOrderIndex = headers.indexOf('Need to Order');
    const stockAfterIndex = headers.indexOf('Stock after machine need');

    expect(stockAfterIndex).toBeGreaterThan(onOrderIndex);
    expect(stockAfterIndex).toBeLessThan(needToOrderIndex);
  });

  it('renders a negative Stock after machine need with the negative value class', async () => {
    const host = await renderDashboard({ products: [product({ quantityInStock: 10, machineReplenishmentNeed: 16 })] });

    const cell = Array.from(host.querySelectorAll('td')).find((td) => td.textContent?.trim() === '-6');
    expect(cell).toBeDefined();
    expect(cell?.className).toContain('value-negative');
  });

  it('no longer renders the permanent Admin tools banner', async () => {
    const host = await renderDashboard();

    expect(host.textContent).not.toContain('Admin tools');
  });

  it('renders exactly four headline cards in order: Sales this week, Needs refill, Needs ordering, Inventory', async () => {
    const host = await renderDashboard();

    const labels = Array.from(host.querySelectorAll('.stat-card-label')).map((el) => el.textContent?.trim());
    expect(labels).toEqual(['Sales this week', 'Needs refill', 'Needs ordering', 'Inventory']);
  });

  it('renders the label/value content before the icon tile in every summary stat card, so the icon sits at the right', async () => {
    const host = await renderDashboard();
    const heads = Array.from(host.querySelectorAll('.stat-card-head'));

    expect(heads).toHaveLength(4);
    for (const head of heads) {
      const children = Array.from(head.children);
      expect(children[0].classList.contains('stat-card-content')).toBe(true);
      expect(children[children.length - 1].classList.contains('icon-tile')).toBe(true);
    }
  });

  it('links each headline card to its existing authorized destination', async () => {
    const host = await renderDashboard();
    const cards = Array.from(host.querySelectorAll('a.stat-card')) as HTMLAnchorElement[];
    const hrefs = cards.map((a) => a.getAttribute('href'));

    expect(cards).toHaveLength(4);
    expect(hrefs[0]).toContain('/reports');
    expect(hrefs[1]).toContain('/pick-list');
    expect(hrefs[2]).toContain('needs-ordering');
    expect(hrefs[3]).toContain('/products');
  });

  it('gives every headline card a descriptive accessible name', async () => {
    const host = await renderDashboard();
    const cards = Array.from(host.querySelectorAll('a.stat-card'));

    for (const card of cards) {
      expect(card.getAttribute('aria-label')).toBeTruthy();
    }
  });

  it('never presents a failed Dashboard summary as a real zero', async () => {
    const host = await renderDashboard({ summary$: throwError(() => new Error('network error')) });

    const values = Array.from(host.querySelectorAll('.stat-card-value')).map((el) => el.textContent?.trim());
    expect(values).toEqual(['Unavailable', 'Unavailable', 'Unavailable', 'Unavailable']);
    expect(host.textContent).toContain('The Dashboard summary could not be loaded');
  });

  /**
   * A role without `Dashboard.ViewFinancials` is given the two non-financial cards (issue #527).
   * The page must render them correctly with both money figures absent - never as `$0.00`, never
   * as "Unavailable", and never by reading a property the response does not have.
   */
  it('renders only the two non-financial cards for a redacted summary', async () => {
    const host = await renderDashboard({ summary$: of(REDACTED_SUMMARY) });

    const labels = Array.from(host.querySelectorAll('.stat-card-label')).map((el) => el.textContent?.trim());
    expect(labels).toEqual(['Needs refill', 'Needs ordering']);
  });

  it('renders no sales or inventory figure at all in a redacted summary', async () => {
    const host = await renderDashboard({ summary$: of(REDACTED_SUMMARY) });

    const values = Array.from(host.querySelectorAll('.stat-card-value')).map((el) => el.textContent?.trim());
    expect(values).toEqual(['2', '4']);
    expect(host.textContent).not.toContain('Sales this week');
    expect(host.textContent).not.toContain('$1,234.56');
    expect(host.textContent).not.toContain('$0.00');
  });

  it('keeps the remaining cards and their figures and links unchanged under redaction', async () => {
    const host = await renderDashboard({ summary$: of(REDACTED_SUMMARY) });
    const hrefs = Array.from(host.querySelectorAll('a.stat-card')).map((card) => card.getAttribute('href'));

    expect(hrefs).toEqual(['/pick-list', '/products/needs-ordering']);
    expect(host.textContent).toContain('2 low · 1 empty selections across 5 machines evaluated');
    expect(host.textContent).toContain('of 50 products in the catalogue');
  });
});

describe('DashboardComponent Machines row navigation', () => {
  afterEach(() => TestBed.resetTestingModule());

  function machinesTable(host: HTMLElement): HTMLElement {
    const section = Array.from(host.querySelectorAll('section.card')).find((el) => el.textContent?.includes('Machines'));
    return section!.querySelector('table') as HTMLElement;
  }

  it('renders the machine name as a native link to the existing machine details route, and no Details button', async () => {
    const host = await renderDashboard({ machines: [MACHINE] });

    const table = machinesTable(host);
    const link = table.querySelector('tbody tr td a') as HTMLAnchorElement;

    expect(link).not.toBeNull();
    expect(link.getAttribute('href')).toBe('/machines/1');
    expect(link.textContent?.trim()).toBe('Machine A');
    expect(link.getAttribute('aria-label')).toBe('View details for Machine A');
    expect(table.querySelector('.btn')).toBeNull();
  });

  it('leaves the row itself without an emulated link role', async () => {
    const host = await renderDashboard({ machines: [MACHINE] });

    const row = machinesTable(host).querySelector('tbody tr') as HTMLElement;
    expect(row.getAttribute('role')).toBeNull();
    expect(row.getAttribute('tabindex')).toBeNull();
    expect(row.classList.contains('table-row-link')).toBe(true);
  });

  it('follows the row link when the row itself is clicked', async () => {
    const host = await renderDashboard({ machines: [MACHINE] });
    const navigateByUrl = spyOnNavigateByUrl();

    const row = machinesTable(host).querySelector('tbody tr') as HTMLElement;
    row.click();

    expect(navigatedUrls(navigateByUrl)).toEqual(['/machines/1']);
  });

  it('navigates once, through the link, when the link itself is clicked', async () => {
    const host = await renderDashboard({ machines: [MACHINE] });
    const navigateByUrl = spyOnNavigateByUrl();

    const link = machinesTable(host).querySelector('tbody tr td a') as HTMLAnchorElement;
    link.click();

    expect(navigatedUrls(navigateByUrl)).toEqual(['/machines/1']);
  });

  it('leaves a ctrl-click on the row to the browser so the link can open in a new tab', async () => {
    const host = await renderDashboard({ machines: [MACHINE] });
    const navigateByUrl = spyOnNavigateByUrl();

    const row = machinesTable(host).querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }));

    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('navigates once when Enter is pressed on the focused link', async () => {
    const host = await renderDashboard({ machines: [MACHINE] });
    const navigateByUrl = spyOnNavigateByUrl();

    const link = machinesTable(host).querySelector('tbody tr td a') as HTMLAnchorElement;
    link.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    link.click(); // the browser's own activation of a focused anchor on Enter

    expect(navigatedUrls(navigateByUrl)).toEqual(['/machines/1']);
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
