import {
  ReimbursementImportFileOutcome,
  ReimbursementImportJobState,
  ReimbursementImportJobSummary
} from '../../../../services/import.service';

/**
 * How the Reimbursement Report page words a job's state and a file's outcome, and the one rule that
 * decides whether anything is still running (issue #1152).
 *
 * It is a module of its own so the page, the list and their tests read the same answers, and so the
 * "keep refreshing" rule is stated once: a job is live while the server says `Queued` or
 * `Processing`, and every other state - including a state this build does not recognise - is
 * finished as far as the browser is concerned. Nothing here decides anything about the import: the
 * states are #1149's, the counts are counted on the server from its own per-file outcome rows, and
 * this file only names them.
 */

/** The states the server moves on from by itself, so the list keeps asking while one is reported. */
const RUNNING_STATES: ReadonlySet<ReimbursementImportJobState> = new Set([
  ReimbursementImportJobState.Queued,
  ReimbursementImportJobState.Processing
]);

const JOB_STATE_LABELS: Readonly<Record<ReimbursementImportJobState, string>> = {
  [ReimbursementImportJobState.Queued]: 'Queued',
  [ReimbursementImportJobState.Processing]: 'Processing',
  [ReimbursementImportJobState.Completed]: 'Completed',
  [ReimbursementImportJobState.CompletedWithFailures]: 'Completed with failures',
  [ReimbursementImportJobState.Failed]: 'Failed'
};

const FILE_OUTCOME_LABELS: Readonly<Record<ReimbursementImportFileOutcome, string>> = {
  [ReimbursementImportFileOutcome.Imported]: 'Imported',
  [ReimbursementImportFileOutcome.Skipped]: 'Skipped',
  [ReimbursementImportFileOutcome.Failed]: 'Failed'
};

/** The badge class each state is shown with, so a failure reads as one at a glance. */
const JOB_STATE_BADGES: Readonly<Record<ReimbursementImportJobState, string>> = {
  [ReimbursementImportJobState.Queued]: 'badge badge-neutral',
  [ReimbursementImportJobState.Processing]: 'badge badge-info',
  [ReimbursementImportJobState.Completed]: 'badge badge-success',
  [ReimbursementImportJobState.CompletedWithFailures]: 'badge badge-warning',
  [ReimbursementImportJobState.Failed]: 'badge badge-danger'
};

/**
 * The state in words. An unrecognised number is shown as `Unknown` rather than as a bare number or
 * as one of the states it is not: a state this build has never heard of is exactly what it says.
 */
export function reimbursementJobStateLabel(state: ReimbursementImportJobState): string {
  return JOB_STATE_LABELS[state] ?? 'Unknown';
}

/** The badge classes for a state, neutral for one this build does not recognise. */
export function reimbursementJobStateBadge(state: ReimbursementImportJobState): string {
  return JOB_STATE_BADGES[state] ?? 'badge badge-neutral';
}

/** One file's outcome in words. */
export function reimbursementFileOutcomeLabel(outcome: ReimbursementImportFileOutcome): string {
  return FILE_OUTCOME_LABELS[outcome] ?? 'Unknown';
}

/** Whether the server is still expected to move this job on by itself. */
export function isReimbursementJobRunning(job: ReimbursementImportJobSummary): boolean {
  return RUNNING_STATES.has(job.state);
}

/** Whether any of these jobs is still running, which is the whole of the refresh rule. */
export function anyReimbursementJobRunning(jobs: readonly ReimbursementImportJobSummary[]): boolean {
  return jobs.some(isReimbursementJobRunning);
}
