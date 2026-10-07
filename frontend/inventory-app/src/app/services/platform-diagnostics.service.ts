import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * The server-side hard maxima a diagnostics query runs under. Matches
 * `InventoryApi.DTOs.PlatformDiagnosticsLimitsResponse` (issue #336).
 *
 * They are published so this client can *label* its input and its results with the server's own
 * numbers instead of restating them. No caller can raise one: the query request carries a
 * statement and nothing else - no page size, no row count and no timeout - so there is no input
 * here that could widen anything.
 */
export interface PlatformDiagnosticsLimits {
  maxSqlBytes: number;
  maxRows: number;
  maxResponseBytes: number;
  maxDurationSeconds: number;
}

/**
 * The capability signal from `GET /api/admin/diagnostics/access`.
 *
 * Reaching it at all *is* the signal: the `PlatformDiagnostics` policy has already refused
 * everyone else, and the body carries no business, row, count, name or identifier. It is not a
 * client-side role source and must never be treated as one - the policy is satisfied only by the
 * separately configured Entra `(tid, oid)` pair, and `POST .../query` re-checks it on every
 * request.
 */
export interface PlatformDiagnosticsAccess {
  authorized: boolean;
  crossBusinessScope: boolean;
  limits: PlatformDiagnosticsLimits;
}

/** How a query ended, as the API reports it (`DiagnosticsQueryOutcome`). */
export type PlatformDiagnosticsOutcome = 'Succeeded' | 'Truncated' | 'Rejected' | 'TimedOut' | 'Cancelled' | 'Failed';

/** Which server-side cap stopped a truncated read (`DiagnosticsTruncationReason`). */
export type PlatformDiagnosticsTruncationReason = 'RowLimit' | 'ResponseByteLimit';

/**
 * The bounded result of one diagnostics query, matching
 * `InventoryApi.DTOs.PlatformDiagnosticsQueryResponse` field for field.
 *
 * Values arrive as strings because the permitted surface is identity and foreign-key columns, not
 * a financial contract: nothing is calculated from them here or anywhere else in the frontend.
 * `truncated`/`truncationReason` are the contract's promise that a prefix is never presented as a
 * complete answer, and the API sends this same body with a non-success status for a refusal, a
 * timeout and a provider failure, so the outcome is always read from the body rather than
 * inferred from the status code.
 */
export interface PlatformDiagnosticsQueryResult {
  outcome: PlatformDiagnosticsOutcome | string;
  denialReason: string | null;
  message: string | null;
  crossBusinessScope: boolean;
  columns: string[];
  rows: (string | null)[][];
  rowCount: number;
  truncated: boolean;
  truncationReason: PlatformDiagnosticsTruncationReason | string | null;
  durationMilliseconds: number;
  queryFingerprint: string;
}

/**
 * The client for the two read-only platform diagnostics endpoints (issue #336). It holds no
 * authority of its own: the API authorizes both requests against the configured platform-admin
 * identity, enforces the permitted table/column surface inside SQLite, and caps duration, rows,
 * response bytes and submitted SQL. This client submits a statement and reports what came back.
 */
@Injectable({ providedIn: 'root' })
export class PlatformDiagnosticsService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/admin/diagnostics`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /** The capability signal. A `403` means this actor is not the configured platform administrator. */
  access(): Observable<PlatformDiagnosticsAccess> {
    return this.http.get<PlatformDiagnosticsAccess>(`${this.baseUrl}/access`);
  }

  /**
   * Runs one read-only statement against the permitted diagnostics surface. The SQL is sent and
   * never stored: nothing here writes it to browser storage, a URL or a log.
   */
  query(sql: string): Observable<PlatformDiagnosticsQueryResult> {
    return this.http.post<PlatformDiagnosticsQueryResult>(`${this.baseUrl}/query`, { sql });
  }
}
