import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { BusinessTimeZoneService } from '../../../../formatting/business-time-zone.service';
import {
  ImportService,
  ReimbursementImportFileOutcome,
  ReimbursementImportJobDetail,
  ReimbursementImportJobState,
  ReimbursementImportJobSummary
} from '../../../../services/import.service';
import {
  REIMBURSEMENT_JOBS_REFRESH_MS,
  ReimbursementImportJobsComponent
} from './reimbursement-import-jobs.component';

function job(overrides: Partial<ReimbursementImportJobSummary> = {}): ReimbursementImportJobSummary {
  return {
    id: 1,
    fileName: 'Reports_09-11-26.zip',
    uploadedAtUtc: '2026-11-09T01:30:00Z',
    state: ReimbursementImportJobState.Completed,
    attempts: 1,
    nextAttemptAtUtc: null,
    lastError: null,
    finishedAtUtc: '2026-11-09T01:31:00Z',
    importedFiles: 3,
    skippedFiles: 1,
    failedFiles: 0,
    ...overrides
  };
}

function detail(
  summary: ReimbursementImportJobSummary,
  files: ReimbursementImportJobDetail['files']
): ReimbursementImportJobDetail {
  return { job: summary, files };
}

async function render(imports: Partial<ImportService>, reloadToken = 0) {
  await TestBed.configureTestingModule({
    imports: [ReimbursementImportJobsComponent],
    providers: [{ provide: ImportService, useValue: imports }]
  }).compileComponents();

  // The instants render in the business's own zone, which this business's is Australia/Sydney.
  TestBed.inject(BusinessTimeZoneService).publish('Australia/Sydney');

  const fixture = TestBed.createComponent(ReimbursementImportJobsComponent);
  fixture.componentRef.setInput('reloadToken', reloadToken);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function testId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(`[data-testid="${id}"]`);
}

function rows(host: HTMLElement): HTMLElement[] {
  return Array.from(host.querySelectorAll<HTMLElement>('[data-testid="job-row"]'));
}

function text(element: HTMLElement | null, id: string): string {
  return element?.querySelector<HTMLElement>(`[data-testid="${id}"]`)?.textContent?.trim() ?? '';
}

beforeEach(() => jest.useFakeTimers());
afterEach(() => {
  jest.useRealTimers();
  TestBed.resetTestingModule();
});

/**
 * The recent uploads list on the Reimbursement Report page (issue #1152). It reads #1149's two
 * status endpoints and decides nothing of its own: every state, count and reason on screen is the
 * server's answer, counted on the server from its own per-file outcome rows.
 */
