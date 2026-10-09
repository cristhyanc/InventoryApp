import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NayaxSaleTimestampRepairPreviewComponent } from './nayax-sale-timestamp-repair-preview.component';
import {
  NayaxSaleTimestampEvidenceSource,
  NayaxSaleTimestampRepairOutcome,
  NayaxSaleTimestampRepairPreview,
  NayaxSaleTimestampReconciliation,
  NayaxSaleTimestampUnresolvedReason
} from '../../../services/nayax-sale-timestamp-repair.service';

function reconciliation(
  overrides: Partial<NayaxSaleTimestampReconciliation> = {}
): NayaxSaleTimestampReconciliation {
  return {
    cutoffUtc: '2026-10-08T04:00:00Z',
    fromBusinessDate: '2026-10-05T00:00:00',
    toBusinessDate: '2026-10-08T00:00:00',
    days: [
      {
        businessDate: '2026-10-05T00:00:00',
        completedCountBefore: 10,
        completedSalesBefore: 108.2,
        completedCountAfter: 11,
        completedSalesAfter: 120.4,
        unresolvedCount: 0,
        unresolvedAmount: 0,
        sourceVerifiedCountAfter: 11,
        sourceVerifiedAfter: 120.4
      }
    ],
    completedCountBefore: 60,
    totalBefore: 726.2,
    completedCountAfter: 58,
    totalAfter: 726.2,
    sourceVerifiedCountAfter: 40,
    sourceVerifiedTotalAfter: 535.3,
    excludedAfterCutoffCount: 1,
    excludedAfterCutoffAmount: 4.5,
    unresolvedCompletedCount: 12,
    unresolvedCompletedAmount: 190.9,
    missingFromDatabaseCount: 8,
    missingFromDatabaseAmount: 119.7,
    ...overrides
  };
}

function preview(overrides: Partial<NayaxSaleTimestampRepairPreview> = {}): NayaxSaleTimestampRepairPreview {
  return {
    previewId: '6f1b2c4e-0000-4000-8000-000000000001',
    evidenceRecords: 152,
    examinedFromUtc: '2026-10-04T13:00:00Z',
    examinedToUtc: '2026-10-08T04:00:00Z',
    salesExamined: 3,
    repairable: 2,
    alreadyCorrect: 1,
    unresolved: 0,
    rows: [
      {
        transactionId: 2108816976,
        machineId: 942488501,
        machineName: 'Sample Machine',
        settlementValue: 4.5,
        transactionStatusId: 12,
        completedSale: true,
        storedInstantUtc: '2026-10-06T23:14:00Z',
        storedBusinessDate: '2026-10-07T00:00:00',
        repairedInstantUtc: '2026-10-06T12:14:00Z',
        repairedBusinessDate: '2026-10-06T00:00:00',
        outcome: NayaxSaleTimestampRepairOutcome.Repairable,
        unresolvedReason: null,
        evidenceSource: NayaxSaleTimestampEvidenceSource.OperatorExport,
        evidenceReference: 'operator export week.csv'
      }
    ],
    revenueMovement: [
      { businessDate: '2026-10-07T00:00:00', amountLeaving: 12.2, amountArriving: 0, netMovement: -12.2 },
      { businessDate: '2026-10-06T00:00:00', amountLeaving: 0, amountArriving: 12.2, netMovement: 12.2 }
    ],
    affectedProducts: [
      {
        productId: 7,
        productName: 'Coke Zero',
        rebuildFromUtc: '2026-10-06T12:14:00Z',
        hasTransitionBaseline: true,
        transitionCutoffAt: '2026-01-01T13:00:00Z',
        rebuildPlanned: true
      },
      {
        productId: 9,
        productName: 'Water 600ml',
        rebuildFromUtc: '2026-10-06T12:14:00Z',
        hasTransitionBaseline: false,
        transitionCutoffAt: null,
        rebuildPlanned: false
      }
    ],
    missingFromDatabase: [],
    reconciliation: null,
    createdAt: '2026-10-08T05:00:00Z',
    expiresAt: '2026-10-08T07:00:00Z',
    ...overrides
  };
}

