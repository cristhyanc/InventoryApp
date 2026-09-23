import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

export enum OperatingExpenseCategory {
  NayaxMonthlyFee,
  Insurance,
  RepairsAndMaintenance,
  Software,
  Accounting,
  PhoneInternet,
  VehicleTravel,
  BankFees,
  Other
}

export interface OperatingExpense {
  id: number;
  expenseDate: string;
  category: OperatingExpenseCategory;
  description: string;
  amountExGst: number;
  gstAmount: number;
  totalAmount: number;
  supplierId?: number | null;
  supplierName?: string | null;
  siteId?: number | null;
  machineId?: number | null;
  attachmentFileName?: string | null;
  attachmentStoredFileName?: string | null;
  attachmentContentType?: string | null;
  attachmentFileSizeBytes?: number | null;
  servicePeriodStart?: string | null;
  servicePeriodEnd?: string | null;
  notes?: string | null;
}

export interface OperatingExpensePayload {
  expenseDate: string;
  category: OperatingExpenseCategory;
  description: string;
  amountExGst: number;
  gstAmount: number;
  totalAmount: number;
  supplierId?: number | null;
  siteId?: number | null;
  machineId?: number | null;
  servicePeriodStart?: string | null;
  servicePeriodEnd?: string | null;
  notes?: string | null;
}

@Injectable({ providedIn: 'root' })
export class OperatingExpenseService {
  private get baseUrl(): string { return `${this.config.apiBaseUrl.replace(/\/$/, '')}/operating-expenses`; }
  constructor(private http: HttpClient, private config: ConfigService) {}
  getAll(filters: { from?: string; to?: string; category?: OperatingExpenseCategory; supplierId?: number }): Observable<OperatingExpense[]> {
    let params = new HttpParams();
    if (filters.from) params = params.set('from', filters.from);
    if (filters.to) params = params.set('to', filters.to);
    if (filters.category !== undefined) params = params.set('category', filters.category);
    if (filters.supplierId !== undefined) params = params.set('supplierId', filters.supplierId);
    return this.http.get<OperatingExpense[]>(this.baseUrl, { params });
  }
  create(payload: OperatingExpensePayload, attachment?: File | null): Observable<OperatingExpense> {
    return attachment
      ? this.http.post<OperatingExpense>(this.baseUrl, this.toFormData(payload, attachment))
      : this.http.post<OperatingExpense>(this.baseUrl, payload);
  }

  update(id: number, payload: OperatingExpensePayload, attachment?: File | null): Observable<OperatingExpense> {
    return attachment
      ? this.http.put<OperatingExpense>(`${this.baseUrl}/${id}`, this.toFormData(payload, attachment))
      : this.http.put<OperatingExpense>(`${this.baseUrl}/${id}`, payload);
  }

  delete(id: number): Observable<void> { return this.http.delete<void>(`${this.baseUrl}/${id}`); }
  /**
   * The attachment endpoint is behind the API's authorization boundary, so it must be
   * fetched through HttpClient: only then does MsalInterceptor attach the bearer token. A
   * direct `<a href>` to this URL is an unauthenticated browser request and gets 401.
   */
  getAttachment(id: number): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${id}/attachment`, { responseType: 'blob' });
  }

  private toFormData(payload: OperatingExpensePayload, attachment: File): FormData {
    const formData = new FormData();
    formData.append('expenseDate', payload.expenseDate);
    formData.append('category', String(payload.category));
    formData.append('description', payload.description);
    formData.append('amountExGst', String(payload.amountExGst));
    formData.append('gstAmount', String(payload.gstAmount));
    formData.append('totalAmount', String(payload.totalAmount));
    if (payload.supplierId != null) formData.append('supplierId', String(payload.supplierId));
    if (payload.siteId != null) formData.append('siteId', String(payload.siteId));
    if (payload.machineId != null) formData.append('machineId', String(payload.machineId));
    if (payload.servicePeriodStart) formData.append('servicePeriodStart', payload.servicePeriodStart);
    if (payload.servicePeriodEnd) formData.append('servicePeriodEnd', payload.servicePeriodEnd);
    if (payload.notes != null) formData.append('notes', payload.notes);
    formData.append('attachment', attachment);
    return formData;
  }
}
