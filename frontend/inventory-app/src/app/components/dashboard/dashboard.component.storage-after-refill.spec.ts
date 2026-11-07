import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { DashboardSummary, Product, ReorderAlertListing } from '../../models/models';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { NayaxSaleReviewService } from '../../services/nayax-sale-review.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { DashboardService } from '../../services/dashboard.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { CheapestSupplierService } from '../../services/cheapest-supplier.service';
import { AccessService } from '../../access/access.service';
import { BusinessSettingsService } from '../../services/business-settings.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { NayaxUnavailability } from '../../provider/nayax-availability';

/**
 * The Reorder priorities "Storage after refill" column (issue #1020): `quantityInStock -
 * machineReplenishmentNeed`, computed in the browser purely so an operator is not left doing that
 * subtraction themselves. It is a display-only figure - never a second reorder formula - and
 * follows the same storage-only hiding rule as Machine need (issue #727), because the machine need
 * there is an unread `0` and the subtraction would be misleading.
 */

const SUMMARY: DashboardSummary = {
  asOfUtc: '2026-10-21T09:30:00Z',
  businessDate: '2026-10-21T00:00:00Z',
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
    quantityInStock: 32,
    maxStockInMachine: 20,
    machineReplenishmentNeed: 3,
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
    isActive: true,
    isLowStock: true,
    isReorderAlert: true,
    ...overrides
  };
}

function machineAwareAlerts(products: Product[]): ReorderAlertListing {
  return { products, mode: 'MachineAware', machineNeedUnavailableReason: null };
}

function storageOnlyAlerts(products: Product[], reason: NayaxUnavailability = 'NayaxNotConnected'): ReorderAlertListing {
  return { products, mode: 'StorageOnly', machineNeedUnavailableReason: reason };
}

async function render(alerts: Observable<ReorderAlertListing>): Promise<HTMLElement> {
  await TestBed.configureTestingModule({
    imports: [DashboardComponent],
    providers: [
      provideRouter([]),
      { provide: ProductService, useValue: { getLowStock: () => alerts } },
      { provide: MachineService, useValue: { getAll: () => of([]) } },
      { provide: NayaxSaleReviewService, useValue: { summary: () => of({ needsReviewCount: 0, notCountedCount: 0 }) } },
      { provide: SiteService, useValue: { getAll: () => of([]) } },
      { provide: NayaxSalesSyncService, useValue: { syncLatest: () => of({ wasRecentlySynced: false, lastSyncedAtUtc: null }) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
      { provide: DashboardService, useValue: { getSummary: () => of(SUMMARY) } },
      { provide: ProductPurchaseCostService, useValue: { getSummariesBatched: () => of([]) } },
      { provide: CheapestSupplierService, useValue: { getSummary: () => of({ displayBasis: 'ExGst', products: [] }) } },
      { provide: AccessService, useValue: { allows: () => of(false) } },
      { provide: BusinessSettingsService, useValue: { get: () => of(null) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(DashboardComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function alertsSection(host: HTMLElement): HTMLElement {
  const section = Array.from(host.querySelectorAll('section.card')).find((el) =>
    el.textContent?.includes('Reorder priorities')
  );
  return section as HTMLElement;
}

function alertHeaders(host: HTMLElement): (string | undefined)[] {
  return Array.from(alertsSection(host).querySelectorAll('thead th')).map((th) => th.textContent?.trim());
}

/** The "Storage after refill" desktop cell for the first rendered row, with its CSS classes. */
function firstDesktopCell(host: HTMLElement): HTMLTableCellElement | null {
  const headers = alertHeaders(host);
  const index = headers.indexOf('Storage after refill');
  if (index === -1) return null;
  const row = alertsSection(host).querySelector('tbody tr');
  return (row?.querySelectorAll('td')[index] as HTMLTableCellElement) ?? null;
}

function firstCard(host: HTMLElement): HTMLElement {
  return alertsSection(host).querySelector('[data-testid="reorder-cards"] > li') as HTMLElement;
}

function cardEntry(host: HTMLElement, label: string): { dt: Element | null; dd: Element | null } {
  const card = firstCard(host);
  const dt = Array.from(card.querySelectorAll('dt')).find((candidate) => candidate.textContent?.trim() === label) ?? null;
  const dd = dt?.nextElementSibling ?? null;
  return { dt, dd };
}

describe('Reorder priorities Storage after refill column (issue #1020)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the column immediately after Machine need on desktop', async () => {
    const host = await render(of(machineAwareAlerts([product()])));

    const headers = alertHeaders(host);
    expect(headers.indexOf('Storage after refill')).toBe(headers.indexOf('Machine need') + 1);
  });

  it('shows storage stock minus machine need as a plain positive number, with no negative styling', async () => {
    const host = await render(of(machineAwareAlerts([product({ quantityInStock: 32, machineReplenishmentNeed: 3 })])));

    const cell = firstDesktopCell(host);
    expect(cell?.textContent?.trim()).toBe('29');
    expect(cell?.classList.contains('value-negative')).toBe(false);
  });

  it('shows a zero result as a plain number, with no negative styling', async () => {
    const host = await render(of(machineAwareAlerts([product({ quantityInStock: 5, machineReplenishmentNeed: 5 })])));

    const cell = firstDesktopCell(host);
    expect(cell?.textContent?.trim()).toBe('0');
    expect(cell?.classList.contains('value-negative')).toBe(false);
  });

  it('shows a negative result as a signed number styled value-negative', async () => {
    const host = await render(of(machineAwareAlerts([product({ quantityInStock: 2, machineReplenishmentNeed: 7 })])));

    const cell = firstDesktopCell(host);
    expect(cell?.textContent?.trim()).toBe('-5');
    expect(cell?.classList.contains('value-negative')).toBe(true);
  });

  it('hides the column in StorageOnly mode, exactly like Machine need', async () => {
    const host = await render(of(storageOnlyAlerts([product()])));

    const headers = alertHeaders(host);
    expect(headers).not.toContain('Machine need');
    expect(headers).not.toContain('Storage after refill');
  });

  it('leaves every other reorder column at its original value', async () => {
    const host = await render(
      of(machineAwareAlerts([product({ quantityInStock: 32, machineReplenishmentNeed: 3, needToOrder: 12 })]))
    );

    const row = alertsSection(host).querySelector('tbody tr') as HTMLElement;
    const cells = Array.from(row.querySelectorAll('td')).map((cell) => cell.textContent?.trim());
    expect(cells[0]).toBe('Cola 375mL');
    expect(cells[1]).toBe('32');
    expect(cells[2]).toBe('3');
    expect(cells[4]).toContain('12');
  });

  it('shows the same entry, value and styling on the mobile card, beside Machine need', async () => {
    const host = await render(of(machineAwareAlerts([product({ quantityInStock: 2, machineReplenishmentNeed: 7 })])));

    const { dd } = cardEntry(host, 'Storage after refill');
    expect(dd?.textContent?.trim()).toBe('-5 units');
    expect(dd?.classList.contains('value-negative')).toBe(true);
  });

  it('hides the mobile entry in StorageOnly mode, exactly like Machine need', async () => {
    const host = await render(of(storageOnlyAlerts([product()])));

    const card = firstCard(host);
    const labels = Array.from(card.querySelectorAll('dt')).map((dt) => dt.textContent?.trim());
    expect(labels).not.toContain('Machine need');
    expect(labels).not.toContain('Storage after refill');
  });
});
