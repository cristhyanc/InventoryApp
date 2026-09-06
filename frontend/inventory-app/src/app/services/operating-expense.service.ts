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
  receiptId?: number | null;
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
  receiptId?: number | null;
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
  create(payload: OperatingExpensePayload): Observable<OperatingExpense> { return this.http.post<OperatingExpense>(this.baseUrl, payload); }
  update(id: number, payload: OperatingExpensePayload): Observable<OperatingExpense> { return this.http.put<OperatingExpense>(`${this.baseUrl}/${id}`, payload); }
  delete(id: number): Observable<void> { return this.http.delete<void>(`${this.baseUrl}/${id}`); }
}
