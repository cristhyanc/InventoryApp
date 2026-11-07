import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, map, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ConfigService } from './config.service';

/**
 * What the synchronization answered (issue #843). The endpoint has two success answers and they
 * mean different things to the Dashboard: `204` is "it ran", and `200` with a body is "this business
 * synchronized less than three minutes ago, so nothing was read" - which is not a failure and not
 * the `429` run limit either.
 *
 * Since issue #1104 there is a third answer that is also not a failure: `409`, "a sync for this
 * business is already running". It reaches the caller as a skip for the reason the `200` does - the
 * figures loaded next are the ones the run in progress is producing - and {@link wasBusy} is what
 * tells the two apart for anything that needs to.
 *
 * `lastSyncedAtUtc` is the server's own instant and is only carried by the `200` skip; for a run,
 * the caller knows when it finished because it was waiting for it, and a `409` carries no instant at
 * all because the run that holds the lock has not finished yet.
 */
export interface NayaxSalesSyncResult {
  /**
   * Whether the server skipped the run - because the sales were already recent, or because a sync
   * for this business was already running.
   */
  wasRecentlySynced: boolean;
  /**
   * Whether the skip was a `409`: a sync for this business was already running (issue #1104). It is
   * reported rather than acted on - no caller retries on it, and nothing shows an error.
   */
  wasBusy: boolean;
  /** When the last fully successful synchronization finished, as a true instant, or `null`. */
  lastSyncedAtUtc: string | null;
}

/** The `200` body of a skipped run, exactly as `NayaxSalesRecentlySyncedResponse` publishes it. */
interface NayaxSalesRecentlySyncedBody {
  wasRecentlySynced?: boolean;
  lastSyncedAtUtc?: string;
}

/**
 * The explicit, shared latest-Nayax-sales synchronization call (issue #187). The home dashboard
 * calls this once before loading Sites and Machines so both sections read the same freshness
 * boundary, instead of Machines importing the latest transactions as a side effect of its own load.
 */
@Injectable({ providedIn: 'root' })
export class NayaxSalesSyncService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/nayax-sales-sync`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  /**
   * Synchronizes this business's latest Nayax sales.
   *
   * `force` is the Dashboard's Refresh control: it bypasses the server's three-minute recent-sync
   * skip (issue #843) and nothing else - the run limit and the connection refusals still apply. The
   * parameter is left off the URL entirely when it is false, so the ordinary on-load call is the
   * request it has always been.
   *
   * A `409` is turned into a skip here rather than left to every caller (issue #1104): a sync for
   * this business is already running, which is neither an error nor something to retry - retrying
   * would queue up behind a run that is already producing exactly the data this call wanted. Only
   * `409` is caught; `429` and every other failure still reach the caller's `error` handler
   * unchanged, so the run limit keeps its own notice and a real failure still looks like one.
   */
  syncLatest(force = false): Observable<NayaxSalesSyncResult> {
    const url = force ? `${this.baseUrl}?force=true` : this.baseUrl;
    return this.http.post<NayaxSalesRecentlySyncedBody | null>(url, {}).pipe(
      // A `204` arrives as a null body: the run happened, and the caller knows when.
      map((body) => ({
        wasRecentlySynced: body?.wasRecentlySynced === true,
        wasBusy: false,
        lastSyncedAtUtc: body?.lastSyncedAtUtc ?? null
      })),
      catchError((error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 409) {
          return of({ wasRecentlySynced: true, wasBusy: true, lastSyncedAtUtc: null });
        }
        throw error;
      })
    );
  }
}
