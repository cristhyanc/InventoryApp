import {
  NayaxSalesHistoryImportJobState,
  NayaxSalesHistoryImportJobSummary
} from '../../../../services/import.service';

/**
 * How the Imports page words a sales history import job's state and progress (issue #1304), and the
 * one rule that decides whether anything is still running.
 *
 * It is a module of its own so the card, its list and their tests read the same answers, and so the
 * "keep refreshing" rule is stated once: a job is live while the server says `Queued` or
 * `Processing`, and every other state - including a state this build does not recognise - is
 * finished as far as the browser is concerned. Nothing here decides anything about the import: the
 * states are the server's, every count was written there in the same transaction as the sales it
 * describes, and this file only names them.
 */

/** The states the server moves on from by itself, so the list keeps asking while one is reported. */
const RUNNING_STATES: ReadonlySet<NayaxSalesHistoryImportJobState> = new Set([
  NayaxSalesHistoryImportJobState.Queued,
  NayaxSalesHistoryImportJobState.Processing
]);

const JOB_STATE_LABELS: Readonly<Record<NayaxSalesHistoryImportJobState, string>> = {
  [NayaxSalesHistoryImportJobState.Queued]: 'Queued',
  // "Importing" rather than "Processing": what it is doing is importing rows, and the progress
  // beside it says how far it has got.
  [NayaxSalesHistoryImportJobState.Processing]: 'Importing',
  [NayaxSalesHistoryImportJobState.Completed]: 'Completed',
  [NayaxSalesHistoryImportJobState.Failed]: 'Failed'
};

/** The badge class each state is shown with, so a failure reads as one at a glance. */
const JOB_STATE_BADGES: Readonly<Record<NayaxSalesHistoryImportJobState, string>> = {
  [NayaxSalesHistoryImportJobState.Queued]: 'badge badge-neutral',
  [NayaxSalesHistoryImportJobState.Processing]: 'badge badge-info',
  [NayaxSalesHistoryImportJobState.Completed]: 'badge badge-success',
  [NayaxSalesHistoryImportJobState.Failed]: 'badge badge-danger'
};

/**
 * The state in words. An unrecognised number is shown as `Unknown` rather than as a bare number or
 * as one of the states it is not: a state this build has never heard of is exactly what it says.
 */
export function salesHistoryJobStateLabel(state: NayaxSalesHistoryImportJobState): string {
  return JOB_STATE_LABELS[state] ?? 'Unknown';
}

/** The badge classes for a state, neutral for one this build does not recognise. */
export function salesHistoryJobStateBadge(state: NayaxSalesHistoryImportJobState): string {
  return JOB_STATE_BADGES[state] ?? 'badge badge-neutral';
}

/**
 * How far through the file the job is: the rows it has committed against the rows the file carries.
 * Both numbers are the server's - the committed count is also where a resumed attempt restarts - so
 * a job reporting 1,500 of 6,000 has 1,500 rows actually imported and nothing in between.
 */
export function salesHistoryJobProgress(job: NayaxSalesHistoryImportJobSummary): string {
  return `${count(job.rowsRead)} of ${count(job.totalRows)} rows`;
}

/**
 * What the rows turned into, named the way the normal sales file import's own summary names them -
 * including the two facts the first three counts cannot say: how many rows carried a value nobody
 * could read, and how many kept a stored value because the file's cell was blank.
 */
export function salesHistoryJobCounts(job: NayaxSalesHistoryImportJobSummary): string {
  return (
    `${count(job.created)} imported, ${count(job.updated)} updated, ${count(job.skipped)} skipped, ` +
    `${count(job.invalid)} could not be read, ${count(job.keptStoredValues)} kept stored values`
  );
}

/** Whether the server is still expected to move this job on by itself. */
export function isSalesHistoryJobRunning(job: NayaxSalesHistoryImportJobSummary): boolean {
  return RUNNING_STATES.has(job.state);
}

/** Whether any of these jobs is still running, which is the whole of the refresh rule. */
export function anySalesHistoryJobRunning(
  jobs: readonly NayaxSalesHistoryImportJobSummary[]
): boolean {
  return jobs.some(isSalesHistoryJobRunning);
}

/** A row count with thousands separators, because these are numbers in the tens of thousands. */
function count(value: number): string {
  return value.toLocaleString('en-AU');
}
