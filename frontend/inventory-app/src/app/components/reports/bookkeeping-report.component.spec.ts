import { readFileSync } from 'fs';
import { join } from 'path';
import { ActivatedRoute } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { BookkeepingReportComponent } from './bookkeeping-report.component';
import { ReportingFilter, ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { BookkeepingReport } from '../../features/reports/bookkeeping/models/bookkeeping-report.model';
import { ReportProviderAvailability } from '../../features/reports/shared/models/reporting.models';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';

const REPORT: BookkeepingReport = {
  from: '2026-01-01',
  to: '2026-01-31',
  financialYear: '2025/26',
  sales: 1000,
  grossProfit: 400,
  fees: 20,
  netSettlement: 980,
  gstOnSales: 90,
  gstOnFees: 2,
  dataQuality: {
    missingStatus: false,
    historicalCostUnavailable: false,
    gstClassificationMissing: false,
    commissionNotPersisted: false,
    containsUnmappedProducts: false
  }
};

async function render(overrides: Partial<BookkeepingReport> = {}) {
  const route = {} as unknown as ActivatedRoute;
  const reports = {
    bookkeeping: () => of({ ...REPORT, ...overrides }),
    // The GST summary child (issue #1318) asks for its own report. These tests are about the
    // bookkeeping page's own notes and help, so the business is not registered for GST here; the
    // summary's own spec covers what a registered one sees.
    gstBasSummary: () => of({
      from: REPORT.from, to: REPORT.to, status: 'NotGstRegistered', figures: null,
      dataQuality: {
        missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
        commissionNotPersisted: false, containsUnmappedProducts: false, notes: []
      }
    })
  } as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;

  await TestBed.configureTestingModule({
    imports: [BookkeepingReportComponent],
    providers: [
      { provide: ActivatedRoute, useValue: route },
      { provide: ReportingService, useValue: reports },
      { provide: MachineService, useValue: machines },
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(BookkeepingReportComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

/**
 * Issue #476: the data-quality section must describe the requested period only, and the normal
 * calculation methodology belongs in expandable help instead of a permanent disclaimer list.
 */
describe('BookkeepingReportComponent conditional data quality and calculation help (issue #476)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('hides the data-quality section when the report reported no problem for the period', async () => {
    const host = await render();

    expect(host.querySelector('[data-testid="data-quality-notes"]')).toBeNull();
    expect(host.textContent).not.toContain('Data quality notes');
  });

  it('shows no migration or raw-status implementation history anywhere on the page', async () => {
    const host = await render();

    const text = host.textContent ?? '';
    expect(text).not.toContain('stored raw');
    expect(text).not.toContain('backfilled');
    expect(text).not.toContain('migration');
  });

  it('renders every data-quality note the report supplied, and only those', async () => {
    const host = await render({
      uncostedTransactionCount: 2,
      uncostedSalesAmount: 12.5,
      isCogsComplete: false,
      missingStatusTransactionCount: 1,
      dataQuality: {
        ...REPORT.dataQuality,
        missingStatus: true,
        historicalCostUnavailable: true,
        notes: [
          '1 Nayax transaction(s) have no status ID and are excluded from completed sales.',
          '2 completed sale(s) totalling 12.50 have no persisted COGS; profit is incomplete.'
        ]
      }
    });

    const section = host.querySelector('[data-testid="data-quality-notes"]');
    expect(section).not.toBeNull();
    expect(section!.querySelectorAll('li')).toHaveLength(2);
    expect(section!.textContent).toContain('have no status ID');
    expect(section!.textContent).toContain('no persisted COGS');
  });

  it('offers the calculation help as a native, keyboard-accessible disclosure that starts collapsed', async () => {
    const host = await render();

    const help = host.querySelector('[data-testid="calculation-help"]') as HTMLDetailsElement | null;
    expect(help).not.toBeNull();
    expect(help!.tagName).toBe('DETAILS');
    expect(help!.open).toBe(false);
    const summary = help!.querySelector('summary');
    expect(summary).not.toBeNull();
    expect(summary!.textContent).toContain('How this report is calculated');
  });

  it('explains persisted historical sale costs and effective-dated commission agreements in the help', async () => {
    const host = await render();

    const help = host.querySelector('[data-testid="calculation-help"]')!;
    expect(help.textContent).toContain('cost recorded on each sale');
    expect(help.textContent).toContain('effective-dated site commission agreement');
  });

  it('keeps the GST-on-sales rule visible beside the figure, not only inside the collapsed help', async () => {
    const host = await render();

    const estimate = host.querySelector('[data-testid="gst-on-sales-estimate"]');
    expect(estimate).not.toBeNull();
    expect(estimate!.closest('details')).toBeNull();
    // Issue #1255 replaced "assumes all included sales are taxable" with the actual rule.
    expect(estimate!.textContent).not.toContain('assumes all included sales are taxable');
    expect(estimate!.textContent).toContain('one eleventh of taxable sales');
    expect(estimate!.textContent).toContain('GST rule is GST-free are left out');
    expect(estimate!.textContent).toContain('a product with no GST rule is taxable');
  });

  it('lists excluded non-completed transactions as neutral scope information, never as a warning', async () => {
    const host = await render({ declinedOrCancelledTransactionCount: 4, pendingTransactionCount: 2 });

    expect(host.querySelector('[data-testid="data-quality-notes"]')).toBeNull();
    const excluded = host.querySelector('[data-testid="excluded-transactions"]');
    expect(excluded).not.toBeNull();
    expect(excluded!.textContent).toContain('Cancelled or declined');
    expect(excluded!.textContent).toContain('4');
    expect(excluded!.textContent).toContain('Pending');
    expect(excluded!.classList.contains('alert-warning')).toBe(false);
  });

  it('omits the excluded-transaction list when every transaction in the period was completed', async () => {
    const host = await render();

    expect(host.querySelector('[data-testid="excluded-transactions"]')).toBeNull();
  });

  /**
   * The new sections must reuse the shared card/typography tokens rather than introduce a utility
   * class of their own: an uncompiled class renders unstyled and is the way this kind of block
   * breaks a narrow screen. Every class the new markup uses therefore has to exist in the committed
   * compiled stylesheet, which is also what keeps the desktop and narrow layouts identical to the
   * cards already on this page.
   */
  it('builds the new sections from shared tokens that the committed stylesheet already compiles', async () => {
    const host = await render({ declinedOrCancelledTransactionCount: 1 });
    const css = readFileSync(join(__dirname, '..', '..', '..', 'styles.css'), 'utf8');

    const sections = [
      host.querySelector('[data-testid="excluded-transactions"]')!,
      host.querySelector('[data-testid="calculation-help"]')!,
      host.querySelector('[data-testid="gst-on-sales-estimate"]')!
    ];
    const tokens = new Set<string>();
    for (const section of sections) {
      for (const element of [section, ...Array.from(section.querySelectorAll('*'))]) {
        element.classList.forEach(token => tokens.add(token));
      }
    }

    expect(tokens.size).toBeGreaterThan(0);
    for (const token of tokens) {
      expect(css).toContain(`.${token}`);
    }
  });
});

/**
 * Issue #804: Bookkeeping shows the sales volume behind its own total and the average sale over
 * it, so an operator no longer has to open the Reporting Dashboard for them.
 */
describe('BookkeepingReportComponent transaction count and average sale (issue #804)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the completed transaction count and the average sale the API reported', async () => {
    const host = await render({ sales: 1000, transactionCount: 40, averageSale: 25 });

    expect(host.querySelector('[data-testid="transactions"]')!.textContent).toContain('40');
    expect(host.querySelector('[data-testid="average-sale"]')!.textContent).toContain('$25.00');
    expect(host.textContent).toContain('average sale');
  });

  it('shows a measured zero for a covered period that genuinely sold nothing', async () => {
    const host = await render({
      sales: 0, transactionCount: 0, averageSale: 0,
      providerAvailability: {
        sales: { state: 'Complete', asOfUtc: null, isComplete: true },
        fees: { state: 'Complete', asOfUtc: null, isComplete: true },
        combined: { state: 'Complete', asOfUtc: null, isComplete: true }
      }
    });

    expect(host.querySelector('[data-testid="transactions"]')!.textContent).toContain('0');
    expect(host.querySelector('[data-testid="average-sale"]')!.textContent).toContain('$0.00');
  });

  it('explains both figures in the calculation help rather than leaving them undefined', async () => {
    const host = await render();

    const help = host.querySelector('[data-testid="calculation-help"]')!.textContent ?? '';
    expect(help).toContain('transaction count');
    expect(help).toContain('average sale');
  });
});

/**
 * Issue #805: Bookkeeping compares the reimbursement its own card sales and fees expect against the
 * reimbursement the settlement import actually recorded, so an operator no longer has to open the
 * Reporting Dashboard to reconcile a month. The calculated expectation, the imported actual and the
 * established net reimbursement are three different figures and must read as three.
 */
describe('BookkeepingReportComponent expected-versus-actual reimbursement (issue #805)', () => {
  afterEach(() => TestBed.resetTestingModule());

  const COMPLETE: ReportProviderAvailability = {
    sales: { state: 'Complete', asOfUtc: null, isComplete: true },
    fees: { state: 'Complete', asOfUtc: null, isComplete: true },
    combined: { state: 'Complete', asOfUtc: null, isComplete: true }
  };

  it('shows the calculated expected reimbursement, the imported actual and the difference between them', async () => {
    const host = await render({
      cardSales: 100, nayaxFeesIncludingGst: 5, netSettlement: 94,
      expectedReimbursement: 95, actualReimbursement: 94, reimbursementDifference: -1,
      isReconciled: false, reconciliationStatus: 'Needs Review', reconciliationTolerance: 0.01,
      providerAvailability: COMPLETE
    });

    const section = host.querySelector('[data-testid="reimbursement-reconciliation"]')!;
    expect(section).not.toBeNull();
    expect(section.querySelector('[data-testid="expected-reimbursement"]')!.textContent).toContain('$95.00');
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).toContain('$94.00');
    expect(section.querySelector('[data-testid="reimbursement-difference"]')!.textContent).toContain('-$1.00');
    expect(section.querySelector('[data-testid="reconciliation-status"]')!.textContent).toContain('Needs Review');
    // The established net reimbursement figure keeps its own card and its own meaning.
    expect(host.querySelector('[data-testid="net-reimbursement"]')!.textContent).toContain('$94.00');
    expect(section.textContent).toContain('Expected');
    expect(section.textContent).toContain('Actual');
  });

  it('marks a reconciled period as reconciled', async () => {
    const host = await render({
      expectedReimbursement: 95, actualReimbursement: 95, reimbursementDifference: 0,
      isReconciled: true, reconciliationStatus: 'Reconciled', reconciliationTolerance: 0.01,
      providerAvailability: COMPLETE
    });

    const status = host.querySelector('[data-testid="reconciliation-status"]')!;
    expect(status.textContent).toContain('Reconciled');
    expect(status.classList.contains('badge-success')).toBe(true);
  });

  it('shows a pending comparison when no settlement import covers the period, never a difference', async () => {
    const host = await render({
      expectedReimbursement: 95, actualReimbursement: null, reimbursementDifference: null,
      isReconciled: false, reconciliationStatus: 'Pending', reconciliationTolerance: 0.01,
      providerAvailability: COMPLETE
    });

    const section = host.querySelector('[data-testid="reimbursement-reconciliation"]')!;
    expect(section.querySelector('[data-testid="reconciliation-status"]')!.textContent).toContain('Pending');
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).toContain('Not available');
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).not.toContain('$0.00');
    expect(section.querySelector('[data-testid="reimbursement-difference"]')!.textContent).not.toContain('$');
    expect(section.querySelector('[data-testid="expected-reimbursement"]')!.textContent).toContain('$95.00');
  });

  it('shows an imported zero as a measured zero for a covered period', async () => {
    const host = await render({
      expectedReimbursement: 0, actualReimbursement: 0, reimbursementDifference: 0,
      isReconciled: true, reconciliationStatus: 'Reconciled', reconciliationTolerance: 0.01,
      providerAvailability: COMPLETE
    });

    const section = host.querySelector('[data-testid="reimbursement-reconciliation"]')!;
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).toContain('$0.00');
    expect(section.querySelector('[data-testid="reconciliation-status"]')!.textContent).toContain('Reconciled');
  });

  it('shows the settlement availability state instead of an actual reimbursement nothing measured', async () => {
    const host = await render({
      expectedReimbursement: 95, actualReimbursement: 0, reimbursementDifference: -95,
      isReconciled: false, reconciliationStatus: 'Needs Review', reconciliationTolerance: 0.01,
      providerAvailability: {
        sales: { state: 'Complete', asOfUtc: null, isComplete: true },
        fees: { state: 'Missing', asOfUtc: null, isComplete: false },
        combined: { state: 'Missing', asOfUtc: null, isComplete: false }
      }
    });

    const section = host.querySelector('[data-testid="reimbursement-reconciliation"]')!;
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).toContain('No provider data');
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).not.toContain('$0.00');
  });

  it('states the tolerance the API reported rather than a hard-coded one', async () => {
    const host = await render({
      expectedReimbursement: 95, actualReimbursement: 95, reimbursementDifference: 0,
      isReconciled: true, reconciliationStatus: 'Reconciled', reconciliationTolerance: 0.05,
      providerAvailability: COMPLETE
    });

    expect(host.querySelector('[data-testid="reimbursement-reconciliation"]')!.textContent).toContain('$0.05');
  });

  it('explains the two sides in the calculation help', async () => {
    const host = await render();

    const help = host.querySelector('[data-testid="calculation-help"]')!.textContent ?? '';
    expect(help).toContain('expected reimbursement');
    expect(help).toContain('imported');
    // Issue #1253: the help says which payouts are counted and what happens when none are.
    expect(help).toContain('entirely inside');
    expect(help).toContain('not available');
  });

  /**
   * Issue #1253, the issue's own fixture: card sales of $100 a day for 1-7 September 2026 with one
   * payout for that week of $680.75, viewed for 7 September alone. The day has no actual
   * reimbursement, the net reimbursement beside it is labelled as an estimate, and the payout the
   * day cuts is listed on its own instead of being counted as money received that day.
   */
  it('reports no actual, an estimated net reimbursement and the payout the range cuts', async () => {
    const host = await render({
      cardSales: 100, nayaxFeesIncludingGst: 2.75, netSettlement: 97.25, isNetSettlementEstimated: true,
      expectedReimbursement: 97.25, actualReimbursement: null, reimbursementDifference: null,
      isReconciled: false, reconciliationStatus: 'Range cuts a payout period', reconciliationTolerance: 0.01,
      reimbursementPeriods: [],
      partlyCoveredReimbursements: [{ periodStart: '2026-09-01', periodEnd: '2026-09-07', total: 680.75 }],
      providerAvailability: COMPLETE
    });

    const section = host.querySelector('[data-testid="reimbursement-reconciliation"]')!;
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).toContain('Not available');
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).not.toContain('680.75');
    expect(section.querySelector('[data-testid="reimbursement-difference"]')!.textContent).not.toContain('$');
    expect(section.querySelector('[data-testid="reconciliation-status"]')!.textContent).toContain('Range cuts a payout period');
    expect(section.querySelector('[data-testid="reconciliation-status"]')!.textContent).not.toContain('Needs Review');
    // The expected figure for the day is unchanged and stays on its own line.
    expect(section.querySelector('[data-testid="expected-reimbursement"]')!.textContent).toContain('$97.25');

    const partly = section.querySelector('[data-testid="partly-covered-payouts"]')!;
    expect(partly.textContent).toContain('Payouts partly outside this range');
    expect(partly.textContent).toContain('01/09/2026');
    expect(partly.textContent).toContain('07/09/2026');
    expect(partly.textContent).toContain('$680.75');

    // The net reimbursement card keeps the estimate and says it is one.
    expect(host.querySelector('[data-testid="net-reimbursement"]')!.textContent).toContain('$97.25');
    expect(host.querySelector('[data-testid="net-reimbursement-estimated"]')!.textContent).toContain('Estimated');
  });

  /**
   * The same payout viewed for the week it settles: it lies wholly inside the range, so it is the
   * actual reimbursement, it is labelled with the period it covers, nothing is listed as partly
   * outside, and the net reimbursement is the imported amount rather than an estimate.
   */
  it('labels the actual reimbursement with the payout periods it is made of', async () => {
    const host = await render({
      cardSales: 700, nayaxFeesIncludingGst: 19.25, netSettlement: 680.75, isNetSettlementEstimated: false,
      expectedReimbursement: 680.75, actualReimbursement: 680.75, reimbursementDifference: 0,
      isReconciled: true, reconciliationStatus: 'Reconciled', reconciliationTolerance: 0.01,
      reimbursementPeriods: [{ periodStart: '2026-09-01', periodEnd: '2026-09-07', total: 680.75 }],
      partlyCoveredReimbursements: [],
      providerAvailability: COMPLETE
    });

    const section = host.querySelector('[data-testid="reimbursement-reconciliation"]')!;
    expect(section.querySelector('[data-testid="actual-reimbursement"]')!.textContent).toContain('$680.75');
    const periods = section.querySelector('[data-testid="reimbursement-periods"]')!;
    expect(periods.textContent).toContain('01/09/2026');
    expect(periods.textContent).toContain('07/09/2026');
    expect(section.querySelector('[data-testid="partly-covered-payouts"]')).toBeNull();
    expect(host.querySelector('[data-testid="net-reimbursement-estimated"]')).toBeNull();
  });

  it('builds the comparison from shared tokens the committed stylesheet already compiles', async () => {
    const host = await render({ reconciliationStatus: 'Reconciled', isReconciled: true, providerAvailability: COMPLETE });
    const css = readFileSync(join(__dirname, '..', '..', '..', 'styles.css'), 'utf8');

    const section = host.querySelector('[data-testid="reimbursement-reconciliation"]')!;
    const tokens = new Set<string>();
    for (const element of [section, ...Array.from(section.querySelectorAll('*'))]) {
      element.classList.forEach(token => tokens.add(token));
    }

    expect(tokens.size).toBeGreaterThan(0);
    for (const token of tokens) {
      // A responsive token's colon is escaped in the compiled stylesheet's own selector.
      expect(css).toContain(`.${token.replace(/:/g, '\\:')}`);
    }
  });
});

