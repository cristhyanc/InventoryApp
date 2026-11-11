import {
  ReimbursementImportFileOutcome,
  ReimbursementImportJobState,
  ReimbursementImportJobSummary
} from '../../../../services/import.service';
import {
  anyReimbursementJobRunning,
  isReimbursementJobRunning,
  reimbursementFileOutcomeLabel,
  reimbursementJobStateBadge,
  reimbursementJobStateLabel
} from './reimbursement-import-job-display';

function job(state: ReimbursementImportJobState): ReimbursementImportJobSummary {
  return {
    id: 1,
    fileName: 'Reports.zip',
    uploadedAtUtc: '2026-11-09T01:30:00Z',
    state,
    attempts: 1,
    nextAttemptAtUtc: null,
    lastError: null,
    finishedAtUtc: null,
    importedFiles: 0,
    skippedFiles: 0,
    failedFiles: 0
  };
}

/** The words the page uses for #1149's states, and the one rule that decides when to keep asking. */
describe('reimbursement import job display (issue #1152)', () => {
  it('names every state the API can report', () => {
    expect(reimbursementJobStateLabel(ReimbursementImportJobState.Queued)).toBe('Queued');
    expect(reimbursementJobStateLabel(ReimbursementImportJobState.Processing)).toBe('Processing');
    expect(reimbursementJobStateLabel(ReimbursementImportJobState.Completed)).toBe('Completed');
    expect(reimbursementJobStateLabel(ReimbursementImportJobState.CompletedWithFailures)).toBe(
      'Completed with failures'
    );
    expect(reimbursementJobStateLabel(ReimbursementImportJobState.Failed)).toBe('Failed');
  });

  it('names every file outcome the API can report', () => {
    expect(reimbursementFileOutcomeLabel(ReimbursementImportFileOutcome.Imported)).toBe('Imported');
    expect(reimbursementFileOutcomeLabel(ReimbursementImportFileOutcome.Skipped)).toBe('Skipped');
    expect(reimbursementFileOutcomeLabel(ReimbursementImportFileOutcome.Failed)).toBe('Failed');
  });

  /**
   * A state a later release adds is reported as unknown rather than as one of the states it is not:
   * showing a job the application cannot interpret as `Completed` would be a lie about an import.
   */
  it('reports a state this build does not recognise as unknown, never as one of the others', () => {
    const invented = 99 as ReimbursementImportJobState;

    expect(reimbursementJobStateLabel(invented)).toBe('Unknown');
    expect(reimbursementJobStateBadge(invented)).toBe('badge badge-neutral');
    expect(isReimbursementJobRunning(job(invented))).toBe(false);
  });

  it('marks a failed state as a failure and a finished one as success, by its badge', () => {
    expect(reimbursementJobStateBadge(ReimbursementImportJobState.Completed)).toContain('badge-success');
    expect(reimbursementJobStateBadge(ReimbursementImportJobState.CompletedWithFailures)).toContain('badge-warning');
    expect(reimbursementJobStateBadge(ReimbursementImportJobState.Failed)).toContain('badge-danger');
  });

  it('treats only Queued and Processing as still running', () => {
    expect(isReimbursementJobRunning(job(ReimbursementImportJobState.Queued))).toBe(true);
    expect(isReimbursementJobRunning(job(ReimbursementImportJobState.Processing))).toBe(true);
    expect(isReimbursementJobRunning(job(ReimbursementImportJobState.Completed))).toBe(false);
    expect(isReimbursementJobRunning(job(ReimbursementImportJobState.CompletedWithFailures))).toBe(false);
    expect(isReimbursementJobRunning(job(ReimbursementImportJobState.Failed))).toBe(false);
  });

  it('says a list is running when any one job in it is, and not when none is', () => {
    expect(anyReimbursementJobRunning([])).toBe(false);
    expect(
      anyReimbursementJobRunning([job(ReimbursementImportJobState.Completed), job(ReimbursementImportJobState.Failed)])
    ).toBe(false);
    expect(
      anyReimbursementJobRunning([job(ReimbursementImportJobState.Completed), job(ReimbursementImportJobState.Queued)])
    ).toBe(true);
  });
});
