import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * What the synchronization answered (issue #843). The endpoint has two success answers and they
 * mean different things to the Dashboard: `204` is "it ran", and `200` with a body is "this business
 * synchronized less than three minutes ago, so nothing was read" - which is not a failure and not
 * the `429` run limit either.
 *
 * `lastSyncedAtUtc` is the server's own instant and is only carried by the skip answer; for a run,
 * the caller knows when it finished because it was waiting for it.
 */
export interface NayaxSalesSyncResult {
  /** Whether the server skipped the run because the sales were already recent. */
  wasRecentlySynced: boolean;
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
   */
  syncLatest(force = false): Observable<NayaxSalesSyncResult> {
    const url = force ? `${this.baseUrl}?force=true` : this.baseUrl;
    return this.http.post<NayaxSalesRecentlySyncedBody | null>(url, {}).pipe(
      // A `204` arrives as a null body: the run happened, and the caller knows when.
      map((body) => ({
        wasRecentlySynced: body?.wasRecentlySynced === true,
        lastSyncedAtUtc: body?.lastSyncedAtUtc ?? null
      }))
    );
  }
}
