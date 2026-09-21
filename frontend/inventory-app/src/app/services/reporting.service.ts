import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';
import { NayaxProcessingFeeResult, ReportQuality, ReportingFilter } from '../features/reports/shared/models/reporting.models';
import { BookkeepingReport } from '../features/reports/bookkeeping/models/bookkeeping-report.model';
import { DashboardReport } from '../features/reports/dashboard/models/dashboard-report.model';
import { DailyReport, DailyRow, DailyTotals } from '../features/reports/daily/models/daily-report.model';
import {
  ReconciliationPeriod, ReconciliationReport, ReconciliationTotals,
} from '../features/reports/reconciliation/models/reconciliation-report.model';
import { MachineReport, MachineRow } from '../features/reports/machine-profitability/models/machine-report.model';
import { ProductReport, ProductRow } from '../features/reports/product-profitability/models/product-report.model';
import { GstReport } from '../features/reports/gst/models/gst-report.model';
import {
  TransactionSalesFilter, TransactionSalesFilterOption, TransactionSalesReport,
  TransactionSalesRow, TransactionSalesTotals,
} from '../features/reports/transactions/models/transaction-sales-report.model';

export {
  ReportingFilter, ReportQuality, NayaxProcessingFeeResult,
  BookkeepingReport, DashboardReport, DailyRow, DailyTotals, DailyReport,
  ReconciliationPeriod, ReconciliationTotals, ReconciliationReport,
  MachineRow, MachineReport, ProductRow, ProductReport, GstReport,
  TransactionSalesFilter, TransactionSalesFilterOption, TransactionSalesRow, TransactionSalesTotals, TransactionSalesReport,
};

export interface SiteCommissionMachine { machineId: number; machineName: string; transactionCount: number; grossSales: number; cardSales: number; cashSales: number; eligibleSales: number; commissionDue: number; }
export interface SiteCommissionProduct { productName: string; totalVends: number; totalSales: number; }
export interface CommissionPayment { id: number; siteId: number; periodStart: string; periodEnd: string; paymentDate: string; amount: number; notes?: string; }
export interface SiteCommissionRow { siteId: number; siteName: string; periodStart: string; periodEnd: string; frequency: string; basis: string; grossSales: number; cardSales: number; cashSales: number; eligibleSales: number; commissionRate: number; commissionDue: number; paid: number; outstanding: number; dueDate?: string; status: string; machines: SiteCommissionMachine[]; products: SiteCommissionProduct[]; payments: CommissionPayment[]; dataQuality?: string; }
export interface SiteCommissionReport { from: string; to: string; rows: SiteCommissionRow[]; }
export interface SiteCommissionAgreement { id?: number; siteId: number; effectiveFrom: string; effectiveTo?: string | null; commissionRate: number; frequency: number; basis: number; paymentDueDaysAfterPeriodEnd?: number | null; }
export interface SaleCostingBackfillResult {
  costedCount: number;
  legacyEstimatedCount: number;
  pendingCount: number;
  errorCount: number;
  alreadyFinalizedCount: number;
  dryRun: boolean;
}
export interface NayaxCostBackfillResult {
  salesReviewed: number;
  salesWithNayaxCost: number;
  salesWouldBeCosted: number;
  salesAlreadyCosted: number;
  salesStillPending: number;
  invalidCostRows: number;
  errorRows: number;
  dryRun: boolean;
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
  transactions(filter: TransactionSalesFilter): Observable<TransactionSalesReport> {
    return this.http.get<TransactionSalesReport>(`${this.baseUrl}/transactions`, { params: this.params(filter) });
  }
  siteCommissions(filter: ReportingFilter, siteId?: number | null): Observable<SiteCommissionReport> {
    let params = this.params(filter);
    if (siteId != null) params = params.set('siteId', siteId);
    return this.http.get<SiteCommissionReport>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/site-commissions`, { params });
  }
  recordCommissionPayment(siteId: number, periodStart: string, periodEnd: string, paymentDate: string, amount: number, notes?: string): Observable<CommissionPayment> {
    return this.http.post<CommissionPayment>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/site-commissions/${siteId}/payments`,
      { paymentDate, amount, notes: notes || null }, { params: { periodStart, periodEnd } });
  }
  saveSiteCommissionAgreement(agreement: SiteCommissionAgreement): Observable<SiteCommissionAgreement> {
    return this.http.post<SiteCommissionAgreement>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/site-commissions/agreements`, agreement);
  }
  siteCommissionAgreements(): Observable<SiteCommissionAgreement[]> {
    return this.http.get<SiteCommissionAgreement[]>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/site-commissions/agreements`);
  }
  gst(filter: ReportingFilter): Observable<GstReport> { return this.http.get<GstReport>(`${this.baseUrl}/gst`, { params: this.params(filter) }); }

  backfillSaleCosts(dryRun = true, force = false): Observable<SaleCostingBackfillResult> {
    const url = `${this.config.apiBaseUrl.replace(/\/$/, '')}/sale-costing/backfill`;
    return this.http.post<SaleCostingBackfillResult>(url, null, {
      params: { dryRun, force }
    });
  }

  backfillNayaxSaleCosts(dryRun = true): Observable<NayaxCostBackfillResult> {
    const action = dryRun ? 'dry-run' : 'apply';
    const url = `${this.config.apiBaseUrl.replace(/\/$/, '')}/sale-costing/nayax-cost-backfill/${action}`;
    return this.http.post<NayaxCostBackfillResult>(url, null);
  }

  export(report: string, format: 'csv' | 'xlsx', filter: ReportingFilter | TransactionSalesFilter): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${report}/export`, { params: this.params(filter).set('format', format), responseType: 'blob' });
  }

  private params(filter: ReportingFilter | TransactionSalesFilter): HttpParams {
    let params = new HttpParams();
    if (filter.from) params = params.set('from', filter.from);
    if (filter.to) params = params.set('to', filter.to);
    if (filter.machineId != null) params = params.set('machineId', filter.machineId);
    if ('financialYear' in filter && filter.financialYear) params = params.set('financialYear', filter.financialYear);
    if ('siteId' in filter && filter.siteId != null) params = params.set('siteId', filter.siteId);
    if ('productId' in filter && filter.productId != null) params = params.set('productId', filter.productId);
    if ('paymentType' in filter && filter.paymentType) params = params.set('paymentType', filter.paymentType);
    if ('status' in filter && filter.status) params = params.set('status', filter.status);
    if ('cogsStatus' in filter && filter.cogsStatus) params = params.set('cogsStatus', filter.cogsStatus);
    if ('search' in filter && filter.search) params = params.set('search', filter.search);
    if ('page' in filter && filter.page != null) params = params.set('page', filter.page);
    if ('pageSize' in filter && filter.pageSize != null) params = params.set('pageSize', filter.pageSize);
    if ('sortBy' in filter && filter.sortBy) params = params.set('sortBy', filter.sortBy);
    if ('sortDescending' in filter && filter.sortDescending != null) params = params.set('sortDescending', filter.sortDescending);
    return params;
  }
}