async function render(plan: NayaxSaleTimestampRepairPreview): Promise<{
  fixture: ComponentFixture<NayaxSaleTimestampRepairPreviewComponent>;
  host: HTMLElement;
}> {
  // Reset first, so a test that renders two plans in a row configures a fresh module each time.
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({ imports: [NayaxSaleTimestampRepairPreviewComponent] }).compileComponents();
  const fixture = TestBed.createComponent(NayaxSaleTimestampRepairPreviewComponent);
  fixture.componentRef.setInput('preview', plan);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function text(host: HTMLElement, testId: string): string {
  return host.querySelector(`[data-testid="${testId}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('NayaxSaleTimestampRepairPreviewComponent outcome categories (issue #487)', () => {
  it('shows the three outcome counts the server reported', async () => {
    const { host } = await render(preview({ repairable: 2, alreadyCorrect: 1, unresolved: 4 }));

    expect(text(host, 'repair-count-repairable')).toContain('2');
    expect(text(host, 'repair-count-already-correct')).toContain('1');
    expect(text(host, 'repair-count-unresolved')).toContain('4');
  });

  /** Unresolved rows must never be disguised as a complete repair. */
  it('warns that unresolved sales stay as they are and that applying is not a complete repair', async () => {
    const { host } = await render(preview({ unresolved: 4 }));

    expect(text(host, 'repair-unresolved-warning')).toContain('4 examined sale(s) stay exactly as they are');
    expect(text(host, 'repair-unresolved-warning')).toContain('not a complete repair');
  });

  it('reports a no-change plan as such, and says a completed repair previews the same way', async () => {
    const { host } = await render(preview({ repairable: 0 }));

    expect(text(host, 'repair-no-change')).toContain('No stored sale would change');
    expect(text(host, 'repair-no-change')).toContain('previewed again');
  });

  it('shows the examined range the server derived, and says so when there is none', async () => {
    const withRange = await render(preview());
    expect(withRange.host.textContent).toContain('Examined range (UTC)');

    const withoutRange = await render(preview({ examinedFromUtc: null, examinedToUtc: null }));
    expect(withoutRange.host.textContent?.replace(/\s+/g, ' ')).toContain('No range');
  });
});

describe('NayaxSaleTimestampRepairPreviewComponent revenue and costing (issue #487)', () => {
  it('shows what each business day loses and gains, with the server values', async () => {
    const { host } = await render(preview());
    const rows = Array.from(host.querySelectorAll('[data-testid="repair-revenue-movement"] tbody tr')).map((row) =>
      Array.from(row.querySelectorAll('td')).map((cell) => cell.textContent?.trim() ?? '')
    );

    expect(rows).toHaveLength(2);
    expect(rows[0][0]).toBe('07/10/2026');
    expect(rows[0][1]).toContain('12.20');
    expect(rows[1][0]).toBe('06/10/2026');
    expect(rows[1][2]).toContain('12.20');
  });

  it('says so when no completed sale changes its business date', async () => {
    const { host } = await render(preview({ revenueMovement: [] }));

    expect(text(host, 'repair-revenue-movement-empty')).toContain("no day's revenue moves");
  });

  /**
   * An affected product is not a rebuilt one: the server's baseline-cutoff gate decides, and the
   * page must not imply every affected product replays.
   */
  it('distinguishes an affected product from one whose rebuild is planned', async () => {
    const { host } = await render(preview());
    const rows = Array.from(host.querySelectorAll('[data-testid="repair-affected-products"] tbody tr')).map((row) =>
      Array.from(row.querySelectorAll('td')).map((cell) => cell.textContent?.replace(/\s+/g, ' ').trim() ?? '')
    );

    expect(rows[0][3]).toBe('Yes');
    expect(rows[1][2]).toContain('None recorded');
    expect(rows[1][3]).toContain('No - no baseline recorded');
    expect(text(host, 'repair-rebuild-summary')).toContain('1 of 2 affected product(s) would be replayed');
    expect(host.textContent?.replace(/\s+/g, ' ')).toContain('An affected product is not necessarily a rebuilt one');
  });

  it('explains a product the baseline already covers', async () => {
    const { host } = await render(
      preview({
        affectedProducts: [
          {
            productId: 7,
            productName: 'Coke Zero',
            rebuildFromUtc: '2025-06-01T12:00:00Z',
            hasTransitionBaseline: true,
            transitionCutoffAt: '2026-01-01T13:00:00Z',
            rebuildPlanned: false
          }
        ]
      })
    );
    const cells = Array.from(host.querySelectorAll('[data-testid="repair-affected-products"] tbody td')).map(
      (cell) => cell.textContent?.replace(/\s+/g, ' ').trim() ?? ''
    );

    expect(cells[3]).toContain('No - covered by its baseline');
  });

  it('says so when no product would be replayed at all', async () => {
    const { host } = await render(preview({ affectedProducts: [] }));

    expect(text(host, 'repair-affected-products-empty')).toContain("No product's costing would be replayed");
  });
});

describe('NayaxSaleTimestampRepairPreviewComponent missing sales (issue #487)', () => {
  /** A missing sale is not a timestamp defect, and this page offers no way to import one. */
  it('presents missing sales separately, with import guidance and no import action', async () => {
    const { host } = await render(
      preview({
        missingFromDatabase: [
          {
            transactionId: 2108816999,
            machineId: 942488501,
            authorizationInstantUtc: '2026-10-06T12:00:00Z',
            authorizationBusinessDate: '2026-10-06T00:00:00',
            settlementValue: 3.5,
            transactionStatusId: 12,
            completedSale: true,
            evidenceSource: NayaxSaleTimestampEvidenceSource.OperatorExport
          }
        ]
      })
    );

    expect(text(host, 'repair-missing-warning')).toContain('missing sales, not timestamp defects');
    expect(text(host, 'repair-missing-warning')).toContain('neither imports nor creates them');
    expect(host.querySelector('[data-testid="repair-missing-sales"] tbody tr')).not.toBeNull();
    expect(host.querySelectorAll('button')).toHaveLength(0);
  });

  it('shows an unreadable authorization value as unreadable rather than as a date', async () => {
    const { host } = await render(
      preview({
        missingFromDatabase: [
          {
            transactionId: 2108816999,
            machineId: 942488501,
            authorizationInstantUtc: null,
            authorizationBusinessDate: null,
            settlementValue: 3.5,
            transactionStatusId: null,
            completedSale: false,
            evidenceSource: NayaxSaleTimestampEvidenceSource.NayaxLastSalesApi
          }
        ]
      })
    );
    const cells = Array.from(host.querySelectorAll('[data-testid="repair-missing-sales"] tbody td')).map(
      (cell) => cell.textContent?.replace(/\s+/g, ' ').trim() ?? ''
    );

    expect(cells[1]).toContain('no status - not a completed sale');
    expect(cells[2]).toBe('Unreadable');
    expect(cells[3]).toBe('Unknown');
    expect(cells[4]).toBe('Nayax last-sales API');
  });

  it('says so when every evidence transaction already has a stored sale', async () => {
    const { host } = await render(preview());

    expect(text(host, 'repair-missing-sales-empty')).toContain('already has a stored sale');
  });
});

describe('NayaxSaleTimestampRepairPreviewComponent reconciliation (issue #487)', () => {
  it('shows the API before/after, source-verified, unresolved, missing and excluded figures', async () => {
    const { host } = await render(preview({ reconciliation: reconciliation() }));
    const block = text(host, 'repair-reconciliation');

    expect(block).toContain('726.20');
    expect(block).toContain('535.30');
    expect(block).toContain('190.90');
    expect(block).toContain('119.70');
    expect(block).toContain('4.50');
    expect(block).toContain('05/10/2026');
    expect(block).toContain('08/10/2026');
  });

  /** `totalAfter` is never called reconciled while a source gap remains. */
  it('refuses to call the window reconciled while unresolved or missing sales remain', async () => {
    const { host } = await render(preview({ reconciliation: reconciliation() }));

    expect(text(host, 'repair-reconciliation-incomplete')).toContain('not fully source-verified');
    expect(text(host, 'repair-reconciliation-incomplete')).toContain('source-verified after');
    expect(host.querySelector('[data-testid="repair-reconciliation-complete"]')).toBeNull();
  });

  it.each([
    ['unresolved completed sales', { unresolvedCompletedCount: 3, missingFromDatabaseCount: 0 }],
    ['missing sales', { unresolvedCompletedCount: 0, missingFromDatabaseCount: 2 }]
  ])('treats %s on their own as a source gap', async (_label, overrides) => {
    const { host } = await render(preview({ reconciliation: reconciliation(overrides) }));

    expect(host.querySelector('[data-testid="repair-reconciliation-incomplete"]')).not.toBeNull();
  });

  it('reports a fully source-verified window only when no gap remains', async () => {
    const { host } = await render(
      preview({
        reconciliation: reconciliation({
          unresolvedCompletedCount: 0,
          unresolvedCompletedAmount: 0,
          missingFromDatabaseCount: 0,
          missingFromDatabaseAmount: 0
        })
      })
    );

    expect(text(host, 'repair-reconciliation-complete')).toContain('covered by a source');
    expect(host.querySelector('[data-testid="repair-reconciliation-incomplete"]')).toBeNull();
  });

  it('explains that a sale authorized after the cutoff is not a discrepancy', async () => {
    const { host } = await render(preview({ reconciliation: reconciliation() }));

    expect(text(host, 'repair-reconciliation')).toContain('not a discrepancy');
  });

  it('shows each business day before, after, unresolved and source-verified', async () => {
    const { host } = await render(preview({ reconciliation: reconciliation() }));
    const cells = Array.from(host.querySelectorAll('[data-testid="repair-reconciliation-days"] tbody td')).map(
      (cell) => cell.textContent?.replace(/\s+/g, ' ').trim() ?? ''
    );

    expect(cells[0]).toBe('05/10/2026');
    expect(cells[1]).toContain('108.20');
    expect(cells[2]).toContain('120.40');
    expect(cells[4]).toContain('120.40');
  });

  it('renders no reconciliation section when none was requested', async () => {
    const { host } = await render(preview());

    expect(host.querySelector('[data-testid="repair-reconciliation"]')).toBeNull();
  });
});

describe('NayaxSaleTimestampRepairPreviewComponent authority (issue #487)', () => {
  /** The preview is a review surface: it must offer no mutating action of its own. */
  it('offers no action that could write anything', async () => {
    const { host } = await render(
      preview({
        reconciliation: reconciliation(),
        missingFromDatabase: [
          {
            transactionId: 2108816999,
            machineId: 942488501,
            authorizationInstantUtc: '2026-10-06T12:00:00Z',
            authorizationBusinessDate: '2026-10-06T00:00:00',
            settlementValue: 3.5,
            transactionStatusId: 12,
            completedSale: true,
            evidenceSource: NayaxSaleTimestampEvidenceSource.OperatorExport
          }
        ],
        rows: [
          {
            transactionId: 1,
            machineId: 2,
            machineName: 'M',
            settlementValue: 1,
            transactionStatusId: 12,
            completedSale: true,
            storedInstantUtc: '2026-10-06T23:14:00Z',
            storedBusinessDate: '2026-10-07T00:00:00',
            repairedInstantUtc: null,
            repairedBusinessDate: null,
            outcome: NayaxSaleTimestampRepairOutcome.Unresolved,
            unresolvedReason: NayaxSaleTimestampUnresolvedReason.NoSourceEvidence,
            evidenceSource: null,
            evidenceReference: null
          }
        ]
      })
    );

    expect(host.textContent).toContain('nothing has been repaired yet');
    expect(host.querySelectorAll('button')).toHaveLength(0);
  });
});
