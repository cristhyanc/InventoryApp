import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Machine } from '../../models/models';
import { MachineService } from '../../services/machine.service';
import { ReportingService, TransactionSalesFilter, TransactionSalesReport, TransactionSalesRow } from '../../services/reporting.service';
import { ReportFiltersComponent } from './report-filters.component';
import { BusinessDateTimePipe } from '../../formatting/business-date-time.pipe';
import { IconComponent } from '../shared/icon.component';

@Component({
  selector: 'app-transaction-sales-report',
  standalone: true,
  imports: [CommonModule, FormsModule, ReportFiltersComponent, BusinessDateTimePipe, IconComponent],
  template: `
<div class="page">
  <header class="page-header">
    <h1 class="page-title">Transaction Sales</h1>
    <div class="page-actions">
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('csv')"><app-icon name="download" [size]="18" />Export CSV</button>
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('xlsx')"><app-icon name="download" [size]="18" />Export XLSX</button>
    </div>
  </header>
  <app-report-filters [from]="filter.from!" [to]="filter.to!" [machineId]="filter.machineId ?? null" [machines]="machines" [period]="period"
    (fromChange)="filter.from=$event" (toChange)="filter.to=$event" (machineChange)="filter.machineId=$event" (periodChange)="selectPeriod($event)" (apply)="apply()" />
  <div class="card card-body grid gap-3 md:grid-cols-3 xl:grid-cols-6">
    <label class="field-label">Site<select class="mt-1" [(ngModel)]="filter.siteId"><option [ngValue]="null">All sites</option>@for (site of report?.filterOptions?.sites ?? []; track site.id) {<option [ngValue]="site.id">{{ site.name }}</option>}</select></label>
    <label class="field-label">Product<select class="mt-1" [(ngModel)]="filter.productId"><option [ngValue]="null">All products</option>@for (product of report?.filterOptions?.products ?? []; track product.id) {<option [ngValue]="product.id">{{ product.name }}</option>}</select></label>
    <label class="field-label">Payment<select class="mt-1" [(ngModel)]="filter.paymentType"><option value="">All payments</option><option value="card">Card</option><option value="cash">Cash</option><option value="unknown">Unknown</option></select></label>
    <label class="field-label">Status<select class="mt-1" [(ngModel)]="filter.status"><option value="completed">Completed</option><option value="pending">Pending</option><option value="refunded">Refunded</option><option value="cancelled">Cancelled / declined</option><option value="unknown">Unknown</option><option value="all">All statuses</option></select></label>
    <label class="field-label">COGS<select class="mt-1" [(ngModel)]="filter.cogsStatus"><option value="">All COGS</option><option value="costed">Costed</option><option value="uncosted">Incomplete</option></select></label>
    <label class="field-label">Search<input class="mt-1" placeholder="ID, machine, product" [(ngModel)]="filter.search" (keyup.enter)="apply()" /></label>
    <div class="md:col-span-3 xl:col-span-6"><button type="button" class="btn btn-primary" (click)="apply()">Apply filters</button></div>
  </div>
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading transactions...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    @if (report.dataQuality.notes?.length) { <div class="alert alert-warning">@for (note of report.dataQuality.notes; track note) {<p>{{ note }}</p>}</div> }
    <section class="grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
      <div class="card card-body"><p class="stat-card-label">Sales</p><p class="font-semibold text-md-gray-800">{{ money(report.totals.sales) }}</p></div>
      <div class="card card-body"><p class="stat-card-label">COGS</p><p class="font-semibold" [class.text-md-warning-text]="!report.totals.isCogsComplete" [class.text-md-gray-800]="report.totals.isCogsComplete">{{ report.totals.isCogsComplete ? money(report.totals.costOfGoods) : 'Incomplete' }}</p></div>
      <div class="card card-body"><p class="stat-card-label">Gross Profit</p><p class="font-semibold" [class.text-md-warning-text]="report.totals.grossProfit == null" [class.text-md-gray-800]="report.totals.grossProfit != null">{{ moneyOrDash(report.totals.grossProfit) }}</p></div>
      <div class="card card-body"><p class="stat-card-label">Direct Profit</p><p class="font-semibold" [class.text-md-warning-text]="report.totals.directProfit == null" [class.text-md-gray-800]="report.totals.directProfit != null">{{ moneyOrDash(report.totals.directProfit) }}</p></div>
      <div class="card card-body"><p class="stat-card-label">Estimated fees (inc GST)</p><p class="font-semibold text-md-gray-800">{{ money(report.totals.estimatedFeeIncGst) }}</p></div>
    </section>
    <div class="card overflow-x-auto sm:-mx-6"><table class="table min-w-[900px]">
      <thead class="table-head"><tr>@for (column of columns; track column.key) {<th scope="col" class="table-cell px-2">@if (column.sortable) {<button type="button" class="font-bold uppercase hover:underline" (click)="sort(column.key)">{{ column.label }} @if (filter.sortBy === column.key) {<span>{{ filter.sortDescending ? '↓' : '↑' }}</span>}</button>} @else {<span>{{ column.label }}</span>}</th>}<th scope="col" class="table-cell px-2">Fees / commission</th></tr></thead>
      <tbody>@for (row of report.rows; track row.transactionId) {<tr class="table-row">
        <td class="table-cell px-2">{{ row.transactionDate | businessDateTime }}<div class="value-muted break-words">#{{ row.transactionId }}</div></td>
        <td class="table-cell px-2 break-words">{{ row.machineName }}<div class="value-muted break-words">{{ row.siteName || 'Site unavailable' }}</div></td>
        <td class="table-cell px-2 break-words">{{ row.productName }}</td>
        <td class="table-cell px-2 break-words">{{ row.paymentType }}<div class="value-muted break-words">{{ row.rawPaymentMethod || '—' }}</div></td>
        <td class="table-cell px-2">{{ money(row.sale) }}</td>
        <td class="table-cell px-2" [class.text-md-warning-text]="row.costOfGoods == null">{{ moneyOrDash(row.costOfGoods) }}<div class="value-muted break-words">{{ cogsSource(row) }}</div></td>
        <td class="table-cell px-2" [class.text-md-warning-text]="row.grossProfit == null">{{ moneyOrDash(row.grossProfit) }} @if (row.grossMarginPercent != null) {<div class="value-muted break-words">{{ row.grossMarginPercent | number:'1.1-1' }}%</div>}</td>
        <td class="table-cell px-2" [class.text-md-warning-text]="row.directProfit == null">{{ moneyOrDash(row.directProfit) }} @if (row.directMarginPercent != null) {<div class="value-muted break-words">{{ row.directMarginPercent | number:'1.1-1' }}%</div>}</td>
        <td class="table-cell px-2 break-words">{{ row.transactionStatus }}</td>
        <td class="table-cell px-2">{{ row.feeSource }}@if (row.feeIncGst != null) {<div class="value-muted break-words">{{ money(row.feeIncGst) }} inc GST</div>}@if (row.commissionAmount != null) {<div class="value-muted break-words">Commission {{ money(row.commissionAmount) }}{{ row.commissionBasis ? ' · ' + row.commissionBasis : '' }}</div>}</td>
      </tr>}</tbody>
    </table></div>
    <div class="flex flex-wrap items-center justify-between gap-3">
      <span class="value-muted">{{ report.totalCount }} transaction{{ report.totalCount === 1 ? '' : 's' }} · {{ report.totals.completedTransactionCount }} completed</span>
      <div class="flex items-center gap-2"><label class="value-muted">Rows <select class="w-auto" [(ngModel)]="filter.pageSize" (ngModelChange)="changePageSize($event)"><option [ngValue]="50">50</option><option [ngValue]="100">100</option><option [ngValue]="250">250</option></select></label><button type="button" class="btn btn-sm btn-secondary" [disabled]="report.page <= 1" (click)="goTo(report.page - 1)">Previous</button><span class="value-muted">Page {{ report.page }} / {{ pages }}</span><button type="button" class="btn btn-sm btn-secondary" [disabled]="report.page >= pages" (click)="goTo(report.page + 1)">Next</button></div>
    </div>
  }
</div>
`})
export class TransactionSalesReportComponent implements OnInit {
  machines: Machine[] = [];
  report: TransactionSalesReport | null = null;
  loading = false;
  error = '';
  period = 'thisMonth';
  filter: TransactionSalesFilter = { from: this.isoDate(new Date(new Date().getFullYear(), new Date().getMonth(), 1)), to: this.isoDate(new Date()), status: 'completed', page: 1, pageSize: 50, sortBy: 'date', sortDescending: true };
  columns = [{ key: 'date', label: 'Date / time', sortable: true }, { key: 'machine', label: 'Machine / site', sortable: true }, { key: 'product', label: 'Product', sortable: true }, { key: 'payment', label: 'Payment', sortable: false }, { key: 'sale', label: 'Sale', sortable: true }, { key: 'cogs', label: 'COGS', sortable: true }, { key: 'gross', label: 'Gross Profit', sortable: true }, { key: 'direct', label: 'Direct Profit', sortable: true }, { key: 'status', label: 'Status', sortable: true }];

