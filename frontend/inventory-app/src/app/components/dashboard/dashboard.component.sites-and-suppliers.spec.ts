import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
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
import { CHEAPEST_SUPPLIER_HELP, DashboardComponent } from './dashboard.component';

/**
 * The two Dashboard follow-ups to issue #972: machines listed under their sites (issue #980, now
 * inside issue #987's combined Site and machine performance card), and the observed Cheapest
 * supplier column in Reorder priorities (issue #981), which reuses issue #930's shared read and cell
 * unchanged. The read itself, its batching and the cell's popup have their own specs, and the
 * combined card's own behaviour is `site-machine-performance/site-machine-performance.component.spec.ts`'s;
 * this file is about the dashboard page composing them.
 *
 * Issue #1016 moved the column's explanation into a help button beside the heading and on each
 * narrow-screen card, using the shared `HelpToggletipComponent`. The toggletip's own interaction is
 * tested in `shared/help-toggletip/help-toggletip.component.spec.ts`; what this file tests is where
 * the dashboard puts it, which sentence it carries, and that neither of the two note rows above the
 * table is left behind.
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
  /** The reorder list this render answers with: pending, failed or a list of its own. */
  alerts?: Observable<ReorderAlertListing>;
}

async function render(options: Options = {}): Promise<{ fixture: ComponentFixture<DashboardComponent>; host: HTMLElement }> {
  const listing: ReorderAlertListing = { products: REORDER, mode: 'MachineAware', machineNeedUnavailableReason: null };
  const alerts = options.alerts ?? of(listing);
  const financials = options.canViewFinancials ?? true;
  const getSummary = options.getSummary ?? jest.fn((ids: readonly number[]) => of(cheapest(ids)));
  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock: () => alerts } },
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
      { provide: ProductPurchaseCostService, useValue: { getSummariesBatched: () => of([]) } },
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

function performanceSection(host: HTMLElement): HTMLElement {
  return host.querySelector('section[aria-labelledby="site-machine-performance-title"]') as HTMLElement;
}

function reorderSection(host: HTMLElement): HTMLElement {
  return host.querySelector('section[aria-labelledby="reorder-priorities-title"]') as HTMLElement;
}

/** Every Cheapest supplier help button on the page, in rendered order: the header's, then the cards'. */
function helpButtons(host: HTMLElement): HTMLButtonElement[] {
  return Array.from(host.querySelectorAll<HTMLButtonElement>('button[aria-label="About Cheapest supplier"]'));
}

function helpPanels(host: HTMLElement): HTMLElement[] {
  return Array.from(host.querySelectorAll<HTMLElement>('[data-testid="help-toggletip-panel"]'));
}

function press(key: string, target: EventTarget): void {
  target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
}

/**
 * The combined card's desktop groups, as [expander label, machine names] in rendered order. The
 * expander's leading chevron is presentation (`aria-hidden`), so it is dropped from the label.
 */
function tableGroups(host: HTMLElement): [string | null, string[]][] {
  return Array.from(performanceSection(host).querySelectorAll<HTMLElement>('tbody[id^="site-machines-"]')).map((body) => [
    host
      .querySelector(`[aria-controls="${body.id}"]`)
      ?.textContent?.replace(/\s+/g, ' ')
      .replace(/^[▴▾]\s*/, '')
      .trim() ?? null,
    Array.from(body.querySelectorAll('a.table-row-anchor')).map((anchor) => anchor.textContent?.trim() ?? '')
  ]);
}