/**
 * Issue #1255: GST on sales follows each product's GST rule, so the GST card has to show which
 * sales the figure was taken from - and, as its own line, how much of the taxable total is taxable
 * only because nobody has given those products a rule.
 */
describe('BookkeepingReportComponent GST on sales by product rule (issue #1255)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the taxable, GST-free and no-rule sales the API decided, beside the GST figure', async () => {
    const host = await render({ sales: 36, taxableSales: 33, gstFreeSales: 3, noGstRuleSales: 22, gstOnSales: 3 });

    expect(host.querySelector('[data-testid="taxable-sales"]')!.textContent).toContain('$33.00');
    expect(host.querySelector('[data-testid="gst-free-sales"]')!.textContent).toContain('$3.00');
    expect(host.querySelector('[data-testid="no-gst-rule-sales"]')!.textContent).toContain('$22.00');
    expect(host.textContent).toContain('Sales of products with no GST rule');
  });

  it('shows the sales state instead of a zero nothing measured, as every other provider figure does', async () => {
    const host = await render({
      sales: 0, taxableSales: 0, gstFreeSales: 0, noGstRuleSales: 0, gstOnSales: 0,
      providerAvailability: {
        sales: { state: 'Missing', asOfUtc: null, isComplete: false },
        fees: { state: 'Complete', asOfUtc: null, isComplete: true },
        combined: { state: 'Missing', asOfUtc: null, isComplete: false }
      }
    });

    expect(host.querySelector('[data-testid="taxable-sales"]')!.textContent).not.toContain('$0.00');
    expect(host.querySelector('[data-testid="no-gst-rule-sales"]')!.textContent).not.toContain('$0.00');
  });

  it('states in the calculation help that the rule is read as it stands today, with no per-sale classification', async () => {
    const host = await render();

    const help = host.querySelector('[data-testid="calculation-help"]')!.textContent ?? '';
    expect(help).toContain("each product's own GST rule");
    expect(help).toContain('as it stands today');
    expect(help).toContain('no GST classification is recorded per sale');
    expect(help).not.toContain('assumed taxable at 10%');
  });
});

