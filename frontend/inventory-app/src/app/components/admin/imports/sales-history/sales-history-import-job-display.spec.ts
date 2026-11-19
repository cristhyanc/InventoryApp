import {
  NayaxSalesHistoryImportJobState,
  NayaxSalesHistoryImportJobSummary
} from '../../../../services/import.service';
import {
  anySalesHistoryJobRunning,
  salesHistoryJobCounts,
  salesHistoryJobProgress,
  salesHistoryJobStateBadge,
  salesHistoryJobStateLabel
} from './sales-history-import-job-display';

function job(
  overrides: Partial<NayaxSalesHistoryImportJobSummary> = {}
): NayaxSalesHistoryImportJobSummary {
  return {
    id: 1,
    fileName: 'history.csv',
    uploadedAtUtc: '2026-11-19T03:00:00Z',
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
    finishedAtUtc: '2026-11-19T03:20:00Z',
    ...overrides
  };
}

/**
 * How the Imports page words a sales history job (issue #1304). It decides nothing about the import:
 * the states and every count are the server's, and this module only names them.
 */
describe('sales history import job display (issue #1304)', () => {
  it('names every state the API can report, in words', () => {
    expect(salesHistoryJobStateLabel(NayaxSalesHistoryImportJobState.Queued)).toBe('Queued');
    expect(salesHistoryJobStateLabel(NayaxSalesHistoryImportJobState.Processing)).toBe('Importing');
    expect(salesHistoryJobStateLabel(NayaxSalesHistoryImportJobState.Completed)).toBe('Completed');
    expect(salesHistoryJobStateLabel(NayaxSalesHistoryImportJobState.Failed)).toBe('Failed');
  });

  /** A state this build has never heard of is exactly what it says, not one of the states it is not. */
  it('calls a state it does not recognise unknown', () => {
    expect(salesHistoryJobStateLabel(42 as NayaxSalesHistoryImportJobState)).toBe('Unknown');
    expect(salesHistoryJobStateBadge(42 as NayaxSalesHistoryImportJobState)).toBe('badge badge-neutral');
  });

  it('badges a failure as one and an import in progress as one', () => {
    expect(salesHistoryJobStateBadge(NayaxSalesHistoryImportJobState.Failed)).toBe('badge badge-danger');
    expect(salesHistoryJobStateBadge(NayaxSalesHistoryImportJobState.Processing)).toBe('badge badge-info');
    expect(salesHistoryJobStateBadge(NayaxSalesHistoryImportJobState.Completed)).toBe('badge badge-success');
    expect(salesHistoryJobStateBadge(NayaxSalesHistoryImportJobState.Queued)).toBe('badge badge-neutral');
  });

  /** The progress an operator actually wants: how far through the file the job is. */
  it('reports the rows committed against the rows in the file', () => {
    expect(salesHistoryJobProgress(job({ rowsRead: 1500, totalRows: 6000 }))).toBe('1,500 of 6,000 rows');
    expect(salesHistoryJobProgress(job({ rowsRead: 0, totalRows: 250000 }))).toBe('0 of 250,000 rows');
  });

  /**
   * The counts the server reported, named the way the normal sales file import's summary names them -
   * including the two facts the counts alone cannot say.
   */
  it('reports what the rows turned into', () => {
    expect(salesHistoryJobCounts(job())).toBe(
      '5,800 imported, 150 updated, 40 skipped, 10 could not be read, 20 kept stored values'
    );
  });

  it('is still running while the server says queued or processing', () => {
    expect(anySalesHistoryJobRunning([job({ state: NayaxSalesHistoryImportJobState.Queued })])).toBe(true);
    expect(anySalesHistoryJobRunning([job({ state: NayaxSalesHistoryImportJobState.Processing })])).toBe(
      true
    );
    expect(anySalesHistoryJobRunning([job()])).toBe(false);
    expect(anySalesHistoryJobRunning([job({ state: NayaxSalesHistoryImportJobState.Failed })])).toBe(false);
    expect(anySalesHistoryJobRunning([])).toBe(false);
  });
});