  constructor(private reports: ReportingService, private machineService: MachineService) {}
  ngOnInit(): void { this.machineService.getAll().subscribe({ next: machines => this.machines = machines ?? [] }); this.load(); }
  get pages(): number { return this.report ? Math.max(1, Math.ceil(this.report.totalCount / this.report.pageSize)) : 1; }
  apply(): void { this.filter.page = 1; this.load(); }
  load(): void { this.loading = true; this.error = ''; this.reports.transactions(this.filter).subscribe({ next: value => { this.report = value; this.loading = false; }, error: () => { this.error = 'Unable to load transaction sales.'; this.loading = false; } }); }
  sort(key: string): void { this.filter.sortDescending = this.filter.sortBy === key ? !this.filter.sortDescending : key !== 'machine' && key !== 'product' && key !== 'status'; this.filter.sortBy = key; this.load(); }
  goTo(page: number): void { this.filter.page = page; this.load(); }
  changePageSize(size: number): void { this.filter.pageSize = +size; this.filter.page = 1; this.load(); }
  selectPeriod(period: string): void { this.period = period; const today = new Date(); if (period === 'thisMonth') { this.filter.from = this.isoDate(new Date(today.getFullYear(), today.getMonth(), 1)); this.filter.to = this.isoDate(today); } else if (period === 'lastMonth') { this.filter.from = this.isoDate(new Date(today.getFullYear(), today.getMonth() - 1, 1)); this.filter.to = this.isoDate(new Date(today.getFullYear(), today.getMonth(), 0)); } else if (period === 'currentFy') { const year = today.getMonth() >= 6 ? today.getFullYear() : today.getFullYear() - 1; this.filter.from = this.isoDate(new Date(year, 6, 1)); this.filter.to = this.isoDate(today); } else if (period === 'previousFy') { const year = today.getMonth() >= 6 ? today.getFullYear() - 1 : today.getFullYear() - 2; this.filter.from = this.isoDate(new Date(year, 6, 1)); this.filter.to = this.isoDate(new Date(year + 1, 5, 30)); } }
  export(format: 'csv' | 'xlsx'): void { this.reports.export('transactions', format, this.filter).subscribe(blob => { const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = `transactions.${format}`; link.click(); URL.revokeObjectURL(url); }); }
  money(value: number | null | undefined): string { return new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' }).format(value ?? 0); }
  moneyOrDash(value: number | null | undefined): string { return value == null ? '—' : this.money(value); }
  cogsSource(row: TransactionSalesRow): string {
    if (row.costingStatus === 'Error') return 'Error';
    if (row.costOfGoods == null || row.costingStatus === 'Pending') return 'Pending';
    return row.costSource === 'Unknown' ? row.costingStatus : row.costSource;
  }
  private isoDate(date: Date): string { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`; }
}
