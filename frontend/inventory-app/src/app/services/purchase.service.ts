import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { GstClassification, Purchase, PurchaseGstSummary, PurchaseResponse, PurchaseValidation } from '../models/models';
import { ConfigService } from './config.service';

export interface PurchaseUploadPayload {
  file: File;
  title: string;
  notes?: string | null;
  totalAmount?: number | null;
  deliveryCost?: number | null;
  /**
   * The delivery charge's GST classification (issue #431). Left `undefined` the field is not sent at
   * all, which is how an edit keeps the stored classification and its provenance; sending a value
   * records the person's explicit choice as `Manual` on the server.
   */
  deliveryGstClassification?: GstClassification;
  packageCost?: number | null;
  /** The package charge's GST classification; see `deliveryGstClassification`. */
  packageGstClassification?: GstClassification;
  purchaseDate?: string | null;
  supplierId?: number | null;
  items?: PurchaseItemPayload[];
}

export interface PurchaseItemPayload {
  /**
   * The stored line's own stable id, as a purchase read returns it. It is what keeps each line's
   * classification on the right line when a purchase holds several lines for one product and the
   * lines are reordered or removed (issue #429). A new line omits it.
   */
  id?: number;
  productId: number;
  quantity: number;
  unitCost: number;
  /**
   * This line's GST classification, sent only when the person changed it (issue #431). Omitting it
   * leaves a stored line's classification and provenance untouched and leaves a new line
   * unclassified.
   */
  gstClassification?: GstClassification;
}

export type PurchaseUpdatePayload = Omit<PurchaseUploadPayload, 'file'>;

@Injectable({ providedIn: 'root' })
export class PurchaseService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/purchases`;
  }

  private lastValidation: PurchaseValidation | null = null;
  private validationsByPurchaseId: Map<number, PurchaseValidation | null> = new Map();
  private gstSummariesByPurchaseId: Map<number, PurchaseGstSummary | null> = new Map();

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(supplierId?: number): Observable<Purchase[]> {
    const url = supplierId ? `${this.baseUrl}?supplierId=${supplierId}` : this.baseUrl;
    return this.http.get<PurchaseResponse[]>(url).pipe(
      map(responses => {
        // Store the validation and GST blocks for each purchase
        responses.forEach(r => this.rememberEnvelope(r));
        return responses.map(r => r.purchase);
      })
    );
  }

  get(id: number): Observable<Purchase> {
    return this.http.get<PurchaseResponse>(`${this.baseUrl}/${id}`).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.rememberEnvelope(response);
        return response.purchase;
      })
    );
  }

  getValidation(): PurchaseValidation | null {
    return this.lastValidation;
  }

  getValidationFor(purchaseId: number): PurchaseValidation | null {
    return this.validationsByPurchaseId.get(purchaseId) ?? null;
  }

  /**
   * The saved input-GST summary the API returned for this purchase (issue #431). It is the server's
   * calculation, kept exactly as received: nothing here derives GST from an amount. `null` means the
   * response carried no summary, which must stay visible as unavailable rather than be shown as $0.
   */
  getGstSummaryFor(purchaseId: number): PurchaseGstSummary | null {
    return this.gstSummariesByPurchaseId.get(purchaseId) ?? null;
  }

  /**
   * The document endpoint is behind the API's authorization boundary, so it must be fetched
   * through HttpClient: only then does MsalInterceptor attach the bearer token. A direct
   * `<a href>`/`<img src>` to this URL is an unauthenticated browser request and gets 401.
   */
  getFile(id: number): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${id}/file`, { responseType: 'blob' });
  }

  upload(payload: PurchaseUploadPayload): Observable<Purchase> {
    const formData = new FormData();
    formData.append('file', payload.file);
    this.appendPurchaseFormFields(formData, payload);
    formData.append('items', JSON.stringify(payload.items ?? []));

    return this.http.post<PurchaseResponse>(this.baseUrl, formData).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.rememberEnvelope(response);
        return response.purchase;
      })
    );
  }

  update(id: number, payload: PurchaseUpdatePayload): Observable<Purchase> {
    const formData = new FormData();
    this.appendPurchaseFormFields(formData, payload);
    // JSON.stringify drops a line's undefined `id`/`gstClassification`, which is exactly the
    // omission the server reads as "not submitted".
    if (payload.items !== undefined) formData.append('items', JSON.stringify(payload.items));

    return this.http.put<PurchaseResponse>(`${this.baseUrl}/${id}`, formData).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.rememberEnvelope(response);
        return response.purchase;
      })
    );
  }

  /** The title/notes/amount/charge/GST/date/supplier fields shared by a create and an update multipart request. */
  private appendPurchaseFormFields(formData: FormData, payload: Omit<PurchaseUploadPayload, 'file' | 'items'>): void {
    formData.append('title', payload.title);
    if (payload.notes !== undefined && payload.notes !== null)
      formData.append('notes', payload.notes);
    if (payload.totalAmount !== undefined && payload.totalAmount !== null)
      formData.append('totalAmount', String(payload.totalAmount));
    if (payload.deliveryCost !== undefined && payload.deliveryCost !== null)
      formData.append('deliveryCost', String(payload.deliveryCost));
    if (payload.deliveryGstClassification !== undefined && payload.deliveryGstClassification !== null)
      formData.append('deliveryGstClassification', String(payload.deliveryGstClassification));
    if (payload.packageCost !== undefined && payload.packageCost !== null)
      formData.append('packageCost', String(payload.packageCost));
    if (payload.packageGstClassification !== undefined && payload.packageGstClassification !== null)
      formData.append('packageGstClassification', String(payload.packageGstClassification));
    if (payload.purchaseDate) formData.append('purchaseDate', payload.purchaseDate);
    if (payload.supplierId !== undefined && payload.supplierId !== null)
      formData.append('supplierId', String(payload.supplierId));
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

  private rememberEnvelope(response: PurchaseResponse): void {
    this.validationsByPurchaseId.set(response.purchase.id, response.validation ?? null);
    this.gstSummariesByPurchaseId.set(response.purchase.id, response.gst ?? null);
  }
}
