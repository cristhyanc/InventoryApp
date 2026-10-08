import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { ReconciliationReportComponent } from './reconciliation-report.component';
import { ReconciliationPeriod, ReconciliationReport, ReportingService } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';

function createComponent(): ReconciliationReportComponent {
  const route = {} as unknown as ActivatedRoute;
  const reports = {} as unknown as ReportingService;
  const machines = { getAll: () => of([]) } as unknown as MachineService;
  return new ReconciliationReportComponent(route, reports, machines);
}

describe('ReconciliationReportComponent statusClass (issue #419 Material restyle)', () => {
  it('maps Reconciled to the shared success badge class', () => {
    expect(createComponent().statusClass('Reconciled')).toBe('badge-success');
  });

  it('maps Mismatch to the shared danger badge class', () => {
    expect(createComponent().statusClass('Mismatch')).toBe('badge-danger');
  });

  it('maps any other status to the shared warning badge class', () => {
    expect(createComponent().statusClass('Pending')).toBe('badge-warning');
  });
});

const quality = {
  missingStatus: false, historicalCostUnavailable: true, gstClassificationMissing: false,
  commissionNotPersisted: true, containsUnmappedProducts: false, notes: [] as string[]
};

function period(overrides: Partial<ReconciliationPeriod> = {}): ReconciliationPeriod {
  return {
    from: '2026-06-01T00:00:00Z', to: '2026-06-10T00:00:00Z',
    totalVendingSales: 4231.55, cardSales: 3880.2, cashSales: 351.35,
    cardTransactionSales: 3880.2, nayaxReportedGrossCardSales: 3875.6,
    cardTransactionCount: 1284, nayaxReportedCardTransactionCount: 1283, countDifference: 1,
    totalTransactionCount: 1402, cashTransactionCount: 118,
    grossDifference: 4.6, grossStatus: 'Mismatch',
    processingFeesExGst: 112.34, feeGst: 11.23, otherFees: 8.5, adjustments: 0,
    adjustmentsSupported: false,
    expectedNetReimbursement: 3743.53, actualNetReimbursement: 3743.53,
    settlementDifference: 0, settlementStatus: 'Reconciled', status: 'Mismatch',
    payoutDate: '2026-06-18T00:00:00Z', dataQuality: { ...quality },
    ...overrides
  };
}

function report(periodRows: ReconciliationPeriod[]): ReconciliationReport {
  return {
    from: '2026-06-01', to: '2026-06-30', nayaxSales: 11640.6, importedReimbursement: 11626.8,
    difference: 13.8, tolerance: 0.01, isMatch: false, dataQuality: { ...quality },
    totalVendingSales: 12694.65, cardSales: 11640.6, cashSales: 1054.05,
    totalTransactionCount: 4206, cashTransactionCount: 354,
    cardTransactionSales: 11640.6, nayaxReportedGrossCardSales: 11626.8,
    nayaxReportedCardTransactionCount: 3849, grossDifference: 13.8, grossStatus: 'Mismatch',
    processingFeesExGst: 337.02, feeGst: 33.69, otherFees: 25.5, adjustments: 0,
    expectedNetReimbursement: 11230.59, actualNetReimbursement: 11230.59, settlementDifference: 0,
    adjustmentsSupported: false, settlementStatus: 'Reconciled', status: 'Mismatch',
    periodRows,
    totals: {
      totalVendingSales: 12694.65, cardSales: 11640.6, cashSales: 1054.05,
      cardTransactionSales: 11640.6, nayaxReportedGrossCardSales: 11626.8,
      cardTransactionCount: 3850, nayaxReportedCardTransactionCount: 3849, countDifference: 1,
      totalTransactionCount: 4206, cashTransactionCount: 354,
      grossDifference: 13.8, processingFeesExGst: 337.02, feeGst: 33.69, otherFees: 25.5,
      adjustments: 0, adjustmentsSupported: false,
      expectedNetReimbursement: 11230.59, actualNetReimbursement: 11230.59,
      settlementDifference: 0, grossStatus: 'Mismatch', settlementStatus: 'Reconciled', status: 'Mismatch'
    }
  };
}

