import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Receipt } from '../models/models';
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
}

export type ReceiptUpdatePayload = Omit<ReceiptUploadPayload, 'file'>;

@Injectable({ providedIn: 'root' })
export class ReceiptService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/receipts`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(supplierId?: number): Observable<Receipt[]> {
    const url = supplierId ? `${this.baseUrl}?supplierId=${supplierId}` : this.baseUrl;
    return this.http.get<Receipt[]>(url);
  }

  get(id: number): Observable<Receipt> {
    return this.http.get<Receipt>(`${this.baseUrl}/${id}`);
  }

  fileUrl(id: number): string {
    return `${this.baseUrl}/${id}/file`;
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

    return this.http.post<Receipt>(this.baseUrl, formData);
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

    return this.http.put<Receipt>(`${this.baseUrl}/${id}`, formData);
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