describe('Dashboard machines under their sites (issues #980 and #987)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('composes one card whose site rows hold that site own machines', async () => {
    const { host } = await render();

    expect(performanceSection(host).querySelector('.card-title')?.textContent?.trim()).toBe(
      'Site and machine performance'
    );
    expect(tableGroups(host)).toEqual([
      ['Harbour Point Plaza', ['Avenue Hotel VM']],
      ['Pavillion Shopping Centre', ['Pavillion Left', 'Pavillion Right']],
      ['Other machines · 2 machines', ['Warehouse test unit', 'Moved machine']]
    ]);
  });

  it('groups the narrow-screen cards the same way', async () => {
    const { host } = await render();
    const groups = Array.from(
      performanceSection(host).querySelectorAll<HTMLElement>('[data-testid^="performance-card-group-"]')
    ).map((group) => [
      group
        .querySelector('[data-testid^="performance-card-expander-"]')
        ?.textContent?.replace(/\s+/g, ' ')
        .replace(/^[▴▾]\s*/, '')
        .trim(),
      Array.from(group.querySelectorAll('[data-testid^="performance-machine-card-"]')).length
    ]);

    expect(groups).toEqual([
      ['Harbour Point Plaza', 1],
      ['Pavillion Shopping Centre', 2],
      ['Other machines · 2 machines', 2]
    ]);
  });

  it('keeps the fleet totals over every machine', async () => {
    const { host } = await render();

    expect(performanceSection(host).querySelector('tfoot')?.textContent).toContain('Totals (5 machines)');
    expect(performanceSection(host).querySelector('tfoot')?.textContent).toContain('$100.00 sales');
  });

  it('shows the machines unheaded, in the same card, when the site list could not be read', async () => {
    const { host } = await render({ sites: throwError(() => new Error('down')) });

    expect(performanceSection(host).querySelector('[data-testid="sites-unavailable"]')).not.toBeNull();
    expect(tableGroups(host)).toEqual([
      [null, ['Pavillion Left', 'Avenue Hotel VM', 'Warehouse test unit', 'Pavillion Right', 'Moved machine']]
    ]);
  });
});

