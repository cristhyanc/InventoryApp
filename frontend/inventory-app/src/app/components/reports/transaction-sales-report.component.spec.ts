import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { TransactionSalesReportComponent } from './transaction-sales-report.component';
import { ReportingService, TransactionSalesReport, TransactionSalesRow } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';

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

  // The business time zone the shell loads at sign-in (issue #499). Every instant the report
  // renders is shown in it, and the application's existing business is in Sydney.
  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(TransactionSalesReportComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  return { host };
}

/** The long-text case of issue #452: no word in it offers a break opportunity. */
const longTextRow = row({
  transactionId: 884512339,
  machineName: 'Tuggeranong-Hyperdome-Entrance-Vending-Machine-Seventeen',
  siteName: 'TuggeranongHyperdomeShoppingCentreMainConcourseNorth',
  productName: 'ExtraordinarilyLongConfectioneryProductDescription',
  rawPaymentMethod: 'MastercardContactlessDebitDomestic',
  transactionStatus: 'Cancelled / declined'
});

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

/**
 * Issue #452: the transaction table forced every header and the whole timestamp onto one line and
 * paid the full #410 cell padding, which pushed it past the desktop content width. jsdom cannot
 * measure a layout, so these assertions pin the decisions behind the width budget - the card
 * reclaims the duplicated page gutter, the cells use compact padding, headers and long
 * descriptive text wrap - plus the invariant that no column or value was dropped for it.
 */
describe('TransactionSalesReportComponent table width (issue #452)', () => {
  afterEach(() => TestBed.resetTestingModule());

  function transactionTable(host: HTMLElement): HTMLTableElement {
    const table = host.querySelector<HTMLTableElement>('.overflow-x-auto table');
    expect(table).not.toBeNull();
    return table!;
  }

  it('keeps the table in a contained scroll area that reclaims the duplicated horizontal page gutter', async () => {
    const { host } = await render([row()]);

    const scrollArea = host.querySelector('.overflow-x-auto');
    expect(scrollArea).not.toBeNull();
    expect(Array.from(scrollArea!.classList)).toEqual(expect.arrayContaining(['card', 'overflow-x-auto', 'sm:-mx-6']));
    expect(scrollArea!.querySelector('table')).not.toBeNull();
  });

  it('keeps a readable minimum table width for the narrow-viewport scroll fallback', async () => {
    const { host } = await render([row()]);

    expect(Array.from(transactionTable(host).classList)).toEqual(expect.arrayContaining(['table', 'min-w-[900px]']));
  });

  it('lets every column header wrap instead of forcing the header row onto one line', async () => {
    const { host } = await render([row()]);

    const headers = Array.from(transactionTable(host).querySelectorAll<HTMLTableCellElement>('thead th'));
    expect(headers).toHaveLength(10);
    for (const header of headers) {
      expect(Array.from(header.classList)).toEqual(expect.arrayContaining(['table-cell', 'px-2']));
      expect(Array.from(header.classList)).not.toContain('whitespace-nowrap');
    }
    expect(headers.map((header) => (header.textContent ?? '').replace(/[↑↓]/g, '').trim())).toEqual([
      'Date / time', 'Machine / site', 'Product', 'Payment', 'Sale', 'COGS',
      'Gross Profit', 'Direct Profit', 'Status', 'Fees / commission'
    ]);
  });

  it('lets the date/time cell wrap between the date and the time while still showing both', async () => {
    const { host } = await render([row({ transactionDate: '2026-06-15T00:00:00Z' })]);

    const dateCell = transactionTable(host).querySelector<HTMLTableCellElement>('tbody td')!;
    expect(Array.from(dateCell.classList)).not.toContain('whitespace-nowrap');
    expect(dateCell.textContent).toContain('15/06/2026, 10:00 am');
    expect(dateCell.textContent).toContain('#1');
  });

  it('uses compact horizontal cell padding on every body cell', async () => {
    const { host } = await render([row()]);

    const cells = Array.from(transactionTable(host).querySelectorAll<HTMLTableCellElement>('tbody td'));
    expect(cells).toHaveLength(10);
    for (const cell of cells) {
      expect(Array.from(cell.classList)).toEqual(expect.arrayContaining(['table-cell', 'px-2']));
    }
  });

  it('breaks unbroken descriptive content instead of widening the table for it', async () => {
    const { host } = await render([longTextRow]);

    const cells = Array.from(transactionTable(host).querySelectorAll<HTMLTableCellElement>('tbody td'));
    // Machine / site, Product, Payment and Status carry free text; the money columns must not
    // break, so they keep their secondary lines breakable instead of the whole cell.
    for (const index of [1, 2, 3, 8]) {
      expect(Array.from(cells[index].classList)).toContain('break-words');
    }
    for (const index of [4, 5, 6, 7, 9]) {
      expect(Array.from(cells[index].classList)).not.toContain('break-words');
    }
    const secondaryLines = Array.from(transactionTable(host).querySelectorAll<HTMLElement>('tbody .value-muted'));
    expect(secondaryLines.length).toBeGreaterThan(0);
    for (const line of secondaryLines) {
      expect(Array.from(line.classList)).toContain('break-words');
    }
  });

  it('keeps every value of a long-text row readable and complete', async () => {
    const { host } = await render([longTextRow]);

    const text = transactionTable(host).querySelector('tbody tr')?.textContent ?? '';
    expect(text).toContain('Tuggeranong-Hyperdome-Entrance-Vending-Machine-Seventeen');
    expect(text).toContain('TuggeranongHyperdomeShoppingCentreMainConcourseNorth');
    expect(text).toContain('ExtraordinarilyLongConfectioneryProductDescription');
    expect(text).toContain('MastercardContactlessDebitDomestic');
    expect(text).toContain('Cancelled / declined');
    expect(text).toContain('$2.50');
  });

  it('keeps the sort controls, pagination and page-size control available', async () => {
    const { host } = await render([row()]);

    const sortButtons = Array.from(transactionTable(host).querySelectorAll('thead button'));
    expect(sortButtons.map((button) => (button.textContent ?? '').replace(/[↑↓]/g, '').trim())).toEqual([
      'Date / time', 'Machine / site', 'Product', 'Sale', 'COGS', 'Gross Profit', 'Direct Profit', 'Status'
    ]);

    const pager = host.querySelectorAll('button');
    expect(Array.from(pager).some((button) => button.textContent?.trim() === 'Previous')).toBe(true);
    expect(Array.from(pager).some((button) => button.textContent?.trim() === 'Next')).toBe(true);
    const pageSizes = Array.from(host.querySelectorAll('select'))
      .find((select) => Array.from(select.options).some((option) => option.textContent?.trim() === '250'));
    expect(pageSizes).toBeDefined();
  });
});