/**
 * Regression coverage for issue #453: this component shares the `.stat-card-head`/`.icon-tile`
 * arrangement validated structurally by `stat-card-icon-position.spec.ts`, so it only needs to
 * confirm this specific production template was updated to the same order.
 */
describe('BookkeepingReportComponent stat-card icon tile position (issue #453)', () => {
  it('renders the label/value content before the icon tile in every summary stat card, so the icon sits at the right', async () => {
    const route = {} as unknown as ActivatedRoute;
    const reports = {
      bookkeeping: () => of(REPORT),
      gstBasSummary: () => of({
        from: REPORT.from, to: REPORT.to, status: 'NotGstRegistered', figures: null,
        dataQuality: {
          missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
          commissionNotPersisted: false, containsUnmappedProducts: false, notes: []
        }
      })
    } as unknown as ReportingService;
    const machines = { getAll: () => of([]) } as unknown as MachineService;

    await TestBed.configureTestingModule({
      imports: [BookkeepingReportComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route },
        { provide: ReportingService, useValue: reports },
        { provide: MachineService, useValue: machines },
        provideNayaxConnectionStatus()
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(BookkeepingReportComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const heads = Array.from(host.querySelectorAll('.stat-card-head'));
    // Five since issue #804 added the Transactions card.
    expect(heads).toHaveLength(5);
    for (const head of heads) {
      const children = Array.from(head.children);
      expect(children[0].classList.contains('stat-card-content')).toBe(true);
      expect(children[children.length - 1].classList.contains('icon-tile')).toBe(true);
    }
  });
});

/**
 * Issue #1318: Bookkeeping composes the GST and BAS summary as a child component and hands it the
 * period it actually reported on. These tests are about that composition; what the summary itself
 * renders is `gst-bas-summary.component.spec.ts`.
 */
describe('BookkeepingReportComponent GST and BAS summary (issue #1318)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function renderWithSummary(status: 'Available' | 'NotGstRegistered' | 'MachineFiltered') {
    const gstBasSummary = jest.fn(() => of({
      from: '2026-01-01T00:00:00',
      to: '2026-01-31T00:00:00',
      status,
      figures: status === 'Available' ? {
        totalSales: 1000, gstOnSales: 90, gstCredits: 12, purchaseGstCredits: 8,
        operatingExpenseGstCredits: 2, nayaxFeeGstCredits: 2, netGst: 78, netGstOutcome: 'Payable',
        isIncomplete: false, gstFreeSales: 0, noGstRuleSales: 0,
        purchaseUnresolvedComponentCount: 0, purchaseUnresolvedAmount: 0,
        estimatedFeeGst: 0, feeGstIncludesEstimates: false,
        providerAvailability: {
          sales: { state: 'Complete', asOfUtc: null },
          fees: { state: 'Complete', asOfUtc: null },
          combined: { state: 'Complete', asOfUtc: null }
        },
        netGstAvailability: { state: 'Complete', asOfUtc: null }
      } : null,
      dataQuality: {
        missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
        commissionNotPersisted: false, containsUnmappedProducts: false, notes: []
      }
    }));
    const reports = { bookkeeping: () => of(REPORT), gstBasSummary } as unknown as ReportingService;

    await TestBed.configureTestingModule({
      imports: [BookkeepingReportComponent],
      providers: [
        { provide: ActivatedRoute, useValue: {} as unknown as ActivatedRoute },
        { provide: ReportingService, useValue: reports },
        { provide: MachineService, useValue: { getAll: () => of([]) } as unknown as MachineService },
        provideNayaxConnectionStatus()
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(BookkeepingReportComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, gstBasSummary };
  }

  it('shows the GST summary on the page for a GST-registered business', async () => {
    const { host } = await renderWithSummary('Available');

    expect(host.querySelector('[data-testid="gst-bas-summary"]')).not.toBeNull();
    expect(host.textContent).toContain('G1 — Total sales');
    expect(host.textContent).toContain('1A — GST on sales');
    expect(host.textContent).toContain('1B — GST credits');
  });

  it('hides the GST summary entirely when the business is not registered for GST', async () => {
    const { host } = await renderWithSummary('NotGstRegistered');

    expect(host.querySelector('[data-testid="gst-bas-summary"]')).toBeNull();
    expect(host.textContent).not.toContain('G1 — Total sales');
  });

  it('prompts for all machines instead of a machine-scoped estimate when a machine filter is active', async () => {
    const { host } = await renderWithSummary('MachineFiltered');

    expect(host.querySelector('[data-testid="gst-bas-summary"]')).toBeNull();
    expect(host.querySelector('[data-testid="gst-bas-summary-machine-filtered"]')?.textContent)
      .toContain('All machines');
  });

  /**
   * The repair of the review finding on this composition: the page and its summary must stay on the
   * same applied filter even when two report requests are in flight and the earlier one answers
   * last. `ReportPageBase` now cancels the request a new filter supersedes, so the figures can no
   * longer fall back to the period the summary beside them has already moved off.
   */
  it('keeps the figures and the GST summary on the same filter when responses finish out of order', async () => {
    const reportAnswers = new Map<number | null, Subject<BookkeepingReport>>();
    const bookkeeping = jest.fn((filter: ReportingFilter) => {
      const answer = new Subject<BookkeepingReport>();
      reportAnswers.set(filter.machineId ?? null, answer);
      return answer;
    });
    const summaryAnswers = new Map<number | null, Subject<unknown>>();
    const gstBasSummary = jest.fn((filter: ReportingFilter) => {
      const answer = new Subject<unknown>();
      summaryAnswers.set(filter.machineId ?? null, answer);
      return answer;
    });

    await TestBed.configureTestingModule({
      imports: [BookkeepingReportComponent],
      providers: [
        { provide: ActivatedRoute, useValue: {} as unknown as ActivatedRoute },
        { provide: ReportingService, useValue: { bookkeeping, gstBasSummary } as unknown as ReportingService },
        { provide: MachineService, useValue: { getAll: () => of([]) } as unknown as MachineService },
        provideNayaxConnectionStatus()
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(BookkeepingReportComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();

    // The whole-business request is still in flight when the reader narrows to one machine.
    component.machineId = 7;
    component.load();
    fixture.detectChanges();

    // The machine-scoped request answers first, and the summary it then asks for refuses that scope.
    reportAnswers.get(7)!.next({ ...REPORT, sales: 7000 });
    fixture.detectChanges();
    summaryAnswers.get(7)!.next({
      from: REPORT.from, to: REPORT.to, status: 'MachineFiltered', figures: null,
      dataQuality: {
        missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
        commissionNotPersisted: false, containsUnmappedProducts: false, notes: []
      }
    });
    fixture.detectChanges();

    // Only now does the superseded whole-business request answer. It was cancelled, so it reaches
    // nothing: it cannot put whole-business figures on screen beside a machine-scoped summary.
    expect(reportAnswers.get(null)!.observed).toBe(false);
    reportAnswers.get(null)!.next({ ...REPORT, sales: 1000 });
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(component.report!.sales).toBe(7000);
    expect(host.querySelector('[data-testid="gross-sales"]')!.textContent).toContain('$7,000.00');
    expect(component.gstSummaryFilter).toEqual({ from: component.from, to: component.to, machineId: 7 });
    expect(gstBasSummary).toHaveBeenCalledTimes(1);
    expect(gstBasSummary).toHaveBeenCalledWith({ from: component.from, to: component.to, machineId: 7 });
    expect(host.querySelector('[data-testid="gst-bas-summary-machine-filtered"]')).not.toBeNull();
  });

  it('asks for the summary with the period and scope the page itself reported on', async () => {
    const { fixture, gstBasSummary } = await renderWithSummary('Available');
    const component = fixture.componentInstance;

    component.from = '2026-02-01';
    component.to = '2026-02-28';
    component.machineId = 7;
    component.load();
    fixture.detectChanges();

    expect(gstBasSummary).toHaveBeenLastCalledWith({ from: '2026-02-01', to: '2026-02-28', machineId: 7 });
  });
});
