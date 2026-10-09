import { readFileSync } from 'fs';
import { join } from 'path';
import { ActivatedRoute } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { BookkeepingReportComponent } from './bookkeeping-report.component';
import { ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { BookkeepingReport } from '../../features/reports/bookkeeping/models/bookkeeping-report.model';

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
  const reports = { bookkeeping: () => of({ ...REPORT, ...overrides }) } as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;

  await TestBed.configureTestingModule({
    imports: [BookkeepingReportComponent],
    providers: [
      { provide: ActivatedRoute, useValue: route },
      { provide: ReportingService, useValue: reports },
      { provide: MachineService, useValue: machines }
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

  it('keeps the GST-on-sales estimate visible beside the figure, not only inside the collapsed help', async () => {
    const host = await render();

    const estimate = host.querySelector('[data-testid="gst-on-sales-estimate"]');
    expect(estimate).not.toBeNull();
    expect(estimate!.closest('details')).toBeNull();
    expect(estimate!.textContent).toContain('Estimated GST on sales');
    expect(estimate!.textContent).toContain('taxable at 10% (GST-inclusive)');
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
 * Regression coverage for issue #453: this component shares the `.stat-card-head`/`.icon-tile`
 * arrangement validated structurally by `stat-card-icon-position.spec.ts`, so it only needs to
 * confirm this specific production template was updated to the same order.
 */
describe('BookkeepingReportComponent stat-card icon tile position (issue #453)', () => {
  it('renders the label/value content before the icon tile in every summary stat card, so the icon sits at the right', async () => {
    const route = {} as unknown as ActivatedRoute;
    const reports = { bookkeeping: () => of(REPORT) } as unknown as ReportingService;
    const machines = { getAll: () => of([]) } as unknown as MachineService;

    await TestBed.configureTestingModule({
      imports: [BookkeepingReportComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route },
        { provide: ReportingService, useValue: reports },
        { provide: MachineService, useValue: machines }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(BookkeepingReportComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const heads = Array.from(host.querySelectorAll('.stat-card-head'));
    expect(heads).toHaveLength(4);
    for (const head of heads) {
      const children = Array.from(head.children);
      expect(children[0].classList.contains('stat-card-content')).toBe(true);
      expect(children[children.length - 1].classList.contains('icon-tile')).toBe(true);
    }
  });
});
