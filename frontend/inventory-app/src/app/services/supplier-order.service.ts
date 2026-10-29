import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  SupplierOrder,
  SupplierOrderCompletionPreview,
  SupplierOrderCompletionPreviewRequest,
  SupplierOrderCreateDto
} from '../models/models';
import { ConfigService } from './config.service';

@Injectable({ providedIn: 'root' })
export class SupplierOrderService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/supplierorders`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  getActive(): Observable<SupplierOrder[]> {
    return this.http.get<SupplierOrder[]>(this.baseUrl);
  }

  getById(id: number): Observable<SupplierOrder> {
    return this.http.get<SupplierOrder>(`${this.baseUrl}/${id}`);
  }

  create(payload: SupplierOrderCreateDto): Observable<SupplierOrder> {
    return this.http.post<SupplierOrder>(this.baseUrl, payload);
  }

  cancel(id: number): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${id}/cancel`, {});
  }

  /**
   * What the order would still be owed once the purchase on the form is applied (issue #974). It
   * writes nothing: the server projects the entered lines against the order's stored receipts with
   * its own allocation and outstanding rules, so this page never subtracts a receipt itself. The
   * fingerprint it returns is what a completion decision is submitted with.
   */
  completionPreview(
    id: number,
    request: SupplierOrderCompletionPreviewRequest
  ): Observable<SupplierOrderCompletionPreview> {
    return this.http.post<SupplierOrderCompletionPreview>(`${this.baseUrl}/${id}/completion-preview`, request);
  }
}