describe('Dashboard Reorder priorities Cheapest supplier column (issue #981)', () => {
  afterEach(() => TestBed.resetTestingModule());

  /**
   * Issue #1017 pages this card, so the one read is for every product it lists rather than for the
   * five rows on screen: that is the owner's requirement change, and it is what keeps a page turn
   * from asking anything and keeps the rows of a later page from rendering without their prices.
   */
  it('reads the cheapest supplier once, for every listed product, and again for no page turn', async () => {
    const getSummary = jest.fn((ids: readonly number[]) => of(cheapest(ids)));
    const { fixture, host } = await render({ getSummary });

    expect(getSummary).toHaveBeenCalledTimes(1);
    expect(getSummary).toHaveBeenCalledWith([1, 2, 3, 4, 5, 6, 7]);

    host.querySelector<HTMLButtonElement>('[data-testid="reorder-next-page"]')?.click();
    fixture.detectChanges();

    expect(getSummary).toHaveBeenCalledTimes(1);
    const cells = reorderSection(host).querySelectorAll('table [data-testid="cheapest-supplier-cell"]');
    expect(cells).toHaveLength(2);
    expect(cells[0].textContent).toContain('JB Metro');
  });

  it('shows each product cheapest supplier after the historical purchase columns', async () => {
    const { host } = await render();
    const headers = Array.from(reorderSection(host).querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    const cells = reorderSection(host).querySelectorAll('table [data-testid="cheapest-supplier-cell"]');

    expect(headers.slice(-3)).toEqual(['Last purchase', 'Cheapest purchase', 'Cheapest supplier']);
    expect(cells).toHaveLength(5);
    expect(cells[0].textContent).toContain('JB Metro');
  });

  /**
   * Rewritten for issue #1016, which is a requirement change rather than a weakened test: the owner
   * asked for the observed-prices sentence to move behind the header's help button and for the
   * purchase-cost sentence to be removed from the dashboard altogether. What the sentence says is
   * unchanged, and the help button's own behaviour is tested below.
   */
  it('says the supplier prices are observed in the header help panel, not in a row above the table', async () => {
    const { host, fixture } = await render();

    expect(host.querySelector('[data-testid="supplier-price-note"]')).toBeNull();
    expect(host.querySelector('[data-testid="purchase-cost-note"]')).toBeNull();

    helpButtons(host)[0].click();
    fixture.detectChanges();

    expect(helpPanels(host)[0].textContent?.trim()).toBe(CHEAPEST_SUPPLIER_HELP);
    expect(CHEAPEST_SUPPLIER_HELP).toContain('not costs you have paid');
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

describe('Dashboard Cheapest supplier help button (issue #1016)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('sits in the Cheapest supplier header, named after the column, and leaves its text alone', async () => {
    const { host } = await render();
    const header = Array.from(reorderSection(host).querySelectorAll('thead th')).at(-1) as HTMLElement;
    const button = header.querySelector<HTMLButtonElement>('button[aria-label="About Cheapest supplier"]');

    expect(header.textContent?.trim()).toBe('Cheapest supplier');
    expect(button).not.toBeNull();
    expect(button?.textContent?.trim()).toBe('');
    expect(button?.getAttribute('aria-expanded')).toBe('false');
    expect(button?.getAttribute('aria-controls')).toBeTruthy();
    expect(button?.getAttribute('title')).toBeNull();
  });

  it('opens the sentence on click and closes it again', async () => {
    const { host, fixture } = await render();
    const button = helpButtons(host)[0];

    button.click();
    fixture.detectChanges();
    expect(helpPanels(host)[0].id).toBe(button.getAttribute('aria-controls'));
    expect(button.getAttribute('aria-expanded')).toBe('true');

    button.click();
    fixture.detectChanges();
    expect(helpPanels(host)).toHaveLength(0);
  });

  it('opens on Enter and on Space, so it is reachable from the keyboard alone', async () => {
    const { host, fixture } = await render();

    press('Enter', helpButtons(host)[0]);
    fixture.detectChanges();
    expect(helpPanels(host)[0].textContent?.trim()).toBe(CHEAPEST_SUPPLIER_HELP);

    press('Enter', helpButtons(host)[0]);
    fixture.detectChanges();
    press(' ', helpButtons(host)[0]);
    fixture.detectChanges();
    expect(helpPanels(host)[0].textContent?.trim()).toBe(CHEAPEST_SUPPLIER_HELP);
  });

  it('closes on Escape and returns focus to the header button', async () => {
    const { host, fixture } = await render();
    const button = helpButtons(host)[0];

    button.focus();
    button.click();
    fixture.detectChanges();
    press('Escape', document);
    fixture.detectChanges();

    expect(helpPanels(host)).toHaveLength(0);
    expect(document.activeElement).toBe(button);
  });

  it('gives every narrow-screen card its own button, and opens one panel at a time', async () => {
    const { host, fixture } = await render();
    const cardButtons = Array.from(
      reorderSection(host).querySelectorAll<HTMLButtonElement>(
        '[data-testid="reorder-card-cheapest-supplier"] button[aria-label="About Cheapest supplier"]'
      )
    );
    const ids = cardButtons.map((button) => button.getAttribute('aria-controls'));

    expect(cardButtons).toHaveLength(5);
    expect(new Set(ids).size).toBe(5);

    cardButtons[0].click();
    fixture.detectChanges();
    cardButtons[3].click();
    fixture.detectChanges();

    expect(helpPanels(host)).toHaveLength(1);
    expect(helpPanels(host)[0].textContent?.trim()).toBe(CHEAPEST_SUPPLIER_HELP);
    expect(cardButtons.map((button) => button.getAttribute('aria-expanded'))).toEqual([
      'false',
      'false',
      'false',
      'true',
      'false'
    ]);
  });

  it('says nothing about the recorded purchase costs, with or without Dashboard.ViewFinancials', async () => {
    for (const canViewFinancials of [true, false]) {
      const { host } = await render({ canViewFinancials });

      expect(host.querySelector('[data-testid="purchase-cost-note"]')).toBeNull();
      expect(reorderSection(host).textContent).not.toContain('historical costs');
      TestBed.resetTestingModule();
    }
  });

  it('shows no help button at all while the list is loading, has failed, or is empty', async () => {
    const pending = await render({ alerts: new Subject<ReorderAlertListing>() });
    expect(helpButtons(pending.host)).toHaveLength(0);
    TestBed.resetTestingModule();

    const failed = await render({ alerts: throwError(() => new Error('down')) });
    expect(helpButtons(failed.host)).toHaveLength(0);
    TestBed.resetTestingModule();

    const empty = await render({
      alerts: of({ products: [], mode: 'MachineAware', machineNeedUnavailableReason: null } as ReorderAlertListing)
    });
    expect(helpButtons(empty.host)).toHaveLength(0);
  });
});
