import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

export interface ReportingFilter {
  from?: string;
  to?: string;
  machineId?: number | null;
  financialYear?: string;
}

export interface ReportQuality {
  missingStatus: boolean;
  historicalCostUnavailable: boolean;
  gstClassificationMissing: boolean;
  commissionNotPersisted: boolean;
  containsUnmappedProducts: boolean;
  notes?: string[];
}

export interface DashboardReport {
  from: string; to: string; sales: number; grossProfit: number; transactions: number;
  quantity: number; machineCount: number; productCount: number; unmappedProductCount: number;
  dataQuality: ReportQuality;
  nayaxFeesExGst?: number; netReimbursement?: number; siteCommission?: number;
  netProfit?: number; netMarginPercent?: number;
  deliveryCosts?: number; packageCosts?: number; otherOperatingExpenses?: number;
}
export interface BookkeepingReport {
  from: string; to: string; financialYear: string; sales: number; costOfGoods: number;
  grossProfit: number; fees: number; netSettlement: number; gstOnSales: number; gstOnFees: number;
  dataQuality: ReportQuality;
  siteCommission?: number; netProfit?: number; netMarginPercent?: number;
  nayaxFeesExGst?: number; nayaxFeesIncludingGst?: number;
  deliveryCosts?: number; packageCosts?: number; otherOperatingExpenses?: number;
}
export interface DailyRow { date: string; sales: number; quantity: number; costOfGoods: number; grossProfit: number; transactionCount: number; }
export interface DailyReport { from: string; to: string; rows: DailyRow[]; dataQuality: ReportQuality; }
export interface ReconciliationReport {
  from: string; to: string; nayaxSales: number; importedReimbursement: number; difference: number;
  tolerance: number; isMatch: boolean; dataQuality: ReportQuality;
}
export interface MachineRow {
  machineId: number; machineName: string; sales: number; quantity: number; costOfGoods: number;
  grossProfit: number; marginPercent: number; transactionCount: number;
  siteCommission?: number; netProfit?: number; netMarginPercent?: number; commissionPercent?: number;
}
export interface MachineReport { from: string; to: string; rows: MachineRow[]; dataQuality: ReportQuality; }
export interface ProductRow {
  productId: number | null; productName: string; categoryName?: string; sales: number; quantity: number;
  costOfGoods: number; grossProfit: number; marginPercent: number; transactionCount: number;
  isUnmapped: boolean; historicalCostAvailable: boolean;
}
export interface ProductReport { from: string; to: string; rows: ProductRow[]; dataQuality: ReportQuality; }
export interface GstReport {
  from: string; to: string; taxableSales: number; gstOnSales: number; taxableFees: number;
  gstOnFees: number; netGst: number; dataQuality: ReportQuality;
}

@Injectable({ providedIn: 'root' })
export class ReportingService {
  private get baseUrl(): string { return `${this.config.apiBaseUrl.replace(/\/$/, '')}/reports`; }
  constructor(private http: HttpClient, private config: ConfigService) {}

  dashboard(filter: ReportingFilter): Observable<DashboardReport> { return this.http.get<DashboardReport>(`${this.baseUrl}/dashboard`, { params: this.params(filter) }); }
  bookkeeping(filter: ReportingFilter): Observable<BookkeepingReport> { return this.http.get<BookkeepingReport>(`${this.baseUrl}/bookkeeping`, { params: this.params(filter) }); }
  daily(filter: ReportingFilter): Observable<DailyReport> { return this.http.get<DailyReport>(`${this.baseUrl}/daily`, { params: this.params(filter) }); }
  reconciliation(filter: ReportingFilter): Observable<ReconciliationReport> { return this.http.get<ReconciliationReport>(`${this.baseUrl}/reconciliation`, { params: this.params(filter) }); }
  machines(filter: ReportingFilter): Observable<MachineReport> { return this.http.get<MachineReport>(`${this.baseUrl}/machine-profitability`, { params: this.params(filter) }); }
  products(filter: ReportingFilter): Observable<ProductReport> { return this.http.get<ProductReport>(`${this.baseUrl}/product-profitability`, { params: this.params(filter) }); }
  gst(filter: ReportingFilter): Observable<GstReport> { return this.http.get<GstReport>(`${this.baseUrl}/gst`, { params: this.params(filter) }); }

  export(report: string, format: 'csv' | 'xlsx', filter: ReportingFilter): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${report}/export`, { params: this.params(filter).set('format', format), responseType: 'blob' });
  }

  private params(filter: ReportingFilter): HttpParams {
    let params = new HttpParams();
    if (filter.from) params = params.set('from', filter.from);
    if (filter.to) params = params.set('to', filter.to);
    if (filter.machineId) params = params.set('machineId', filter.machineId);
    if (filter.financialYear) params = params.set('financialYear', filter.financialYear);
    return params;
  }
}
