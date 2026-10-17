import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { DailyReportComponent } from './daily-report.component';
import { DashboardReportComponent } from './dashboard-report.component';
import { MachineReportComponent } from './machine-report.component';
import { ProductReportComponent } from './product-report.component';
import { ReconciliationReportComponent } from './reconciliation-report.component';
import { SiteCommissionsReportComponent } from './site-commissions-report.component';
import { TransactionSalesReportComponent } from './transaction-sales-report.component';
import { countOrState } from './report-formatting';
import {
  DailyReport, DashboardReport, MachineReport, ProductReport, ReconciliationReport, ReportAvailability,
  ReportProviderAvailability, ReportingService, SiteCommissionReport, TransactionSalesReport,
} from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { SupportSessionService } from '../../support-session/support-session.service';

/**
 * Issue #731 (under issue #730's model, parent #329 decision P9): every remaining sales-based report
 * shows the availability states beside the figures they govern, and never presents an
 * unaccounted-for period as a real zero. Site Commissions shows its unavailable answer instead of an
 * error.
 */

const complete: ReportAvailability = { state: 'Complete', asOfUtc: null, isComplete: true };
const missing: ReportAvailability = { state: 'Missing', asOfUtc: null, isComplete: false };
const unverified: ReportAvailability = { state: 'Unverified', asOfUtc: null, isComplete: false };
const staleTo18July: ReportAvailability = { state: 'Stale', asOfUtc: '2026-07-18T06:00:00Z', isComplete: false };

/** The severity order the server combines states by, mirrored here only to build fixtures. */
const severity: Record<ReportAvailability['state'], number> = {
  Missing: 0, Unverified: 1, Stale: 2, Complete: 3,
};

function availability(sales: ReportAvailability, fees: ReportAvailability): ReportProviderAvailability {
  return { sales, fees, combined: severity[fees.state] < severity[sales.state] ? fees : sales };
}

const emptyQuality = {
  missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
  commissionNotPersisted: false, containsUnmappedProducts: false,
};

function route() {
  return { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map() } } };
}

function machines() {
  return { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } };
}

