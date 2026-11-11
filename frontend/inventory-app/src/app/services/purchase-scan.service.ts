import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PurchaseScanDraft } from '../models/purchase-scan';
import { ConfigService } from './config.service';

/**
 * The frontend client for the receipt scan (`POST /api/purchases/scan`, issue #1163), read by the
 * Scan receipt workflow of Add Purchase (issue #1166).
 *
 * **It sends one file and returns the draft, and that is all it does.** Every figure in the answer -
 * the units, the unit cost, the levy's placement, the delivery and package charges, the receipt
 * total and what each line is still missing - is the backend's, and nothing here recalculates,
 * rounds or completes any of it. Nothing is stored by the request either: no file, no draft and no
 * purchase. The operator edits the suggestion and records it through the ordinary create.
 *
 * It is deliberately its own service rather than another method on `PurchaseService`: scanning
 * creates nothing, and the only thing it shares with the purchase writes is the controller it
 * happens to sit on.
 */
@Injectable({ providedIn: 'root' })
export class PurchaseScanService {
  private get scanUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/purchases/scan`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /**
   * The suggested draft for `file`.
   *
   * **Unsubscribing cancels the request**, which is what the workflow's Cancel does: Angular aborts
   * the underlying request, so the operator is not left waiting for an answer they no longer want.
   * The scan may still have been read and counted at the other end - a scan is counted when the
   * reader is called (issue #1164) - so Cancel stops the waiting, never the charge, and the
   * workflow says nothing about the month's allowance it cannot know.
   */
  scan(file: File): Observable<PurchaseScanDraft> {
    const formData = new FormData();
    formData.append('file', file);

    return this.http.post<PurchaseScanDraft>(this.scanUrl, formData);
  }
}
