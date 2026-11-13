import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

export interface NayaxSalesImportResult {
  imported: number;
  updated: number;
  skipped: number;
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