async function render<T>(component: new (...args: never[]) => T, reporting: unknown, extra: unknown[] = []) {
  await TestBed.configureTestingModule({
    imports: [component as never],
    providers: [route(), { provide: ReportingService, useValue: reporting }, machines(), ...(extra as never[])],
  }).compileComponents();

  const fixture = TestBed.createComponent(component as never);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

afterEach(() => TestBed.resetTestingModule());

describe('countOrState (issue #731)', () => {
  it('never prints a count that coverage did not measure', () => {
    expect(countOrState(0, missing)).toBe('No provider data');
    expect(countOrState(0, unverified)).toBe('Unverified');
    expect(countOrState(undefined, staleTo18July)).toContain('As of');
  });

  it('keeps a real count visible in an incomplete state and prints a measured zero', () => {
    expect(countOrState(14, staleTo18July)).toBe('14');
    expect(countOrState(0, complete)).toBe('0');
  });
});

function daily(providerAvailability?: ReportProviderAvailability, grossSales = 0): DailyReport {
  return {
    from: '2026-07-01', to: '2026-07-31',
    rows: [{
      date: '2026-07-05', sales: grossSales, grossSales, cardSales: grossSales, cashSales: 0, quantity: 0,
      transactionCount: 0, costOfGoods: grossSales ? 10 : 0, grossProfit: grossSales ? 20 : 0,
      isCogsComplete: true, grossMarginPercent: grossSales ? 25 : 0,
      nayaxFeesIncludingGst: 0, importedReimbursement: 0, netReimbursement: 0,
      reconciliationStatus: 'Pending', averageSale: 0,
    }],
    totals: {
      grossSales, cardSales: grossSales, cashSales: 0, quantity: 0, costOfGoods: 0, grossProfit: 0,
      transactionCount: 0, averageSale: 0, isCogsComplete: true, uncostedTransactionCount: 0,
      uncostedSalesAmount: 0, grossMarginPercent: 0, nayaxFeesExGst: 0, nayaxFeesIncludingGst: 0,
      importedReimbursement: 0, netReimbursement: 0,
    },
    dataQuality: { ...emptyQuality },
    providerAvailability,
  };
}

describe('DailyReportComponent provider availability', () => {
  it('shows no notice and a real zero when both families are complete', async () => {
    const host = await render(DailyReportComponent, { daily: jest.fn(() => of(daily(availability(complete, complete)))) });

    expect(host.querySelector('[data-testid="provider-availability"]')).toBeNull();
    expect(host.querySelector('[data-testid="totals-gross-sales"]')!.textContent).toContain('$0.00');
  });

  it('shows a day with no sales data as absent rather than as zero', async () => {
    const host = await render(DailyReportComponent, { daily: jest.fn(() => of(daily(availability(missing, missing)))) });

    expect(host.querySelector('[data-testid="provider-availability"]')).not.toBeNull();
    const totals = host.querySelector('[data-testid="totals-gross-sales"]')!;
    expect(totals.textContent).toContain('No provider data');
    expect(totals.textContent).not.toContain('$0.00');
    expect(host.querySelector('[data-testid="totals-fees"]')!.textContent).toContain('No provider data');
  });

  it('keeps historical sales after a disconnect visible and labels them as of the covered instant', async () => {
    const host = await render(DailyReportComponent, {
      daily: jest.fn(() => of(daily(availability(staleTo18July, complete), 880))),
    });

    const totals = host.querySelector('[data-testid="totals-gross-sales"]')!;
    expect(totals.textContent).toContain('$880.00');
    expect(host.querySelector('[data-testid="provider-availability"]')!.textContent).toContain('Incomplete');
  });
});

function reconciliation(providerAvailability?: ReportProviderAvailability): ReconciliationReport {
  return {
    from: '2026-07-01', to: '2026-07-31', nayaxSales: 0, importedReimbursement: 0, difference: 0,
    tolerance: 0.01, isMatch: true, dataQuality: { ...emptyQuality },
    totalVendingSales: 0, cardSales: 0, cashSales: 0, totalTransactionCount: 0, cashTransactionCount: 0,
    cardTransactionSales: 0, nayaxReportedGrossCardSales: 0, nayaxReportedCardTransactionCount: 0,
    grossDifference: 0, grossStatus: 'Reconciled', processingFeesExGst: 0, feeGst: 0, otherFees: 0,
    adjustments: 0, expectedNetReimbursement: 0, actualNetReimbursement: 0, settlementDifference: 0,
    adjustmentsSupported: false, settlementStatus: 'Reconciled', status: 'Reconciled', periodRows: [],
    providerAvailability,
  };
}

describe('ReconciliationReportComponent provider availability', () => {
  it('shows no notice and real zeros when both sides are complete', async () => {
    const host = await render(ReconciliationReportComponent, {
      reconciliation: jest.fn(() => of(reconciliation(availability(complete, complete)))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).toBeNull();
    expect(host.querySelector('[data-testid="gross-difference"]')!.textContent).toContain('$0.00');
  });

  it('never presents a difference between two unmeasured sides as a reconciled zero', async () => {
    const host = await render(ReconciliationReportComponent, {
      reconciliation: jest.fn(() => of(reconciliation(availability(missing, missing)))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).not.toBeNull();
    const difference = host.querySelector('[data-testid="gross-difference"]')!;
    expect(difference.textContent).toContain('No provider data');
    expect(difference.textContent).not.toContain('$0.00');
  });

  it('labels each side with its own evidence', async () => {
    const host = await render(ReconciliationReportComponent, {
      reconciliation: jest.fn(() => of(reconciliation(availability(staleTo18July, missing)))),
    });

    expect(host.querySelector('[data-testid="sales-availability"]')!.textContent).toContain('As of 18 July 2026');
    expect(host.querySelector('[data-testid="fees-availability"]')!.textContent).toContain('No provider data');
    expect(host.querySelector('[data-testid="nayax-reported-gross"]')!.textContent).toContain('No provider data');
  });
});

function machineReport(providerAvailability?: ReportProviderAvailability, rows: MachineReport['rows'] = []): MachineReport {
  return { from: '2026-07-01', to: '2026-07-31', rows, dataQuality: { ...emptyQuality }, providerAvailability };
}

describe('MachineReportComponent provider availability', () => {
  it('explains an empty machine list rather than letting it read as a fleet that sold nothing', async () => {
    const host = await render(MachineReportComponent, {
      machines: jest.fn(() => of(machineReport(availability(missing, missing)))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="empty-machine-list"]')!.textContent).toContain('No provider data');
  });

  it('says nothing about an empty list when coverage is complete', async () => {
    const host = await render(MachineReportComponent, {
      machines: jest.fn(() => of(machineReport(availability(complete, complete)))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).toBeNull();
    expect(host.querySelector('[data-testid="empty-machine-list"]')).toBeNull();
  });

  it('shows a machine row with no measured sales as absent rather than as zero', async () => {
    const host = await render(MachineReportComponent, {
      machines: jest.fn(() => of(machineReport(availability(missing, missing), [{
        machineId: 10, machineName: 'Machine A', sales: 0, quantity: 0, costOfGoods: 0, grossProfit: 0,
        marginPercent: 0, transactionCount: 0, siteCommission: 0, directProfit: 0, directMarginPercent: 0,
        commissionPercent: 0, cardSales: 0, cashSales: 0, isCogsComplete: true, directOperatingExpenses: 25,
      }]))),
    });

    const text = host.textContent ?? '';
    expect(text).toContain('No provider data');
    // The machine's own recorded direct expenses stay exact.
    expect(text).toContain('$25.00');
  });
});

function productReport(salesAvailability?: ReportAvailability, rows: ProductReport['rows'] = []): ProductReport {
  return { from: '2026-07-01', to: '2026-07-31', rows, dataQuality: { ...emptyQuality }, salesAvailability };
}

describe('ProductReportComponent provider availability', () => {
  it('explains an empty product table rather than letting it read as a period that sold nothing', async () => {
    const host = await render(ProductReportComponent, {
      products: jest.fn(() => of(productReport(missing))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="empty-product-list"]')!.textContent).toContain('No provider data');
  });

  it('shows a product row with no measured revenue as absent rather than as zero', async () => {
    const host = await render(ProductReportComponent, {
      products: jest.fn(() => of(productReport(unverified, [{
        productId: 1, productName: 'Water', sales: 0, quantity: 0, costOfGoods: 0, grossProfit: 0,
        marginPercent: 0, transactionCount: 0, isUnmapped: false, historicalCostAvailable: true,
        isCogsComplete: true, cardRevenue: 0, cashRevenue: 0, lastCost: 1.1,
      }]))),
    });

    const text = host.textContent ?? '';
    expect(text).toContain('Unverified');
    // The product's own purchase cost is not provider data and stays exact.
    expect(text).toContain('$1.10');
  });

  it('says nothing when coverage is complete and prints a real zero', async () => {
    const host = await render(ProductReportComponent, { products: jest.fn(() => of(productReport(complete))) });

    expect(host.querySelector('[data-testid="provider-availability"]')).toBeNull();
    expect(host.querySelector('[data-testid="empty-product-list"]')).toBeNull();
  });
});

function dashboard(providerAvailability?: ReportProviderAvailability, sales = 0): DashboardReport {
  return {
    from: '2026-07-01', to: '2026-07-31', sales, totalSales: sales, grossProfit: 0, transactions: 0,
    quantity: 0, machineCount: 0, productCount: 0, unmappedProductCount: 0,
    dataQuality: { ...emptyQuality },
    cardSales: 0, cashSales: 0, nayaxFeesIncludingGst: 0, siteCommission: 0, netProfit: 0,
    netMarginPercent: 0, grossMarginPercent: 0, averageSale: 0, isCogsComplete: true,
    costOfGoodsSold: 0, expectedReimbursement: 0, actualReimbursement: 0, reimbursementDifference: 0,
    reconciliationStatus: 'Reconciled', isReconciled: true, reconciliationTolerance: 0.01,
    deliveryCosts: 12.5, packageCosts: 3.25, otherOperatingExpenses: 40,
    providerAvailability,
  };
}

describe('DashboardReportComponent provider availability', () => {
  it('shows no notice and real zeros when both families are complete', async () => {
    const host = await render(DashboardReportComponent, {
      dashboard: jest.fn(() => of(dashboard(availability(complete, complete)))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).toBeNull();
    expect(host.querySelector('[data-testid="dashboard-total-sales"]')!.textContent).toContain('$0.00');
    expect(host.querySelector('[data-testid="dashboard-transactions"]')!.textContent).toContain('0');
  });

  it('shows every provider figure as absent rather than as zero', async () => {
    const host = await render(DashboardReportComponent, {
      dashboard: jest.fn(() => of(dashboard(availability(missing, missing)))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).not.toBeNull();
    for (const id of ['dashboard-total-sales', 'dashboard-gross-profit', 'dashboard-net-profit',
      'dashboard-transactions', 'dashboard-cost-of-goods', 'dashboard-nayax-fees']) {
      const cell = host.querySelector(`[data-testid="${id}"]`);
      expect(cell).not.toBeNull();
      expect(cell!.textContent).toContain('No provider data');
      expect(cell!.textContent).not.toContain('$0.00');
    }
    expect(host.querySelector('[data-testid="dashboard-gross-margin"]')!.textContent).not.toContain('0.0%');
    // This page shows no figure the business records itself, so there is nothing here that should
    // stay exact; the bookkeeping report, which does show them, asserts that in its own spec.
    expect(host.textContent).not.toContain('$0.00');
  });

  it('labels the two families separately, as the bookkeeping report decided them', async () => {
    const host = await render(DashboardReportComponent, {
      dashboard: jest.fn(() => of(dashboard(availability(staleTo18July, missing), 880))),
    });

    expect(host.querySelector('[data-testid="dashboard-total-sales"]')!.textContent).toContain('$880.00');
    expect(host.querySelector('[data-testid="dashboard-sales-availability"]')!.textContent).toContain('As of 18 July 2026');
    expect(host.querySelector('[data-testid="dashboard-fee-availability"]')!.textContent).toContain('No provider data');
  });
});

function transactions(salesAvailability?: ReportAvailability, sales = 0): TransactionSalesReport {
  return {
    from: '2026-07-01', to: '2026-07-31', rows: [], page: 1, pageSize: 50, totalCount: 0,
    filterOptions: { sites: [], products: [] },
    totals: {
      transactionCount: 0, completedTransactionCount: 0, sales, cardSales: 0, cashSales: 0,
      costedCompletedTransactionCount: 0, uncostedCompletedTransactionCount: 0, isCogsComplete: true,
      costOfGoods: 0, partialCostOfGoods: 0, grossProfit: 0, grossMarginPercent: 0, directProfit: 0,
      directMarginPercent: 0, partialGrossProfit: 0, partialDirectProfit: 0,
      estimatedFeeExGst: 0, estimatedFeeGst: 0, estimatedFeeIncGst: 0, commissionAmount: 0,
    },
    dataQuality: { ...emptyQuality },
    salesAvailability,
  };
}

describe('TransactionSalesReportComponent provider availability', () => {
  it('shows no notice and a real zero total when coverage is complete', async () => {
    const host = await render(TransactionSalesReportComponent, {
      transactions: jest.fn(() => of(transactions(complete))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).toBeNull();
    expect(host.querySelector('[data-testid="totals-sales"]')!.textContent).toContain('$0.00');
  });

  it('never presents an unmeasured total as zero, and says the listed rows may not be all of them', async () => {
    const host = await render(TransactionSalesReportComponent, {
      transactions: jest.fn(() => of(transactions(missing))),
    });

    expect(host.querySelector('[data-testid="provider-availability"]')).not.toBeNull();
    const total = host.querySelector('[data-testid="totals-sales"]')!;
    expect(total.textContent).toContain('No provider data');
    expect(total.textContent).not.toContain('$0.00');
    expect(host.querySelector('[data-testid="transaction-count"]')!.textContent).toContain('No provider data');
  });

  it('keeps a real total visible in a stale period, beside its badge', async () => {
    const host = await render(TransactionSalesReportComponent, {
      transactions: jest.fn(() => of(transactions(staleTo18July, 42.5))),
    });

    expect(host.querySelector('[data-testid="totals-sales"]')!.textContent).toContain('$42.50');
    expect(host.querySelector('[data-testid="sales-availability"]')!.textContent).toContain('As of 18 July 2026');
  });
});

function siteCommissions(overrides: Partial<SiteCommissionReport> = {}): SiteCommissionReport {
  return { from: '2026-07-01', to: '2026-07-31', rows: [], ...overrides };
}

async function renderSiteCommissions(report: SiteCommissionReport) {
  await TestBed.configureTestingModule({
    imports: [SiteCommissionsReportComponent],
    providers: [
      { provide: ReportingService, useValue: { siteCommissions: jest.fn(() => of(report)) } },
      { provide: SiteService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: SupportSessionService, useValue: { active$: of(false) } },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(SiteCommissionsReportComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('SiteCommissionsReportComponent unavailable answer (issue #731)', () => {
  it('shows the unavailable reason instead of an empty table when the provider is not usable', async () => {
    const host = await renderSiteCommissions(siteCommissions({
      isAvailable: false,
      unavailableReason: 'NayaxNotConnected',
      unavailableDetail: 'Site commission is unavailable because Nayax is not connected for this business.',
    }));

    const notice = host.querySelector('[data-testid="commission-unavailable"]');
    expect(notice).not.toBeNull();
    expect(notice!.textContent).toContain('Nayax is not connected');
    expect(host.querySelector('table')).toBeNull();
  });

  it('explains a permission refusal with the detail the API gave', async () => {
    const host = await renderSiteCommissions(siteCommissions({
      isAvailable: false,
      unavailableReason: 'PermissionNotGranted',
      unavailableDetail: 'Nayax refused the machine list for lack of permission.',
    }));

    expect(host.querySelector('[data-testid="commission-unavailable"]')!.textContent)
      .toContain('lack of permission');
  });

  /**
   * A business whose fleet read succeeded but holds no site-mapped machine is a different answer: it
   * genuinely has no site to owe anything, and the ordinary (empty) table is the right thing to
   * show. The two must not collapse into each other.
   */
  it('shows the ordinary table for an available report with no rows', async () => {
    const host = await renderSiteCommissions(siteCommissions({ isAvailable: true }));

    expect(host.querySelector('[data-testid="commission-unavailable"]')).toBeNull();
    expect(host.querySelector('table')).not.toBeNull();
  });

  /** An older response carries no flag at all, and only ever returned an available report. */
  it('treats a response with no availability flag as available', async () => {
    const host = await renderSiteCommissions(siteCommissions());

    expect(host.querySelector('[data-testid="commission-unavailable"]')).toBeNull();
    expect(host.querySelector('table')).not.toBeNull();
  });
});
