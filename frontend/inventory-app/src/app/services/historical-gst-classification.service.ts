import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * How many components of one kind the preview examined and what would become of them. Matches
 * `Inventory.Domain.Gst.HistoricalGstComponentCounts` (issue #433).
 */
export interface HistoricalGstComponentCounts {
  examined: number;
  becomingTaxable: number;
  becomingGstFree: number;
  stayingUnknown: number;
}

/**
 * What applying the configured product GST rules and supplier GST defaults to the unclassified
 * purchase history would do. Matches `Inventory.Domain.Gst.HistoricalGstClassificationSummary`
 * field-for-field, including its derived totals: the API calculates every one of them, and the
 * frontend performs no GST arithmetic of its own.
 */
export interface HistoricalGstClassificationSummary {
  productLines: HistoricalGstComponentCounts;
  deliveryCharges: HistoricalGstComponentCounts;
  packageCharges: HistoricalGstComponentCounts;
  purchasesExamined: number;
  lineGst: number;
  chargeGst: number;
  stayingUnknownAmount: number;
  componentsExamined: number;
  becomingTaxable: number;
  becomingGstFree: number;
  stayingUnknown: number;
  inputGst: number;
  classifiesAnything: boolean;
}

/**
 * The preview, and the fingerprint the apply must echo back. The fingerprint identifies the exact
 * purchase data, configured rules and business the summary was derived from; the server recomputes
 * it and refuses anything else, so it is carried back unchanged and never inspected or built here.
 */
export interface HistoricalGstClassificationPreview {
  summary: HistoricalGstClassificationSummary;
  fingerprint: string;
}

/** What the apply wrote: the server-recomputed summary it applied, and how many components it classified. */
export interface HistoricalGstClassificationApplied {
  summary: HistoricalGstClassificationSummary;
  componentsClassified: number;
}

@Injectable({ providedIn: 'root' })
export class HistoricalGstClassificationService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/admin/historical-gst-classification`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /**
   * Projects the configured rules onto the unclassified purchase history. Read-only on the server,
   * and a POST rather than a GET so the fingerprint it returns can never be served from a cache.
   */
  preview(): Observable<HistoricalGstClassificationPreview> {
    return this.http.post<HistoricalGstClassificationPreview>(`${this.baseUrl}/preview`, {});
  }

  /** Applies exactly the previewed classifications, echoing back the `fingerprint` it reported. */
  apply(preview: HistoricalGstClassificationPreview): Observable<HistoricalGstClassificationApplied> {
    return this.http.post<HistoricalGstClassificationApplied>(`${this.baseUrl}/apply`, {
      fingerprint: preview.fingerprint
    });
  }
}
