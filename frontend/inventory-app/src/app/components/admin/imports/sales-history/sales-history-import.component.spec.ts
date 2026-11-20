import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { BusinessTimeZoneService } from '../../../../formatting/business-time-zone.service';
import {
  ImportService,
  NayaxSalesHistoryImportJobState,
  NayaxSalesHistoryImportJobSummary
} from '../../../../services/import.service';
import {
  SALES_HISTORY_JOBS_REFRESH_MS,
  SalesHistoryImportComponent
} from './sales-history-import.component';

function job(
  overrides: Partial<NayaxSalesHistoryImportJobSummary> = {}
): NayaxSalesHistoryImportJobSummary {
  return {
    id: 1,
    fileName: 'history.csv',
    uploadedAtUtc: '2026-11-19T01:30:00Z',
    state: NayaxSalesHistoryImportJobState.Completed,
    totalRows: 6000,
    rowsRead: 6000,
    created: 5800,
    updated: 150,
    skipped: 40,
    invalid: 10,
    keptStoredValues: 20,
    attempts: 1,
    lastError: null,
    finishedAtUtc: '2026-11-19T01:50:00Z',
    ...overrides
  };
}

async function render(imports: Partial<ImportService>, disabled = false) {
  await TestBed.configureTestingModule({
    imports: [SalesHistoryImportComponent],
    providers: [{ provide: ImportService, useValue: imports }]
  }).compileComponents();

  // The instants render in the business's own zone, which this business's is Australia/Sydney.
  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(SalesHistoryImportComponent);
  fixture.componentRef.setInput('disabled', disabled);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function testId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(`[data-testid="${id}"]`);
}

function rows(host: HTMLElement): HTMLElement[] {
  return Array.from(host.querySelectorAll<HTMLElement>('[data-testid="sales-history-job-row"]'));
}

function text(element: HTMLElement | null, id: string): string {
  return element?.querySelector<HTMLElement>(`[data-testid="${id}"]`)?.textContent?.trim() ?? '';
}

function choose(host: HTMLElement, name: string): void {
  const input = testId(host, 'sales-history-file-input') as HTMLInputElement;
  const file = new File(['TransactionID\n9000001'], name, { type: 'text/csv' });

  Object.defineProperty(input, 'files', { value: [file], configurable: true });
  input.dispatchEvent(new Event('change'));
}

const noJobs: Partial<ImportService> = { nayaxSalesHistoryJobs: () => of([]) };

beforeEach(() => jest.useFakeTimers());
afterEach(() => {
  jest.useRealTimers();
  TestBed.resetTestingModule();
});

/**
 * The sales history import card on the Imports page (issue #1304): the upload, and the list of what
 * this business's uploads turned into.
 *
 * <p>It decides nothing about an import. The server checks the file and answers a readable sentence
 * for a refusal, counts every row and records the progress; this card shows what it said.</p>
 */
