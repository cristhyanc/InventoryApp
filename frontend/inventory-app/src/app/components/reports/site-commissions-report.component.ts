import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Site } from '../../models/models';
import { ReportingService, SiteCommissionReport, SiteCommissionRow } from '../../services/reporting.service';
import { SiteService } from '../../services/site.service';

@Component({
  selector: 'app-site-commissions-report',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
<div class="mb-5"><h1 class="text-2xl font-semibold text-slate-800">Site Commissions</h1></div>
<div class="mb-5 flex flex-wrap items-center gap-2 rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
  @for (option of periods; track option[0]) { <button type="button" class="rounded-md px-3 py-1.5 text-sm" [class.bg-blue-600]="period === option[0]" [class.text-white]="period === option[0]" [class.bg-slate-100]="period !== option[0]" (click)="selectPeriod(option[0])">{{ option[1] }}</button> }
  <label class="ml-auto mr-2 text-sm text-slate-600">Site <select class="ml-2 rounded-md border border-slate-300 px-2 py-1.5" [(ngModel)]="siteId"><option [ngValue]="null">All Sites</option>@for (site of sites; track site.siteId) { <option [ngValue]="site.siteId">{{ site.siteName }}</option> }</select></label>
  <button type="button" class="rounded-md bg-blue-600 px-4 py-1.5 text-sm font-medium text-white" (click)="load()">Apply</button>
  @if (period === 'custom') { <label class="text-sm">From <input type="date" [(ngModel)]="from" /></label><label class="text-sm">To <input type="date" [(ngModel)]="to" /></label> }
</div>
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500">Loading report...</div> }
@else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> }
@else if (report) { <div class="overflow-x-auto rounded-xl bg-white shadow-sm"><table class="min-w-full text-sm"><thead><tr class="bg-slate-50 text-left"><th class="px-4 py-3">Site</th><th class="px-4 py-3">Frequency</th><th class="px-4 py-3">Basis</th><th class="px-4 py-3">Eligible Sales</th><th class="px-4 py-3">Rate</th><th class="px-4 py-3">Due</th><th class="px-4 py-3">Paid</th><th class="px-4 py-3">Outstanding</th><th class="px-4 py-3">Status</th><th class="px-4 py-3">Details</th></tr></thead><tbody>@for (row of report.rows; track row.siteId) { <tr class="border-t"><td class="px-4 py-3">{{ row.siteName }}</td><td class="px-4 py-3">{{ row.frequency }}</td><td class="px-4 py-3">{{ row.basis }}</td><td class="px-4 py-3">{{ money(row.eligibleSales) }}</td><td class="px-4 py-3">{{ row.commissionRate * 100 | number:'1.2-2' }}%</td><td class="px-4 py-3">{{ money(row.commissionDue) }}</td><td class="px-4 py-3">{{ money(row.paid) }}</td><td class="px-4 py-3">{{ money(row.outstanding) }}</td><td class="px-4 py-3">{{ row.status }}</td><td class="px-4 py-3"><button type="button" class="text-blue-600 hover:underline" (click)="selected = row">Details</button></td></tr> }</tbody></table></div>
@if (selected) { <section class="mt-5 rounded-xl bg-white p-5 shadow-sm"><div class="flex justify-between"><div><h2 class="text-lg font-semibold">{{ selected.siteName }}</h2><p class="mt-1 text-sm text-slate-500">{{ selected.periodStart | date:'dd/MM/yyyy' }} - {{ selected.periodEnd | date:'dd/MM/yyyy' }}</p></div><button type="button" (click)="selected = null">Close</button></div><div class="mt-4 grid gap-2 text-sm sm:grid-cols-2"><div>Gross Sales <strong class="float-right">{{ money(selected.grossSales) }}</strong></div><div>Card Sales <strong class="float-right">{{ money(selected.cardSales) }}</strong></div><div>Cash Sales <strong class="float-right">{{ money(selected.cashSales) }}</strong></div><div>Commissionable Sales <strong class="float-right">{{ money(selected.eligibleSales) }}</strong></div><div>Commission Due <strong class="float-right">{{ money(selected.commissionDue) }}</strong></div><div>Outstanding <strong class="float-right">{{ money(selected.outstanding) }}</strong></div></div><h3 class="mt-5 font-semibold">Machine breakdown</h3><table class="mt-2 min-w-full text-sm"><thead><tr class="border-b text-left text-xs text-slate-500"><th class="py-2">Machine</th><th class="py-2 text-right">Transactions</th><th class="py-2 text-right">Sales</th><th class="py-2 text-right">Commission Due</th></tr></thead><tbody>@for (machine of selected.machines; track machine.machineId) { <tr class="border-t"><td class="py-2">{{ machine.machineName }}</td><td class="py-2 text-right">{{ machine.transactionCount }}</td><td class="py-2 text-right">{{ money(machine.grossSales) }}</td><td class="py-2 text-right">{{ money(machine.commissionDue) }}</td></tr> }</tbody></table><h3 class="mt-5 font-semibold">Payment history</h3>@if (!selected.payments.length) { <p class="mt-2 text-sm text-slate-500">No payments recorded for this period.</p> } @else { @for (payment of selected.payments; track payment.id) { <div class="mt-2 text-sm">{{ payment.paymentDate | date:'dd/MM/yyyy' }} - {{ money(payment.amount) }} {{ payment.notes }}</div> } } @if (selected.outstanding > 0) { <div class="mt-4 flex flex-wrap items-end gap-2 border-t pt-4"><label class="text-sm">Payment date <input class="ml-1 rounded border p-1" type="date" [(ngModel)]="paymentDate" /></label><label class="text-sm">Amount <input class="ml-1 w-24 rounded border p-1" type="number" min="0.01" step="0.01" [(ngModel)]="paymentAmount" /></label><label class="text-sm">Notes <input class="ml-1 rounded border p-1" [(ngModel)]="paymentNotes" /></label><button type="button" class="rounded bg-blue-600 px-3 py-1.5 text-sm text-white" (click)="recordPayment()">Record Payment</button></div> } @if (selected.dataQuality) { <p class="mt-4 text-sm text-amber-700">{{ selected.dataQuality }}</p> }</section> } }
@if (selected) {
  <div class="mt-4">
    <button type="button" class="rounded-md border px-3 py-2 text-sm" (click)="printDetails()">Print details</button>
  </div>
  <details class="mt-4 rounded-xl bg-white p-5 shadow-sm">
    <summary class="cursor-pointer font-semibold text-slate-800">Products sold</summary>
    <table class="mt-4 min-w-full text-sm">
      <thead><tr class="border-b text-left text-xs text-slate-500"><th class="py-2">Product</th><th class="py-2 text-right">Total Vends</th></tr></thead>
      <tbody>@for (product of selected.products; track product.productName) { <tr class="border-t"><td class="py-2">{{ product.productName }}</td><td class="py-2 text-right">{{ product.totalVends }}</td></tr> }</tbody>
    </table>
  </details>
}
` })
export class SiteCommissionsReportComponent implements OnInit {
  sites: Site[] = []; report: SiteCommissionReport | null = null; selected: SiteCommissionRow | null = null;
  siteId: number | null = null; loading = false; error = ''; period = 'thisMonth';
  paymentDate = this.date(new Date()); paymentAmount = 0; paymentNotes = '';
  from = this.date(new Date(new Date().getFullYear(), new Date().getMonth(), 1)); to = this.date(new Date());
  periods = [['thisMonth', 'This Month'], ['lastMonth', 'Last Month'], ['thisQuarter', 'This Quarter'], ['lastQuarter', 'Last Quarter'], ['currentFy', 'Current FY'], ['previousFy', 'Previous FY'], ['custom', 'Custom']];
  constructor(private reports: ReportingService, private siteService: SiteService) {}
  ngOnInit(): void { this.siteService.getAll().subscribe({ next: sites => this.sites = sites, error: () => this.sites = [] }); this.load(); }
  load(): void { this.loading = true; this.error = ''; this.selected = null; this.reports.siteCommissions({ from: this.from, to: this.to }, this.siteId).subscribe({ next: report => { this.report = report; this.loading = false; }, error: () => { this.error = 'Unable to load site commissions.'; this.loading = false; } }); }
  selectPeriod(period: string): void { this.period = period; const now = new Date(); const month = now.getMonth(); const year = now.getFullYear(); if (period === 'thisMonth') { this.from = this.date(new Date(year, month, 1)); this.to = this.date(now); } else if (period === 'lastMonth') { this.from = this.date(new Date(year, month - 1, 1)); this.to = this.date(new Date(year, month, 0)); } else if (period.includes('Quarter')) { const quarter = Math.floor(month / 3) + (period === 'lastQuarter' ? -1 : 0); this.from = this.date(new Date(year, quarter * 3, 1)); this.to = this.date(period === 'thisQuarter' ? now : new Date(year, quarter * 3 + 3, 0)); } else if (period === 'currentFy' || period === 'previousFy') { const startYear = year - (month < 6 ? 1 : 0) - (period === 'previousFy' ? 1 : 0); this.from = this.date(new Date(startYear, 6, 1)); this.to = this.date(period === 'currentFy' ? now : new Date(startYear + 1, 5, 30)); } }
  recordPayment(): void { if (!this.selected || this.paymentAmount <= 0) return; this.reports.recordCommissionPayment(this.selected.siteId, this.from, this.to, this.paymentDate, this.paymentAmount, this.paymentNotes).subscribe({ next: () => { this.paymentAmount = 0; this.paymentNotes = ''; this.load(); }, error: () => this.error = 'Unable to record commission payment.' }); }
  printDetails(): void {
    if (!this.selected) return;
    const row = this.selected;
    const escape = (value: string) => value.replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]!));
    const money = (value: number) => this.money(value);
    const machines = row.machines.map(machine => `<tr><td>${escape(machine.machineName)}</td><td>${machine.transactionCount}</td><td>${money(machine.grossSales)}</td><td>${money(machine.commissionDue)}</td></tr>`).join('');
    const products = row.products.map(product => `<tr><td>${escape(product.productName)}</td><td>${product.totalVends}</td><td>${money(product.totalSales)}</td></tr>`).join('');
    const payments = row.payments.map(payment => `<tr><td>${escape(payment.paymentDate.slice(0, 10))}</td><td>${money(payment.amount)}</td><td>${escape(payment.notes ?? '')}</td></tr>`).join('');
    const popup = window.open('', '_blank', 'width=900,height=700');
    if (!popup) return;
    popup.document.write(`<html><head><title>${escape(row.siteName)} commission details</title><style>body{font-family:Arial,sans-serif;margin:32px;color:#1e293b}table{width:100%;border-collapse:collapse;margin:16px 0}th,td{padding:8px;border-bottom:1px solid #cbd5e1;text-align:left}th{text-align:left;background:#f8fafc}td:not(:first-child),th:not(:first-child){text-align:right}.summary{display:grid;grid-template-columns:repeat(2,1fr);gap:8px}</style></head><body><h1>${escape(row.siteName)} - Site Commission Details</h1><p>${escape(row.periodStart.slice(0, 10))} to ${escape(row.periodEnd.slice(0, 10))}</p><div class="summary"><div>Gross Sales: <strong>${money(row.grossSales)}</strong></div><div>Card Sales: <strong>${money(row.cardSales)}</strong></div><div>Cash Sales: <strong>${money(row.cashSales)}</strong></div><div>Eligible Sales: <strong>${money(row.eligibleSales)}</strong></div><div>Commission Due: <strong>${money(row.commissionDue)}</strong></div><div>Outstanding: <strong>${money(row.outstanding)}</strong></div></div><h2>Machine breakdown</h2><table><thead><tr><th>Machine</th><th>Transactions</th><th>Sales</th><th>Commission Due</th></tr></thead><tbody>${machines}</tbody></table><h2>Products sold</h2><table><thead><tr><th>Product</th><th>Total Vends</th><th>Total Sales</th></tr></thead><tbody>${products}</tbody></table><h2>Payment history</h2><table><thead><tr><th>Date</th><th>Amount</th><th>Notes</th></tr></thead><tbody>${payments}</tbody></table></body></html>`);
    popup.document.close();
    popup.focus();
    popup.print();
  }
  money(value: number): string { return new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' }).format(value); }
  private date(value: Date): string { return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`; }
}
