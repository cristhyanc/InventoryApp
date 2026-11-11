import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * One business's receipt-scan usage as
 * `GET /api/super-admin/businesses/{businessId}/purchase-scan-usage` reports it (issue #1164). The
 * route is named `purchase-scan`, never `receipt-scan`, by issue #127's rule that no published
 * schema, tag or vocabulary carries a Receipt name.
 *
 * `month` is the business's **own** calendar month as `yyyy-MM`, derived on the server in the time
 * zone stored on that business - not the administrator's browser's month and not UTC's. It is
 * reported because a count shown without its month is a count against an unknown cap.
 *
 * Nothing here is about any receipt: there is no file name, product, supplier, amount or instant.
 */
export interface SuperAdminReceiptScanUsage {
  readonly businessId: number;
  readonly month: string;
  readonly used: number;
  readonly remaining: number;
  readonly limit: number;
}

/**
 * The frontend client for one business's monthly receipt-scan count (issue #1164), consumed by the
 * Super Admin Businesses page's **Receipt scans** card.
 *
 * It is a thin wrapper over one read, and there is deliberately **no write of any kind**: buying
 * extra scans and resetting a counter are out of scope on issue #1164, and the server has no route
 * that would accept either.
 */
@Injectable({ providedIn: 'root' })
export class SuperAdminReceiptScanUsageService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /** That business's month, what it has used in it, what is left and the cap. */
  get(businessId: number): Observable<SuperAdminReceiptScanUsage> {
    return this.http.get<SuperAdminReceiptScanUsage>(
      `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/businesses/${businessId}/purchase-scan-usage`
    );
  }
}
