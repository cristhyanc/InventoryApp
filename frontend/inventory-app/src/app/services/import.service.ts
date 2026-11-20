import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * One row of an uploaded sales file that carried a status or an amount the server could not read
 * (issue #1298). Nothing of such a row reached a stored sale; it is reported so the operator can
 * find it in the file by its row number.
 */
export interface NayaxSalesImportInvalidRow {
  readonly rowNumber: number;
  readonly transactionId: number;
}

/**
 * What `POST api/imports/nayax-sales` answers with. The three original counts are unchanged; issue
 * #1298 added the two facts the counts alone could not say - how many rows kept a stored value
 * (status, amount, payment method, product name or machine name) because the file's cell was blank
 * or its column was missing, counted once per row, and which rows carried a value that could not be
 * read at all. `invalid` is `invalidRows.length`, sent by the server.
 */
export interface NayaxSalesImportResult {
  imported: number;
  updated: number;
  skipped: number;
  keptStoredValues: number;
  invalid: number;
  invalidRows: readonly NayaxSalesImportInvalidRow[];
}

/**
 * What `POST api/imports/nayax-sales-history` answers with (issue #1304): the id of the job the
 * accepted upload became, and nothing else. The upload has imported nothing yet; the job list below
 * is what the page follows it with.
 */
export interface NayaxSalesHistoryUploadAccepted {
  jobId: number;
}

/**
 * Where one sales history import job has got to, mirroring
 * `Inventory.Domain.Imports.NayaxSalesHistoryImportJobState`. The API serializes the enum by its
 * number, so the numbers here are the contract and must stay in step with it.
 */
export enum NayaxSalesHistoryImportJobState {
  /** Accepted and waiting for the worker. No row has been imported yet. */
  Queued = 0,
  /** The worker is working through it, batch by batch. */
  Processing = 1,
  /** Every row of the file has been read and applied. */
  Completed = 2,
  /**
   * The job itself stopped. Every batch it had already committed stays committed, and the progress
   * says how far it got.
   */
  Failed = 3
}

/**
 * One job as `GET api/imports/nayax-sales-history/jobs` reports it (issue #1304), with the progress
 * the background job recorded in the same transaction as the sales it describes.
 */
export interface NayaxSalesHistoryImportJobSummary {
  readonly id: number;
  readonly fileName: string;
  readonly uploadedAtUtc: string;
  readonly state: NayaxSalesHistoryImportJobState;
  readonly totalRows: number;
  readonly rowsRead: number;
  readonly created: number;
  readonly updated: number;
  readonly skipped: number;
  readonly invalid: number;
  readonly keptStoredValues: number;
  readonly attempts: number;
  readonly lastError: string | null;
  readonly finishedAtUtc: string | null;
}

/**
 * What `POST api/imports/reimbursement-report` answers with (issue #1149): the id of the job the
 * accepted upload became, and nothing else. The upload has imported nothing yet, so there is no
 * count to report; the job endpoints below are what the page follows it with.
 */
export interface ReimbursementReportUploadAccepted {
  jobId: number;
}

/**
 * Where one reimbursement report import job has got to, mirroring
 * `Inventory.Domain.Imports.ReimbursementImportJobState`. The API serializes the enum by its
 * number, so the numbers here are the contract and must stay in step with it.
 */
export enum ReimbursementImportJobState {
  /** Accepted and waiting for the worker. Nothing has been imported yet. */
  Queued = 0,
  /** The worker is working through it now. */
  Processing = 1,
  /** Every XML in the archive was imported, or recognised as an already-imported duplicate. */
  Completed = 2,
  /** It finished, and at least one XML in it could not be imported. The rest were. */
  CompletedWithFailures = 3,
  /** The job itself stopped. Whatever it had already imported stays imported. */
  Failed = 4
}

/**
 * What one XML file inside an archive turned into, mirroring
 * `Inventory.Domain.Imports.ReimbursementImportFileOutcome` by its number for the same reason.
 */
export enum ReimbursementImportFileOutcome {
  Imported = 0,
  Skipped = 1,
  Failed = 2
}

/** One job as `GET api/imports/reimbursement-report/jobs` reports it (issue #1149). */
export interface ReimbursementImportJobSummary {
  readonly id: number;
  readonly fileName: string;
  readonly uploadedAtUtc: string;
  readonly state: ReimbursementImportJobState;
  readonly attempts: number;
  readonly nextAttemptAtUtc: string | null;
  readonly lastError: string | null;
  readonly finishedAtUtc: string | null;
  readonly importedFiles: number;
  readonly skippedFiles: number;
  readonly failedFiles: number;
}

/** One file's recorded outcome, as the job detail reports it. */
export interface ReimbursementImportJobFileSummary {
  readonly file: string;
  readonly outcome: ReimbursementImportFileOutcome;
  readonly reason: string | null;
}

/** One job with every file outcome recorded for it, in the archive's own order. */
export interface ReimbursementImportJobDetail {
  readonly job: ReimbursementImportJobSummary;
  readonly files: readonly ReimbursementImportJobFileSummary[];
}

/**
 * The uploaded sales import, and the Reimbursement Report ZIP upload with its job status reads.
 *
 * <p>There is no product catalogue import here: `POST api/imports/products` was replaced by the
 * catalogue review for every business in issue #739, so the catalogue is imported through
 * `CatalogueReviewService` instead - previewed, decided per entry and applied.</p>
 *
 * <p>And no pending-XML import either: `POST api/imports/pending-xml` read reimbursement reports
 * from a folder on the server, which issue #1237 retired. A reimbursement report is uploaded
 * through the Reimbursement Report page and nowhere else.</p>
 *
 * <p>It stays the sole authority for every import call the frontend makes (issue #1152): the
 * Reimbursement Report page reads and writes through the three methods below and reimplements no
 * archive, deduplication or reimbursement rule - every one of those is #1149's, on the server.</p>
 */
@Injectable({ providedIn: 'root' })
export class ImportService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/imports`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  importNayaxSales(file: File): Observable<NayaxSalesImportResult> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<NayaxSalesImportResult>(`${this.baseUrl}/nayax-sales`, formData);
  }

  /**
   * Uploads a long sales history as a CSV and answers the queued job's id (issue #1304). The server
   * checks the file - the byte limit, the format, its shape and the row ceiling - and refuses a bad
   * one with `400` and a readable sentence, having stored nothing at all. It imports nothing in the
   * request: the background job streams the rows and commits them in batches.
   */
  uploadNayaxSalesHistory(file: File): Observable<NayaxSalesHistoryUploadAccepted> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<NayaxSalesHistoryUploadAccepted>(`${this.baseUrl}/nayax-sales-history`, formData);
  }

  /** This business's most recent sales history uploads, newest first, with their progress. */
  nayaxSalesHistoryJobs(): Observable<readonly NayaxSalesHistoryImportJobSummary[]> {
    return this.http.get<NayaxSalesHistoryImportJobSummary[]>(`${this.baseUrl}/nayax-sales-history/jobs`);
  }

  /**
   * Uploads the ZIP Nayax emailed, exactly as it arrived, and answers the queued job's id. The
   * server checks the archive and refuses a bad one with `400` and a readable sentence, having
   * stored nothing at all.
   */
  uploadReimbursementReport(file: File): Observable<ReimbursementReportUploadAccepted> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<ReimbursementReportUploadAccepted>(`${this.baseUrl}/reimbursement-report`, formData);
  }

  /** This business's most recent reimbursement report uploads, newest first. */
  reimbursementReportJobs(): Observable<readonly ReimbursementImportJobSummary[]> {
    return this.http.get<ReimbursementImportJobSummary[]>(`${this.baseUrl}/reimbursement-report/jobs`);
  }

  /** One of this business's jobs with what each file in it turned into. */
  reimbursementReportJob(jobId: number): Observable<ReimbursementImportJobDetail> {
    return this.http.get<ReimbursementImportJobDetail>(`${this.baseUrl}/reimbursement-report/jobs/${jobId}`);
  }
}
