import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Machine } from '../../models/models';
import { MachineService } from '../../services/machine.service';
import { ReportingService, TransactionSalesFilter, TransactionSalesReport, TransactionSalesRow } from '../../services/reporting.service';
import { ReportFiltersComponent } from './report-filters.component';

@Component({
  selector: 'app-transaction-sales-report',
  standalone: true,
  imports: [CommonModule, FormsModule, ReportFiltersComponent],
  template: `
<div class="mb-5 flex flex-wrap items-center justify-between gap-3">
  <h1 class="text-2xl font-semibold text-slate-800">Transaction Sales</h1>
  <div class="flex gap-2"><button class="rounded-md border px-3 py-2 text-sm" (click)="export('csv')">Export CSV</button><button class="rounded-md border px-3 py-2 text-sm" (click)="export('xlsx')">Export XLSX</button></div>
</div>
<app-report-filters [from]="filter.from!" [to]="filter.to!" [machineId]="filter.machineId ?? null" [machines]="machines" [period]="period"
  (fromChange)="filter.from=$event" (toChange)="filter.to=$event" (machineChange)="filter.machineId=$event" (periodChange)="selectPeriod($event)" (apply)="apply()" />
<div class="mb-5 grid gap-3 rounded-xl border border-slate-200 bg-white p-3 shadow-sm md:grid-cols-3 xl:grid-cols-6">
  <label class="text-xs text-slate-600">Site<select class="mt-1 w-full rounded border border-slate-300 p-2 text-sm" [(ngModel)]="filter.siteId"><option [ngValue]="null">All sites</option>@for (site of report?.filterOptions?.sites ?? []; track site.id) {<option [ngValue]="site.id">{{ site.name }}</option>}</select></label>
  <label class="text-xs text-slate-600">Product<select class="mt-1 w-full rounded border border-slate-300 p-2 text-sm" [(ngModel)]="filter.productId"><option [ngValue]="null">All products</option>@for (product of report?.filterOptions?.products ?? []; track product.id) {<option [ngValue]="product.id">{{ product.name }}</option>}</select></label>
  <label class="text-xs text-slate-600">Payment<select class="mt-1 w-full rounded border border-slate-300 p-2 text-sm" [(ngModel)]="filter.paymentType"><option value="">All payments</option><option value="card">Card</option><option value="cash">Cash</option><option value="unknown">Unknown</option></select></label>
  <label class="text-xs text-slate-600">Status<select class="mt-1 w-full rounded border border-slate-300 p-2 text-sm" [(ngModel)]="filter.status"><option value="completed">Completed</option><option value="pending">Pending</option><option value="refunded">Refunded</option><option value="cancelled">Cancelled / declined</option><option value="unknown">Unknown</option><option value="all">All statuses</option></select></label>
  <label class="text-xs text-slate-600">COGS<select class="mt-1 w-full rounded border border-slate-300 p-2 text-sm" [(ngModel)]="filter.cogsStatus"><option value="">All COGS</option><option value="costed">Costed</option><option value="uncosted">Incomplete</option></select></label>
  <label class="text-xs text-slate-600">Search<input class="mt-1 w-full rounded border border-slate-300 p-2 text-sm" placeholder="ID, machine, product" [(ngModel)]="filter.search" (keyup.enter)="apply()" /></label>
  <div class="md:col-span-3 xl:col-span-6"><button class="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700" (click)="apply()">Apply filters</button></div>
</div>
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500">Loading transactions...</div> }
@else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> }
@else if (report) {
  @if (report.dataQuality.notes?.length) { <div class="mb-4 rounded-lg bg-amber-50 p-3 text-xs text-amber-800">@for (note of report.dataQuality.notes; track note) {<div>{{ note }}</div>}</div> }
  <div class="mb-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
    <div class="rounded-lg bg-white p-3 shadow-sm"><div class="text-xs text-slate-500">Sales</div><div class="font-semibold">{{ money(report.totals.sales) }}</div></div>
    <div class="rounded-lg bg-white p-3 shadow-sm"><div class="text-xs text-slate-500">COGS</div><div class="font-semibold" [class.text-amber-700]="!report.totals.isCogsComplete">{{ report.totals.isCogsComplete ? money(report.totals.costOfGoods) : 'Incomplete' }}</div></div>
    <div class="rounded-lg bg-white p-3 shadow-sm"><div class="text-xs text-slate-500">Gross Profit</div><div class="font-semibold" [class.text-amber-700]="report.totals.grossProfit == null">{{ moneyOrDash(report.totals.grossProfit) }}</div></div>
    <div class="rounded-lg bg-white p-3 shadow-sm"><div class="text-xs text-slate-500">Direct Profit</div><div class="font-semibold" [class.text-amber-700]="report.totals.directProfit == null">{{ moneyOrDash(report.totals.directProfit) }}</div></div>
    <div class="rounded-lg bg-white p-3 shadow-sm"><div class="text-xs text-slate-500">Estimated fees (inc GST)</div><div class="font-semibold">{{ money(report.totals.estimatedFeeIncGst) }}</div></div>
  </div>
  <div class="overflow-x-auto rounded-xl bg-white shadow-sm"><table class="min-w-full text-sm">
    <thead><tr class="bg-slate-50 text-left">@for (column of columns; track column.key) {<th class="whitespace-nowrap px-3 py-3">@if (column.sortable) {<button class="font-semibold" (click)="sort(column.key)">{{ column.label }} @if (filter.sortBy === column.key) {<span>{{ filter.sortDescending ? '↓' : '↑' }}</span>}</button>} @else {<span class="font-semibold">{{ column.label }}</span>}</th>}<th class="px-3 py-3">Fees / commission</th></tr></thead>
    <tbody>@for (row of report.rows; track row.transactionId) {<tr class="border-t align-top">
      <td class="whitespace-nowrap px-3 py-3">{{ row.transactionDate | date:'dd/MM/yyyy HH:mm' }}<div class="text-xs text-slate-500">#{{ row.transactionId }}</div></td>
      <td class="px-3 py-3">{{ row.machineName }}<div class="text-xs text-slate-500">{{ row.siteName || 'Site unavailable' }}</div></td>
      <td class="px-3 py-3">{{ row.productName }}</td>
      <td class="px-3 py-3">{{ row.paymentType }}<div class="text-xs text-slate-500">{{ row.rawPaymentMethod || '—' }}</div></td>
      <td class="px-3 py-3">{{ money(row.sale) }}</td>
      <td class="px-3 py-3" [class.text-amber-700]="row.costOfGoods == null">{{ moneyOrDash(row.costOfGoods) }}<div class="text-xs text-slate-500">{{ row.costingStatus }}</div></td>
      <td class="px-3 py-3" [class.text-amber-700]="row.grossProfit == null">{{ moneyOrDash(row.grossProfit) }} @if (row.grossMarginPercent != null) {<div class="text-xs text-slate-500">{{ row.grossMarginPercent | number:'1.1-1' }}%</div>}</td>
      <td class="px-3 py-3" [class.text-amber-700]="row.directProfit == null">{{ moneyOrDash(row.directProfit) }} @if (row.directMarginPercent != null) {<div class="text-xs text-slate-500">{{ row.directMarginPercent | number:'1.1-1' }}%</div>}</td>
      <td class="px-3 py-3">{{ row.transactionStatus }}</td>
      <td class="px-3 py-3">{{ row.feeSource }}@if (row.feeIncGst != null) {<div class="text-xs text-slate-500">{{ money(row.feeIncGst) }} inc GST</div>}@if (row.commissionAmount != null) {<div class="text-xs text-slate-500">Commission {{ money(row.commissionAmount) }}{{ row.commissionBasis ? ' · ' + row.commissionBasis : '' }}</div>}</td>
    </tr>}</tbody>
  </table></div>
  <div class="mt-4 flex flex-wrap items-center justify-between gap-3 text-sm text-slate-600">
    <span>{{ report.totalCount }} transaction{{ report.totalCount === 1 ? '' : 's' }} · {{ report.totals.completedTransactionCount }} completed</span>
    <div class="flex items-center gap-2"><label>Rows <select class="rounded border p-1" [(ngModel)]="filter.pageSize" (ngModelChange)="changePageSize($event)"><option [ngValue]="50">50</option><option [ngValue]="100">100</option><option [ngValue]="250">250</option></select></label><button class="rounded border px-3 py-1 disabled:opacity-50" [disabled]="report.page <= 1" (click)="goTo(report.page - 1)">Previous</button><span>Page {{ report.page }} / {{ pages }}</span><button class="rounded border px-3 py-1 disabled:opacity-50" [disabled]="report.page >= pages" (click)="goTo(report.page + 1)">Next</button></div>
  </div>
}
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
  private isoDate(date: Date): string { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`; }
}