describe('ReimbursementImportJobsComponent (issue #1152)', () => {
  it('says so when this business has uploaded nothing yet', async () => {
    const { host } = await render({ reimbursementReportJobs: jest.fn(() => of([])) });

    expect(testId(host, 'jobs-empty')).not.toBeNull();
    expect(rows(host)).toHaveLength(0);
  });

  it("shows each upload's file name, Sydney instant, state and counts", async () => {
    const { host } = await render({ reimbursementReportJobs: jest.fn(() => of([job()])) });

    const row = rows(host)[0];
    expect(text(row, 'job-file')).toBe('Reports_09-11-26.zip');
    // 01:30 UTC on 9 November 2026 is 12:30 pm in Sydney (UTC+11).
    expect(text(row, 'job-uploaded-at')).toContain('09/11/2026');
    expect(text(row, 'job-uploaded-at')).toContain('12:30');
    expect(text(row, 'job-state')).toBe('Completed');
    expect(text(row, 'job-counts')).toContain('3 imported');
    expect(text(row, 'job-counts')).toContain('1 skipped');
    expect(text(row, 'job-counts')).toContain('0 failed');
  });

  it('names every state the API can report, in words', async () => {
    const states: [ReimbursementImportJobState, string][] = [
      [ReimbursementImportJobState.Queued, 'Queued'],
      [ReimbursementImportJobState.Processing, 'Processing'],
      [ReimbursementImportJobState.Completed, 'Completed'],
      [ReimbursementImportJobState.CompletedWithFailures, 'Completed with failures'],
      [ReimbursementImportJobState.Failed, 'Failed']
    ];
    const { host } = await render({
      reimbursementReportJobs: jest.fn(() =>
        of(states.map(([state], index) => job({ id: index + 1, state, failedFiles: 0 })))
      )
    });

    expect(rows(host).map((row) => text(row, 'job-state'))).toEqual(states.map(([, label]) => label));
  });

  /**
   * The failed file names and their reasons, from the job detail endpoint, and only while a row is
   * expanded: a list of twenty uploads must not pull twenty details to render a summary.
   */
  it('lists the failed file names and reasons when a row with failures is expanded', async () => {
    const failing = job({ id: 5, state: ReimbursementImportJobState.CompletedWithFailures, importedFiles: 2, failedFiles: 1 });
    const reimbursementReportJob = jest.fn(() =>
      of(
        detail(failing, [
          { file: '11-2026/report_1.xml', outcome: ReimbursementImportFileOutcome.Imported, reason: null },
          { file: '11-2026/report_2.xml', outcome: ReimbursementImportFileOutcome.Skipped, reason: null },
          { file: '11-2026/report_3.xml', outcome: ReimbursementImportFileOutcome.Failed, reason: 'The XML could not be read.' }
        ])
      )
    );
    const { fixture, host } = await render({
      reimbursementReportJobs: jest.fn(() => of([failing])),
      reimbursementReportJob
    });

    expect(testId(host, 'job-detail')).toBeNull();
    expect(reimbursementReportJob).not.toHaveBeenCalled();

    testId(host, 'job-expand')?.click();
    fixture.detectChanges();

    expect(reimbursementReportJob).toHaveBeenCalledWith(5);
    const failed = Array.from(host.querySelectorAll<HTMLElement>('[data-testid="job-failed-file"]'));
    expect(failed).toHaveLength(1);
    expect(failed[0].textContent).toContain('11-2026/report_3.xml');
    expect(failed[0].textContent).toContain('The XML could not be read.');
    // The files that imported or were skipped are not listed: the row's counts already say so.
    expect(host.textContent).not.toContain('report_1.xml');
  });

  it('collapses an expanded row again without re-reading it', async () => {
    const failing = job({ id: 5, state: ReimbursementImportJobState.CompletedWithFailures, failedFiles: 1 });
    const reimbursementReportJob = jest.fn(() =>
      of(detail(failing, [{ file: 'a.xml', outcome: ReimbursementImportFileOutcome.Failed, reason: 'No.' }]))
    );
    const { fixture, host } = await render({
      reimbursementReportJobs: jest.fn(() => of([failing])),
      reimbursementReportJob
    });

    testId(host, 'job-expand')?.click();
    fixture.detectChanges();
    testId(host, 'job-expand')?.click();
    fixture.detectChanges();

    expect(testId(host, 'job-detail')).toBeNull();
    expect(reimbursementReportJob).toHaveBeenCalledTimes(1);
  });

  it('says so when a job detail cannot be read, leaving the row itself alone', async () => {
    const failing = job({ id: 5, state: ReimbursementImportJobState.CompletedWithFailures, failedFiles: 1 });
    const { fixture, host } = await render({
      reimbursementReportJobs: jest.fn(() => of([failing])),
      reimbursementReportJob: jest.fn(() => throwError(() => ({ status: 500 })))
    });

    testId(host, 'job-expand')?.click();
    fixture.detectChanges();

    expect(testId(host, 'job-detail-error')).not.toBeNull();
    expect(text(rows(host)[0], 'job-state')).toBe('Completed with failures');
  });

  /** A finished job has nothing to expand: there is no failed file to name. */
  it('offers no expansion for a job with no failures', async () => {
    const { host } = await render({ reimbursementReportJobs: jest.fn(() => of([job()])) });

    expect(testId(host, 'job-expand')).toBeNull();
  });

  it('refreshes while a job is queued or processing, and stops once nothing is running', async () => {
    const answers: ReimbursementImportJobSummary[][] = [
      [job({ state: ReimbursementImportJobState.Queued, importedFiles: 0, skippedFiles: 0 })],
      [job({ state: ReimbursementImportJobState.Processing, importedFiles: 1, skippedFiles: 0 })],
      [job({ state: ReimbursementImportJobState.Completed, importedFiles: 4, skippedFiles: 0 })]
    ];
    const reimbursementReportJobs = jest.fn(() => of(answers.shift() ?? []));
    const { fixture, host } = await render({ reimbursementReportJobs });

    expect(reimbursementReportJobs).toHaveBeenCalledTimes(1);

    jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS);
    fixture.detectChanges();
    expect(reimbursementReportJobs).toHaveBeenCalledTimes(2);

    jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS);
    fixture.detectChanges();
    expect(reimbursementReportJobs).toHaveBeenCalledTimes(3);
    expect(text(rows(host)[0], 'job-state')).toBe('Completed');

    // Nothing is running any more, so the refresh stops rather than polling a settled list forever.
    jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS * 5);
    expect(reimbursementReportJobs).toHaveBeenCalledTimes(3);
  });

  it('never starts refreshing when the first read already reports nothing running', async () => {
    const reimbursementReportJobs = jest.fn(() => of([job()]));
    await render({ reimbursementReportJobs });

    jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS * 10);

    expect(reimbursementReportJobs).toHaveBeenCalledTimes(1);
  });

  it('stops refreshing once it is destroyed, so a closed page asks for nothing', async () => {
    const reimbursementReportJobs = jest.fn(() => of([job({ state: ReimbursementImportJobState.Queued })]));
    const { fixture } = await render({ reimbursementReportJobs });

    fixture.destroy();
    jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS * 10);

    expect(reimbursementReportJobs).toHaveBeenCalledTimes(1);
  });

  it('reports a first read that failed and reads again when asked to', async () => {
    const answers: Observable<readonly ReimbursementImportJobSummary[]>[] = [
      throwError(() => ({ status: 500 })),
      of([job()])
    ];
    const reimbursementReportJobs = jest.fn(() => answers.shift()!);
    const { fixture, host } = await render({ reimbursementReportJobs });

    expect(testId(host, 'jobs-error')).not.toBeNull();
    expect(rows(host)).toHaveLength(0);

    testId(host, 'jobs-retry')?.click();
    fixture.detectChanges();

    expect(testId(host, 'jobs-error')).toBeNull();
    expect(rows(host)).toHaveLength(1);
  });

  /**
   * A refresh that fails must not take the list off screen: what it already reported is still the
   * last thing the server said. The refresh stops rather than hammering a failing endpoint, and the
   * operator is offered the read again.
   */
  it('keeps the rows and stops refreshing when a refresh fails', async () => {
    const answers: Observable<readonly ReimbursementImportJobSummary[]>[] = [
      of([job({ state: ReimbursementImportJobState.Processing })]),
      throwError(() => ({ status: 503 }))
    ];
    const reimbursementReportJobs = jest.fn(() => answers.shift() ?? of([]));
    const { fixture, host } = await render({ reimbursementReportJobs });

    jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS);
    fixture.detectChanges();

    expect(reimbursementReportJobs).toHaveBeenCalledTimes(2);
    expect(rows(host)).toHaveLength(1);
    expect(testId(host, 'jobs-error')).not.toBeNull();

    jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS * 5);
    expect(reimbursementReportJobs).toHaveBeenCalledTimes(2);
  });

  /** An accepted upload tells the page, and the page tells this list through its reload token. */
  it('reads again when the page reports a new upload', async () => {
    const reimbursementReportJobs = jest.fn(() => of([job()]));
    const { fixture } = await render({ reimbursementReportJobs });

    fixture.componentRef.setInput('reloadToken', 1);
    fixture.detectChanges();

    expect(reimbursementReportJobs).toHaveBeenCalledTimes(2);
  });

  /**
   * Reads overlap, and the one asked for first can answer last. An answer older than an accepted
   * upload does not know about the job it created, so if it were still allowed to land it would put
   * the pre-upload list back, take the queued job off screen, and - finding nothing running - stop
   * the refresh that was following it. The superseded read is cancelled instead.
   */
  describe('overlapping reads', () => {
    /** One list read per call, each answered by hand so the test decides the order they return in. */
    function pendingReads() {
      const reads: Subject<readonly ReimbursementImportJobSummary[]>[] = [];
      const reimbursementReportJobs = jest.fn(() => {
        const read = new Subject<readonly ReimbursementImportJobSummary[]>();
        reads.push(read);
        return read.asObservable();
      });
      return { reads, reimbursementReportJobs };
    }

    const queued = () =>
      job({ id: 7, state: ReimbursementImportJobState.Queued, importedFiles: 0, skippedFiles: 0, failedFiles: 0 });

    it('keeps the newly queued job and keeps refreshing when the superseded read answers last', async () => {
      const { reads, reimbursementReportJobs } = pendingReads();
      const { fixture, host } = await render({ reimbursementReportJobs });

      // The arrival read is still in flight when an accepted upload asks for the list again.
      fixture.componentRef.setInput('reloadToken', 1);
      fixture.detectChanges();
      expect(reimbursementReportJobs).toHaveBeenCalledTimes(2);

      // The newer read answers first, with the job the upload created.
      reads[1].next([queued()]);
      reads[1].complete();
      fixture.detectChanges();

      // The older read answers last, with the list as it was before that upload existed.
      reads[0].next([]);
      reads[0].complete();
      fixture.detectChanges();

      expect(rows(host)).toHaveLength(1);
      expect(text(rows(host)[0], 'job-state')).toBe('Queued');
      expect(testId(host, 'jobs-empty')).toBeNull();

      // And the queued job is still being followed: the refresh the newer answer armed is intact.
      jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS);
      fixture.detectChanges();
      expect(reimbursementReportJobs).toHaveBeenCalledTimes(3);

      // Because the superseded read was cancelled outright, not merely ignored on arrival.
      expect(reads[0].observed).toBe(false);
    });

    it('reports no failure when the superseded read fails after the newer one succeeded', async () => {
      const { reads, reimbursementReportJobs } = pendingReads();
      const { fixture, host } = await render({ reimbursementReportJobs });

      fixture.componentRef.setInput('reloadToken', 1);
      fixture.detectChanges();

      reads[1].next([queued()]);
      reads[1].complete();
      fixture.detectChanges();

      reads[0].error({ status: 503 });
      fixture.detectChanges();

      expect(testId(host, 'jobs-error')).toBeNull();
      expect(rows(host)).toHaveLength(1);

      jest.advanceTimersByTime(REIMBURSEMENT_JOBS_REFRESH_MS);
      fixture.detectChanges();
      expect(reimbursementReportJobs).toHaveBeenCalledTimes(3);
    });
  });
});
