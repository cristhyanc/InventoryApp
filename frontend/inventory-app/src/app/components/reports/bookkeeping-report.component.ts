import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, BookkeepingReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';

@Component({
  selector: 'app-bookkeeping-report',
  standalone: true,
  imports: [CommonModule, ReportFiltersComponent],
  template: `
    <header class="mb-5 flex flex-wrap items-center justify-between gap-3">
      <h1 class="text-2xl font-semibold text-slate-800">Monthly Bookkeeping</h1>
      <div class="flex gap-2"><button class="rounded-md border border-slate-300 px-3 py-2 text-sm hover:bg-slate-50" (click)="export('csv','bookkeeping')">Export CSV</button><button class="rounded-md border border-slate-300 px-3 py-2 text-sm hover:bg-slate-50" (click)="export('xlsx','bookkeeping')">Export XLSX</button></div>
    </header>
    <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
    @if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500 shadow-sm">Loading report...</div> }
    @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> }
    @else if (report) {
      <div class="mb-5 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm"><div class="text-xs font-semibold uppercase tracking-wide text-slate-500">Gross Sales</div><div class="mt-2 text-2xl font-bold text-slate-800">{{ money(report.sales) }}</div><div class="mt-1 text-sm text-slate-500">Card {{ money(report.cardSales) }} · Cash {{ money(report.cashSales) }}</div></div>
          <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm"><div class="text-xs font-semibold uppercase tracking-wide text-slate-500">Gross Profit</div><div class="mt-2 text-2xl font-bold text-slate-800">{{ moneyOrUnavailable(report.grossProfit) }}</div><div class="mt-1 text-sm text-slate-500">{{ report.grossProfit == null ? 'COGS incomplete' : (margin(report.sales, report.grossProfit) | number:'1.1-1') + '% margin' }}</div></div>
          <div class="rounded-xl border-2 border-emerald-500 bg-emerald-50 p-4 shadow-sm"><div class="text-xs font-semibold uppercase tracking-wide text-emerald-700">Net Profit</div><div class="mt-2 text-3xl font-bold text-emerald-800">{{ moneyOrUnavailable(report.netProfit) }}</div>@if (report.nayaxProcessingFees?.hasEstimatedFees) {<div class="mt-1 text-xs text-emerald-700">Includes {{ money(report.nayaxProcessingFees?.estimatedFeeIncGst) }} estimated Nayax fees</div>}</div>
          <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm"><div class="text-xs font-semibold uppercase tracking-wide text-slate-500">Net Margin</div><div class="mt-2 text-2xl font-bold text-slate-800">{{ report.netMarginPercent == null ? 'Profit unavailable' : (report.netMarginPercent | number:'1.1-1') + '%' }}</div></div>
      </div>
      <div class="grid gap-5 lg:grid-cols-2">
        <section class="rounded-xl border border-slate-200 bg-white p-5 shadow-sm"><h2 class="mb-4 text-lg font-semibold text-slate-800">Cost of Sales</h2><div class="space-y-3 text-sm"><div class="flex justify-between"><span>Gross Sales</span><strong>{{ money(report.sales) }}</strong></div><div class="flex justify-between pl-4 text-slate-500"><span>Card Sales</span><strong>{{ money(report.cardSales) }}</strong></div><div class="flex justify-between pl-4 text-slate-500"><span>Cash Sales</span><strong>{{ money(report.cashSales) }}</strong></div><div class="flex justify-between"><span>COGS</span><strong>{{ report.isCogsComplete === false ? 'Partial ' + money(report.partialCostOfGoods) : money(report.costOfGoods) }}</strong></div>@if (report.isCogsComplete === false) {<div class="text-xs text-amber-700">{{ report.uncostedTransactionCount }} completed sale(s) uncosted ({{ money(report.uncostedSalesAmount) }})</div>}<div class="flex justify-between border-t border-slate-100 pt-3"><span class="font-semibold">Gross Profit</span><strong>{{ moneyOrUnavailable(report.grossProfit) }}</strong></div><div class="flex justify-between text-slate-500"><span>Gross Margin</span><strong>{{ report.grossProfit == null ? 'Profit unavailable' : (margin(report.sales, report.grossProfit) | number:'1.1-1') + '%' }}</strong></div></div></section>
        <section class="rounded-xl border border-slate-200 bg-white p-5 shadow-sm"><h2 class="mb-4 text-lg font-semibold text-slate-800">Operating Costs</h2><div class="space-y-3 text-sm"><div class="flex justify-between"><span>Total Nayax Processing Fees</span><strong>{{ money(report.nayaxFeesIncludingGst) }}</strong></div><div class="pl-2 text-xs text-slate-500">@if (report.nayaxProcessingFees?.hasEstimatedFees) { {{ money(report.nayaxProcessingFees?.actualFeeIncGst) }} actual · {{ money(report.nayaxProcessingFees?.estimatedFeeIncGst) }} estimated } @else {Actual}</div><div class="flex justify-between"><span>Site Commission</span><strong>{{ money(report.siteCommission) }}</strong></div><div class="flex justify-between"><span>Delivery Costs</span><strong>{{ money(report.deliveryCosts) }}</strong></div><div class="flex justify-between"><span>Package Costs</span><strong>{{ money(report.packageCosts) }}</strong></div>@for (entry of categoryEntries(report); track entry[0]) {<div class="flex justify-between"><span>{{ categoryLabel(entry[0]) }}</span><strong>{{ money(entry[1]) }}</strong></div>}<div class="flex justify-between border-t border-slate-200 pt-3 text-base"><span class="font-semibold">Total Operating Costs</span><strong>{{ money(totalOperatingCosts(report)) }}</strong></div></div></section>
        <section class="rounded-xl border border-slate-200 bg-white p-5 shadow-sm lg:col-span-2"><h2 class="mb-4 text-lg font-semibold text-slate-800">Nayax Card Settlement</h2><div class="max-w-xl space-y-3 text-sm"><div class="flex justify-between"><span>Card Sales</span><strong>{{ money(report.cardSales) }}</strong></div><div class="flex justify-between"><span>- Nayax Fees</span><strong>{{ money(report.nayaxFeesIncludingGst) }}</strong></div><div class="flex justify-between border-t border-slate-200 pt-3 text-base"><span class="font-semibold">Net Reimbursement</span><strong>{{ money(report.netSettlement) }}</strong></div></div><p class="mt-3 text-xs text-slate-500">Net reimbursement is the amount paid by Nayax for card sales and is not business profit.</p></section>
        <section class="rounded-xl border border-slate-200 bg-white p-5 shadow-sm"><h2 class="mb-4 text-lg font-semibold text-slate-800">GST</h2><div class="space-y-3 text-sm"><div class="flex justify-between"><span>GST on Sales</span><strong>{{ money(report.gstOnSales) }}</strong></div><div class="flex justify-between"><span>GST on Nayax Fees</span><strong>{{ money(report.gstOnFees) }}</strong></div></div></section>
        <section class="rounded-xl border border-slate-200 bg-white p-5 shadow-sm"><h2 class="mb-4 text-lg font-semibold text-slate-800">Cash Reconciliation</h2><div class="flex justify-between text-sm"><span>Cash Sales</span><strong>{{ money(report.cashSales) }}</strong></div><p class="mt-3 text-xs text-slate-500">Cash collection data is not currently recorded.</p></section>
      </div>
    }
    @if (quality().length) { <div class="mt-6 rounded-xl bg-amber-50 p-5 text-sm text-amber-800"><strong>Data quality notes</strong><ul class="mt-2 list-disc pl-5">@for (note of quality(); track note) {<li>{{ note }}</li>}</ul></div> }
  `
})
export class BookkeepingReportComponent extends ReportPageBase<BookkeepingReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<BookkeepingReport> { return this.reports.bookkeeping(filter); }
  margin(sales: number, profit: number | null | undefined): number { return sales && profit != null ? profit / sales * 100 : 0; }
  categoryEntries(report: BookkeepingReport): [string, number][] { return Object.entries(report.operatingExpensesByCategory ?? {}); }
  categoryLabel(category: string): string { return category.replace(/([a-z])([A-Z])/g, '$1 $2'); }
  totalOperatingCosts(report: BookkeepingReport): number { return (report.nayaxFeesIncludingGst ?? 0) + (report.siteCommission ?? 0) + (report.deliveryCosts ?? 0) + (report.packageCosts ?? 0) + (report.structuredOperatingExpenses ?? report.otherOperatingExpenses ?? 0); }
}
