import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { Receipt, ReceiptItem, ReceiptResponse, ReceiptValidation } from '../models/models';
import { ConfigService } from './config.service';

export interface ReceiptUploadPayload {
  file: File;
  title: string;
  notes?: string | null;
  totalAmount?: number | null;
  deliveryCost?: number | null;
  packageCost?: number | null;
  purchaseDate?: string | null;
  supplierId?: number | null;
  items?: ReceiptItemPayload[];
}

export interface ReceiptItemPayload {
  productId: number;
  quantity: number;
  unitCost: number;
}

export type ReceiptUpdatePayload = Omit<ReceiptUploadPayload, 'file'>;

@Injectable({ providedIn: 'root' })
export class ReceiptService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/receipts`;
  }

  private lastValidation: ReceiptValidation | null = null;
  private validationsByReceiptId: Map<number, ReceiptValidation | null> = new Map();

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(supplierId?: number): Observable<Receipt[]> {
    const url = supplierId ? `${this.baseUrl}?supplierId=${supplierId}` : this.baseUrl;
    return this.http.get<ReceiptResponse[]>(url).pipe(
      map(responses => {
        // Store validations for each receipt
        responses.forEach(r => {
          this.validationsByReceiptId.set(r.receipt.id, r.validation ?? null);
        });
        return responses.map(r => r.receipt);
      })
    );
  }

  get(id: number): Observable<Receipt> {
    return this.http.get<ReceiptResponse>(`${this.baseUrl}/${id}`).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.validationsByReceiptId.set(response.receipt.id, response.validation ?? null);
        return response.receipt;
      })
    );
  }

  getValidation(): ReceiptValidation | null {
    return this.lastValidation;
  }

  getValidationFor(receiptId: number): ReceiptValidation | null {
    return this.validationsByReceiptId.get(receiptId) ?? null;
  }

  /**
   * The document endpoint is behind the API's authorization boundary, so it must be fetched
   * through HttpClient: only then does MsalInterceptor attach the bearer token. A direct
   * `<a href>`/`<img src>` to this URL is an unauthenticated browser request and gets 401.
   */
  getFile(id: number): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${id}/file`, { responseType: 'blob' });
  }

  upload(payload: ReceiptUploadPayload): Observable<Receipt> {
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

    return this.http.post<ReceiptResponse>(this.baseUrl, formData).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.validationsByReceiptId.set(response.receipt.id, response.validation ?? null);
        return response.receipt;
      })
    );
  }

  update(id: number, payload: ReceiptUpdatePayload): Observable<Receipt> {
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

    return this.http.put<ReceiptResponse>(`${this.baseUrl}/${id}`, formData).pipe(
      map(response => {
        this.lastValidation = response.validation ?? null;
        this.validationsByReceiptId.set(response.receipt.id, response.validation ?? null);
        return response.receipt;
      })
    );
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

}