describe('SalesHistoryImportComponent (issue #1304)', () => {
  it('says so when this business has uploaded no history yet', async () => {
    const { host } = await render(noJobs);

    expect(testId(host, 'sales-history-jobs-empty')).not.toBeNull();
    expect(rows(host)).toHaveLength(0);
  });

  it('uploads the chosen file and reports that it is queued', async () => {
    const uploadNayaxSalesHistory = jest.fn(() => of({ jobId: 7 }));
    const nayaxSalesHistoryJobs = jest.fn(() => of([]));
    const { fixture, host } = await render({ uploadNayaxSalesHistory, nayaxSalesHistoryJobs });

    choose(host, 'Sales history.csv');
    fixture.detectChanges();
    expect(text(host, 'sales-history-selected-file')).toContain('Sales history.csv');

    (testId(host, 'sales-history-upload-button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(uploadNayaxSalesHistory).toHaveBeenCalledTimes(1);
    expect(testId(host, 'sales-history-upload-accepted')).not.toBeNull();

    // The accepted upload reloads the list, so the job it became appears without a page reload.
    expect(nayaxSalesHistoryJobs).toHaveBeenCalledTimes(2);

    // And the file is cleared, so a second click cannot send the same one again.
    expect(testId(host, 'sales-history-selected-file')).toBeNull();
    expect((testId(host, 'sales-history-upload-button') as HTMLButtonElement).disabled).toBe(true);
  });

  /**
   * The server's own refusal sentence, shown beside the file it refused rather than as a toast: a
   * refused upload stores nothing at all, and the operator's next step is to look at the file they
   * chose.
   */
  it("shows the server's refusal sentence", async () => {
    const uploadNayaxSalesHistory = jest.fn(() =>
      throwError(() => ({ status: 400, error: 'This file has more than 250,000 sales.' }))
    );
    const { fixture, host } = await render({ ...noJobs, uploadNayaxSalesHistory });

    choose(host, 'history.csv');
    fixture.detectChanges();
    (testId(host, 'sales-history-upload-button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(text(host, 'sales-history-upload-error')).toContain('more than 250,000 sales');
    expect(testId(host, 'sales-history-upload-accepted')).toBeNull();
  });

  it('withholds the upload while a support session is active', async () => {
    const uploadNayaxSalesHistory = jest.fn(() => of({ jobId: 7 }));
    const { fixture, host } = await render({ ...noJobs, uploadNayaxSalesHistory }, true);

    choose(host, 'history.csv');
    fixture.detectChanges();

    expect((testId(host, 'sales-history-upload-button') as HTMLButtonElement).disabled).toBe(true);

    // And the guard is the component's own as well as the template's, so the upload is actually
    // unavailable rather than merely inert.
    fixture.componentInstance.upload();

    expect(uploadNayaxSalesHistory).not.toHaveBeenCalled();
  });

  it("shows each upload's name, Sydney instant, state, progress and counts", async () => {
    const { host } = await render({
      nayaxSalesHistoryJobs: jest.fn(() => of([job()]))
    });

    const row = rows(host)[0];
    expect(text(row, 'job-file')).toBe('history.csv');
    // 01:30 UTC on 19 November 2026 is 12:30 pm in Sydney (UTC+11).
    expect(text(row, 'job-uploaded-at')).toContain('19/11/2026');
    expect(text(row, 'job-uploaded-at')).toContain('12:30');
    expect(text(row, 'job-state')).toBe('Completed');
    expect(text(row, 'job-progress')).toBe('6,000 of 6,000 rows');
    expect(text(row, 'job-counts')).toContain('5,800 imported');
    expect(text(row, 'job-counts')).toContain('10 could not be read');
  });

  /** A job that stopped says why, and what it did import stays on screen beside the reason. */
  it('shows a failed job with its reason and the rows it did import', async () => {
    const { host } = await render({
      nayaxSalesHistoryJobs: jest.fn(() =>
        of([
          job({
            state: NayaxSalesHistoryImportJobState.Failed,
            rowsRead: 1500,
            created: 1500,
            updated: 0,
            skipped: 0,
            invalid: 0,
            keptStoredValues: 0,
            lastError: 'The import stopped after 3 attempts.'
          })
        ])
      )
    });

    const row = rows(host)[0];
    expect(text(row, 'job-state')).toBe('Failed');
    expect(text(row, 'job-progress')).toBe('1,500 of 6,000 rows');
    expect(text(row, 'job-last-error')).toContain('stopped after 3 attempts');
  });

  it('refreshes while a job is queued or importing, and stops once nothing is running', async () => {
    const answers: NayaxSalesHistoryImportJobSummary[][] = [
      [job({ state: NayaxSalesHistoryImportJobState.Queued, rowsRead: 0, created: 0 })],
      [job({ state: NayaxSalesHistoryImportJobState.Processing, rowsRead: 2000, created: 2000 })],
      [job({ state: NayaxSalesHistoryImportJobState.Completed, rowsRead: 6000 })]
    ];
    const nayaxSalesHistoryJobs = jest.fn(() => of(answers.shift() ?? []));
    const { fixture, host } = await render({ nayaxSalesHistoryJobs });

    expect(nayaxSalesHistoryJobs).toHaveBeenCalledTimes(1);

    jest.advanceTimersByTime(SALES_HISTORY_JOBS_REFRESH_MS);
    fixture.detectChanges();
    expect(nayaxSalesHistoryJobs).toHaveBeenCalledTimes(2);
    expect(text(rows(host)[0], 'job-progress')).toBe('2,000 of 6,000 rows');

    jest.advanceTimersByTime(SALES_HISTORY_JOBS_REFRESH_MS);
    fixture.detectChanges();
    expect(nayaxSalesHistoryJobs).toHaveBeenCalledTimes(3);
    expect(text(rows(host)[0], 'job-state')).toBe('Completed');

    // Nothing is running any more, so the refresh stops rather than polling a settled list forever.
    jest.advanceTimersByTime(SALES_HISTORY_JOBS_REFRESH_MS * 5);
    expect(nayaxSalesHistoryJobs).toHaveBeenCalledTimes(3);
  });

  it('never starts refreshing when the first read already reports nothing running', async () => {
    const nayaxSalesHistoryJobs = jest.fn(() => of([job()]));
    await render({ nayaxSalesHistoryJobs });

    jest.advanceTimersByTime(SALES_HISTORY_JOBS_REFRESH_MS * 10);

    expect(nayaxSalesHistoryJobs).toHaveBeenCalledTimes(1);
  });

  it('stops refreshing once it is destroyed, so a closed page asks for nothing', async () => {
    const nayaxSalesHistoryJobs = jest.fn(() =>
      of([job({ state: NayaxSalesHistoryImportJobState.Processing })])
    );
    const { fixture } = await render({ nayaxSalesHistoryJobs });

    fixture.destroy();
    jest.advanceTimersByTime(SALES_HISTORY_JOBS_REFRESH_MS * 10);

    expect(nayaxSalesHistoryJobs).toHaveBeenCalledTimes(1);
  });

  it('reports a read that failed and reads again when asked to', async () => {
    const answers: Observable<readonly NayaxSalesHistoryImportJobSummary[]>[] = [
      throwError(() => ({ status: 500 })),
      of([job()])
    ];
    const nayaxSalesHistoryJobs = jest.fn(() => answers.shift()!);
    const { fixture, host } = await render({ nayaxSalesHistoryJobs });

    expect(testId(host, 'sales-history-jobs-error')).not.toBeNull();
    expect(rows(host)).toHaveLength(0);

    testId(host, 'sales-history-jobs-retry')?.click();
    fixture.detectChanges();

    expect(testId(host, 'sales-history-jobs-error')).toBeNull();
    expect(rows(host)).toHaveLength(1);
  });
});