async function render(periodRows: ReconciliationPeriod[] = [period()]) {
  await TestBed.configureTestingModule({
    imports: [ReconciliationReportComponent],
    providers: [
      { provide: ActivatedRoute, useValue: {} },
      { provide: ReportingService, useValue: { reconciliation: jest.fn(() => of(report(periodRows))) } },
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ReconciliationReportComponent);
  fixture.detectChanges();
  return { host: fixture.nativeElement as HTMLElement };
}

/**
 * Issue #452: the twelve-column period table overflowed the desktop content area. The fix is
 * layout-only, and jsdom cannot measure a layout, so these assertions pin the three decisions
 * that make the width budget work - the table card reclaims the duplicated page gutter, the
 * cells use the compact horizontal padding, and secondary text wraps instead of widening a
 * column - plus the invariant that no column, amount or status was dropped to achieve it.
 */
describe('ReconciliationReportComponent period table width (issue #452)', () => {
  afterEach(() => TestBed.resetTestingModule());

  function periodTable(host: HTMLElement): HTMLTableElement {
    const table = host.querySelector<HTMLTableElement>('.overflow-x-auto table');
    expect(table).not.toBeNull();
    return table!;
  }

  it('keeps the period table inside a contained horizontal scroll area', async () => {
    const { host } = await render();

    const scrollArea = host.querySelector('.overflow-x-auto');
    expect(scrollArea).not.toBeNull();
    expect(Array.from(scrollArea!.classList)).toEqual(expect.arrayContaining(['card', 'overflow-x-auto']));
    expect(scrollArea!.querySelector('table')).not.toBeNull();
  });

  it('reclaims the duplicated horizontal page gutter so the table uses the full main-content width', async () => {
    const { host } = await render();

    // The shell wrapper already supplies the #410 page gutter, and `.page` adds a second one.
    // The wide table card cancels that second gutter from the sm breakpoint upwards.
    expect(Array.from(host.querySelector('.overflow-x-auto')!.classList)).toContain('sm:-mx-6');
  });

  it('lets the table narrow onto the desktop content width while keeping a readable floor below it', async () => {
    const { host } = await render();

    const classes = Array.from(periodTable(host).classList);
    expect(classes).toEqual(expect.arrayContaining(['table', 'min-w-[1120px]', 'xl:min-w-0']));
  });

  it('uses compact horizontal cell padding on every period column', async () => {
    const { host } = await render();

    const cells = Array.from(periodTable(host).querySelectorAll<HTMLTableCellElement>('th, td'));
    expect(cells).toHaveLength(36);
    for (const cell of cells) {
      expect(Array.from(cell.classList)).toEqual(expect.arrayContaining(['table-cell', 'px-2']));
    }
  });

  it('wraps the secondary count and payout lines instead of letting one word widen a column', async () => {
    const { host } = await render();

    const secondaryLines = Array.from(periodTable(host).querySelectorAll<HTMLElement>('tbody .value-muted'));
    expect(secondaryLines.length).toBeGreaterThan(0);
    for (const line of secondaryLines) {
      expect(Array.from(line.classList)).toContain('break-words');
    }
  });

  it('still renders every column, amount, count and status of a period row', async () => {
    const { host } = await render([period()]);

    const headers = Array.from(periodTable(host).querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    expect(headers).toEqual([
      'Period', 'Vending sales', 'Card sales', 'Nayax gross card', 'Gross difference',
      'Fees (ex GST)', 'Fee GST', 'Other fees', 'Expected net', 'Actual net', 'Settlement', 'Status'
    ]);

    const row = periodTable(host).querySelector('tbody tr')!;
    const text = row.textContent ?? '';
    expect(text).toContain('01/06/2026');
    expect(text).toContain('10/06/2026');
    expect(text).toContain('Payout 18/06/2026');
    expect(text).toContain('$4,231.55');
    expect(text).toContain('$3,880.20');
    expect(text).toContain('$3,875.60');
    expect(text).toContain('$112.34');
    expect(text).toContain('$11.23');
    expect(text).toContain('$8.50');
    expect(text).toContain('$3,743.53');
    expect(text).toContain('1402 transactions');
    expect(text).toContain('118 cash');
    expect(text).toContain('1284 card transactions');
    expect(text).toContain('1283 transactions');
    expect(row.querySelectorAll('.badge')).toHaveLength(3);
  });

  it('keeps the totals row and its full financial values', async () => {
    const { host } = await render();

    const totals = periodTable(host).querySelector('tfoot tr')!;
    expect(totals.textContent).toContain('Total');
    expect(totals.textContent).toContain('$12,694.65');
    expect(totals.textContent).toContain('$11,640.60');
    expect(totals.textContent).toContain('$11,230.59');
    expect(totals.querySelectorAll('td')).toHaveLength(12);
  });
});
