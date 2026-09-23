import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { Purchase, PurchaseResponse, PurchaseValidation } from '../models/models';
import { ConfigService } from './config.service';

export interface PurchaseUploadPayload {
  file: File;
  title: string;
  notes?: string | null;
  totalAmount?: number | null;
  deliveryCost?: number | null;
  packageCost?: number | null;
  purchaseDate?: string | null;
  supplierId?: number | null;
  items?: PurchaseItemPayload[];
}

export interface PurchaseItemPayload {
  productId: number;
  quantity: number;
  unitCost: number;
}

export type PurchaseUpdatePayload = Omit<PurchaseUploadPayload, 'file'>;

@Injectable({ providedIn: 'root' })
export class PurchaseService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/purchases`;
  }

  private lastValidation: PurchaseValidation | null = null;
  private validationsByPurchaseId: Map<number, PurchaseValidation | null> = new Map();

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(supplierId?: number): Observable<Purchase[]> {
    const url = supplierId ? `${this.baseUrl}?supplierId=${supplierId}` : this.baseUrl;
    return this.http.get<PurchaseResponse[]>(url).pipe(
      map(responses => {
        // Store validations for each purchase
        responses.forEach(r => {
          this.validationsByPurchaseId.set(r.purchase.id, r.validation ?? null);
        });
        return responses.map(r => r.purchase);
      })
    );
  }

  get(id: number): Observable<Purchase> {
    return this.http.get<PurchaseResponse>(`${this.baseUrl}/${id}`).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.validationsByPurchaseId.set(response.purchase.id, response.validation ?? null);
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
    formData.append('title', payload.title);
    if (payload.notes !== undefined && payload.notes !== null)
      formData.append('notes', payload.notes);
    if (payload.totalAmount !== undefined && payload.totalAmount !== null)
      formData.append('totalAmount', String(payload.totalAmount));
    if (payload.deliveryCost !== undefined && payload.deliveryCost !== null)
      formData.append('deliveryCost', String(payload.deliveryCost));
    if (payload.packageCost !== undefined && payload.packageCost !== null)
      formData.append('packageCost', String(payload.packageCost));
    if (payload.purchaseDate) formData.append('purchaseDate', payload.purchaseDate);
    if (payload.supplierId !== undefined && payload.supplierId !== null)
      formData.append('supplierId', String(payload.supplierId));
    formData.append('items', JSON.stringify(payload.items ?? []));

    return this.http.post<PurchaseResponse>(this.baseUrl, formData).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.validationsByPurchaseId.set(response.purchase.id, response.validation ?? null);
        return response.purchase;
      })
    );
  }

  update(id: number, payload: PurchaseUpdatePayload): Observable<Purchase> {
    const formData = new FormData();
    formData.append('title', payload.title);
    if (payload.notes !== undefined && payload.notes !== null)
      formData.append('notes', payload.notes);
    if (payload.totalAmount !== undefined && payload.totalAmount !== null)
      formData.append('totalAmount', String(payload.totalAmount));
    if (payload.deliveryCost !== undefined && payload.deliveryCost !== null)
      formData.append('deliveryCost', String(payload.deliveryCost));
    if (payload.packageCost !== undefined && payload.packageCost !== null)
      formData.append('packageCost', String(payload.packageCost));
    if (payload.purchaseDate) formData.append('purchaseDate', payload.purchaseDate);
    if (payload.supplierId !== undefined && payload.supplierId !== null)
      formData.append('supplierId', String(payload.supplierId));
    if (payload.items !== undefined) formData.append('items', JSON.stringify(payload.items));

    return this.http.put<PurchaseResponse>(`${this.baseUrl}/${id}`, formData).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.validationsByPurchaseId.set(response.purchase.id, response.validation ?? null);
        return response.purchase;
      })
    );
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

}
