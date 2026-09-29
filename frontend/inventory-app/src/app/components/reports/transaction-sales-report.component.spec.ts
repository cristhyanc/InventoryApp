import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { TransactionSalesReportComponent } from './transaction-sales-report.component';
import { ReportingService, TransactionSalesReport, TransactionSalesRow } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';

function row(overrides: Partial<TransactionSalesRow> = {}): TransactionSalesRow {
  return {
    transactionDate: '2026-06-15T00:00:00Z',
    transactionId: 1,
    machineId: 10,
    machineName: 'Machine A',
    siteName: 'Site A',
    productName: 'Coke',
    paymentType: 'card',
    rawPaymentMethod: 'Card',
    sale: 2.5,
    costingStatus: 'Costed',
    costSource: 'Weighted average',
    feeSource: 'Estimated',
    transactionStatusId: 1,
    transactionStatus: 'Completed',
    isCompleted: true,
    ...overrides
  };
}

function report(rows: TransactionSalesRow[]): TransactionSalesReport {
  return {
    from: '2026-06-01', to: '2026-06-30', rows,
    totals: {
      transactionCount: rows.length, completedTransactionCount: rows.length, sales: 0, cardSales: 0, cashSales: 0,
      costedCompletedTransactionCount: rows.length, uncostedCompletedTransactionCount: 0, isCogsComplete: true,
      partialCostOfGoods: 0, estimatedFeeExGst: 0, estimatedFeeGst: 0, estimatedFeeIncGst: 0, commissionAmount: 0
    },
    dataQuality: {
      missingStatus: false, historicalCostUnavailable: false, gstClassificationMissing: false,
      commissionNotPersisted: false, containsUnmappedProducts: false
    },
    page: 1, pageSize: 50, totalCount: rows.length,
    filterOptions: { sites: [], products: [] }
  };
}

async function render(rows: TransactionSalesRow[]) {
  await TestBed.configureTestingModule({
    imports: [TransactionSalesReportComponent],
    providers: [
      { provide: ReportingService, useValue: { transactions: jest.fn(() => of(report(rows))) } },
      { provide: MachineService, useValue: { getAll: jest.fn(() => of([])) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(TransactionSalesReportComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  return { host };
}

describe('TransactionSalesReportComponent instant timestamp display (issue #232)', () => {
  it('renders the transaction date/time as Australia/Canberra local time during AEST (UTC+10), not raw UTC', async () => {
    const { host } = await render([row({ transactionDate: '2026-06-15T00:00:00Z' })]);

    const cell = host.querySelector('tbody tr td');
    expect(cell?.textContent).toContain('15/06/2026, 10:00 am');
  });

  it('renders the transaction date/time as Australia/Canberra local time during AEDT (UTC+11), not raw UTC', async () => {
    const { host } = await render([row({ transactionDate: '2026-01-15T00:00:00Z' })]);

    const cell = host.querySelector('tbody tr td');
    expect(cell?.textContent).toContain('15/01/2026, 11:00 am');
  });
});
