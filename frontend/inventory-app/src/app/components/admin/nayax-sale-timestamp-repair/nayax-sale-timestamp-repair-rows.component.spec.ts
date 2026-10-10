import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NayaxSaleTimestampRepairRowsComponent } from './nayax-sale-timestamp-repair-rows.component';
import {
  NayaxSaleTimestampEvidenceSource,
  NayaxSaleTimestampRepairOutcome,
  NayaxSaleTimestampRepairRow,
  NayaxSaleTimestampUnresolvedReason
} from '../../../services/nayax-sale-timestamp-repair.service';
import { BusinessTimeZoneService } from '../../../formatting/business-time-zone.service';

function row(overrides: Partial<NayaxSaleTimestampRepairRow> = {}): NayaxSaleTimestampRepairRow {
  return {
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
    evidenceReference: 'operator export week.csv',
    ...overrides
  };
}

async function render(rows: NayaxSaleTimestampRepairRow[]): Promise<{
  fixture: ComponentFixture<NayaxSaleTimestampRepairRowsComponent>;
  component: NayaxSaleTimestampRepairRowsComponent;
  host: HTMLElement;
}> {
  await TestBed.configureTestingModule({ imports: [NayaxSaleTimestampRepairRowsComponent] }).compileComponents();
  // The business time zone the shell loads at sign-in (issue #499); each row's instants are
  // rendered in it, and the application's existing business is in Sydney.
  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');
  const fixture = TestBed.createComponent(NayaxSaleTimestampRepairRowsComponent);
  fixture.componentRef.setInput('rows', rows);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

function bodyRows(host: HTMLElement): string[][] {
  return Array.from(host.querySelectorAll('tbody tr')).map((tableRow) =>
    Array.from(tableRow.querySelectorAll('td')).map((cell) => cell.textContent?.replace(/\s+/g, ' ').trim() ?? '')
  );
}

describe('NayaxSaleTimestampRepairRowsComponent (issue #487)', () => {
  it('lists each examined sale with its stored and repaired instants, dates, outcome and source', async () => {
    const { host } = await render([row()]);
    const cells = bodyRows(host)[0];

    expect(cells[0]).toContain('#2108816976');
    expect(cells[0]).toContain('Sample Machine (942488501)');
    expect(cells[1]).toContain('4.50');
    expect(cells[1]).toContain('status 12 - completed sale');
    expect(cells[4]).toBe('07/10/2026');
    expect(cells[5]).toBe('06/10/2026');
    expect(cells[6]).toBe('Repairable');
    expect(cells[7]).toContain('Operator export');
  });

  /**
   * A pending, refunded, cancelled or unknown-status row is re-dated too and must stay visible and
   * visibly not a completed sale: only a completed sale moves revenue between business days.
   */
  it('shows a non-completed sale as such rather than hiding or reclassifying it', async () => {
    const { host } = await render([row({ transactionStatusId: 55, completedSale: false })]);

    expect(bodyRows(host)[0][1]).toContain('status 55 - not a completed sale');
  });

  it('shows a null status explicitly instead of inventing one', async () => {
    const { host } = await render([row({ transactionStatusId: null, completedSale: false })]);

    expect(bodyRows(host)[0][1]).toContain('no status - not a completed sale');
  });

  /** An unresolved row keeps its stored values and states the server's refusal to guess. */
  it('states the unresolved reason and leaves the repaired columns unchanged', async () => {
    const { host } = await render([
      row({
        outcome: NayaxSaleTimestampRepairOutcome.Unresolved,
        unresolvedReason: NayaxSaleTimestampUnresolvedReason.NoSourceEvidence,
        repairedInstantUtc: null,
        repairedBusinessDate: null,
        evidenceSource: null,
        evidenceReference: null
      })
    ]);
    const cells = bodyRows(host)[0];

    expect(cells[3]).toBe('Unchanged');
    expect(cells[5]).toBe('Unchanged');
    expect(cells[6]).toBe('Unresolved');
    expect(cells[7]).toContain('No source');
    expect(cells[7]).toContain('older than the rolling API window');
  });

  it.each([
    [NayaxSaleTimestampUnresolvedReason.UnreadableEvidence, 'could not be read as an instant'],
    [NayaxSaleTimestampUnresolvedReason.ConflictingEvidence, 'different authorization instants'],
    [NayaxSaleTimestampUnresolvedReason.MachineMismatch, 'another machine'],
    [NayaxSaleTimestampUnresolvedReason.AmountMismatch, 'settled amount']
  ])('explains unresolved reason %i', async (reason, expected) => {
    const { host } = await render([
      row({ outcome: NayaxSaleTimestampRepairOutcome.Unresolved, unresolvedReason: reason })
    ]);

    expect(bodyRows(host)[0][7]).toContain(expected);
  });

  /**
   * Narrowing the listing must never read as narrowing the repair: the API has no selective-row
   * repair, and Apply confirms the server's whole plan.
   */
  it('says that filtering and paging do not exclude a row from Apply', async () => {
    const { host } = await render([row()]);
    const text = host.textContent?.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain("Apply confirms the server's whole plan");
    expect(text).toContain('not currently on screen');
  });

  it('filters by outcome without changing the examined total it reports', async () => {
    const { component, fixture, host } = await render([
      row({ transactionId: 1 }),
      row({ transactionId: 2, outcome: NayaxSaleTimestampRepairOutcome.Unresolved }),
      row({ transactionId: 3, outcome: NayaxSaleTimestampRepairOutcome.AlreadyCorrect })
    ]);

    component.outcomeFilter = 'unresolved';
    component.resetPage();
    fixture.detectChanges();

    expect(bodyRows(host)).toHaveLength(1);
    expect(bodyRows(host)[0][0]).toContain('#2');
    expect(host.querySelector('[data-testid="repair-rows-range"]')?.textContent?.replace(/\s+/g, ' ')).toContain(
      'of 3 examined'
    );
  });

  it('searches by transaction id, machine id and machine name', async () => {
    const { component, fixture, host } = await render([
      row({ transactionId: 11, machineId: 100, machineName: 'Alpha' }),
      row({ transactionId: 22, machineId: 200, machineName: 'Beta' })
    ]);

    component.search = 'beta';
    component.resetPage();
    fixture.detectChanges();
    expect(bodyRows(host)).toHaveLength(1);

    component.search = '100';
    component.resetPage();
    fixture.detectChanges();
    expect(bodyRows(host)[0][0]).toContain('#11');

    component.search = '22';
    component.resetPage();
    fixture.detectChanges();
    expect(bodyRows(host)[0][0]).toContain('#22');
  });

  it('reports a filter that matches nothing without implying the rows are gone', async () => {
    const { component, fixture, host } = await render([row()]);

    component.search = 'nothing-matches-this';
    component.resetPage();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="repair-rows-table"]')).toBeNull();
    expect(host.querySelector('[data-testid="repair-rows-empty"]')?.textContent).toContain('Clear it to see all 1');
  });

  it('pages a long list and reports which rows are on screen', async () => {
    const rows = Array.from({ length: 60 }, (_, index) => row({ transactionId: index + 1 }));
    const { component, fixture, host } = await render(rows);

    expect(bodyRows(host)).toHaveLength(25);
    expect(host.querySelector('[data-testid="repair-rows-range"]')?.textContent?.replace(/\s+/g, ' ')).toContain(
      'Showing 1–25 of 60'
    );

    component.nextPage();
    fixture.detectChanges();
    expect(bodyRows(host)[0][0]).toContain('#26');

    component.nextPage();
    fixture.detectChanges();
    expect(bodyRows(host)).toHaveLength(10);
    expect(component.pageCount()).toBe(3);

    component.nextPage();
    expect(component.pageIndex).toBe(2);
  });

  it('returns to the first page when a new plan arrives', async () => {
    const rows = Array.from({ length: 60 }, (_, index) => row({ transactionId: index + 1 }));
    const { component, fixture } = await render(rows);

    component.nextPage();
    expect(component.pageIndex).toBe(1);

    fixture.componentRef.setInput('rows', [row({ transactionId: 999 })]);
    fixture.detectChanges();

    expect(component.pageIndex).toBe(0);
  });

  it('reports an empty plan as having nothing to repair', async () => {
    const { host } = await render([]);

    expect(host.querySelector('[data-testid="repair-rows-empty"]')?.textContent).toContain('Nothing is repairable');
  });
});
