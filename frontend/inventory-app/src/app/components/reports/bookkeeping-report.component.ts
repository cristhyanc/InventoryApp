import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, BookkeepingReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';
import { IconComponent } from '../shared/icon.component';

@Component({
  selector: 'app-bookkeeping-report',
  standalone: true,
  imports: [CommonModule, ReportFiltersComponent, IconComponent],
  template: `
  <div class="page">
    <header class="page-header">
      <h1 class="page-title">Monthly Bookkeeping</h1>
      <div class="page-actions">
        <button type="button" class="btn btn-sm btn-secondary" (click)="export('csv','bookkeeping')"><app-icon name="download" [size]="18" />Export CSV</button>
        <button type="button" class="btn btn-sm btn-secondary" (click)="export('xlsx','bookkeeping')"><app-icon name="download" [size]="18" />Export XLSX</button>
      </div>
    </header>
    <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
    @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
    @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
    @else if (report) {
      <section class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <article class="stat-card">
          <div class="stat-card-head"><span class="icon-tile icon-tile-dark"><app-icon name="attach_money" /></span><div><p class="stat-card-label">Gross Sales</p><p class="stat-card-value">{{ money(report.sales) }}</p></div></div>
          <p class="stat-card-footer"><span class="value-muted">Card {{ money(report.cardSales) }} · Cash {{ money(report.cashSales) }}</span></p>
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><span class="icon-tile icon-tile-dark"><app-icon name="bar_chart" /></span><div><p class="stat-card-label">Gross Profit</p><p class="stat-card-value">{{ moneyOrUnavailable(report.grossProfit) }}</p></div></div>
          <p class="stat-card-footer"><span class="value-muted">{{ report.grossProfit == null ? 'COGS incomplete' : (margin(report.sales, report.grossProfit) | number:'1.1-1') + '% margin' }}</span></p>
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><span class="icon-tile icon-tile-success"><app-icon name="trending_up" /></span><div><p class="stat-card-label">{{ machineId ? 'Direct Profit' : 'Net Profit' }}</p><p class="stat-card-value">{{ moneyOrUnavailable(machineId ? report.directProfit : report.netProfit) }}</p></div></div>
          @if (report.nayaxProcessingFees?.hasEstimatedFees) {<p class="stat-card-footer"><span class="value-muted">Includes {{ money(report.nayaxProcessingFees?.estimatedFeeIncGst) }} estimated Nayax fees</span></p>}
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><span class="icon-tile icon-tile-dark"><app-icon name="trending_up" /></span><div><p class="stat-card-label">{{ machineId ? 'Direct Margin' : 'Net Margin' }}</p><p class="stat-card-value">{{ (machineId ? report.directMarginPercent : report.netMarginPercent) == null ? 'Profit unavailable' : ((machineId ? report.directMarginPercent : report.netMarginPercent) | number:'1.1-1') + '%' }}</p></div></div>
        </article>
      </section>
      @if (machineId) { <p class="value-muted">Net profit is unavailable because shared business overhead is not allocated to individual machines.</p> }
      <div class="grid gap-5 lg:grid-cols-2">
        <section class="card"><div class="card-header"><h2 class="card-title">Cost of Sales</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>Gross Sales</span><strong>{{ money(report.sales) }}</strong></div><div class="flex justify-between pl-4"><span class="value-muted">Card Sales</span><strong>{{ money(report.cardSales) }}</strong></div><div class="flex justify-between pl-4"><span class="value-muted">Cash Sales</span><strong>{{ money(report.cashSales) }}</strong></div><div class="flex justify-between"><span>COGS</span><strong>{{ report.isCogsComplete === false ? 'Partial ' + money(report.partialCostOfGoods) : money(report.costOfGoods) }}</strong></div>@if (report.isCogsComplete === false) {<div class="text-md-warning-text">{{ report.uncostedTransactionCount }} completed sale(s) uncosted ({{ money(report.uncostedSalesAmount) }})</div>}<div class="flex justify-between border-t border-md-gray-200 pt-3"><span class="font-semibold">Gross Profit</span><strong>{{ moneyOrUnavailable(report.grossProfit) }}</strong></div><div class="flex justify-between"><span class="value-muted">Gross Margin</span><strong>{{ report.grossProfit == null ? 'Profit unavailable' : (margin(report.sales, report.grossProfit) | number:'1.1-1') + '%' }}</strong></div></div></section>
        <section class="card"><div class="card-header"><h2 class="card-title">Operating Costs</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>Total Nayax Processing Fees</span><strong>{{ money(report.nayaxFeesIncludingGst) }}</strong></div><div class="pl-2"><span class="value-muted">@if (report.nayaxProcessingFees?.hasEstimatedFees) { {{ money(report.nayaxProcessingFees?.actualFeeIncGst) }} actual · {{ money(report.nayaxProcessingFees?.estimatedFeeIncGst) }} estimated } @else {Actual}</span></div><div class="flex justify-between"><span>Site Commission</span><strong>{{ money(report.siteCommission) }}</strong></div><div class="flex justify-between"><span>Delivery Costs</span><strong>{{ money(report.deliveryCosts) }}</strong></div><div class="flex justify-between"><span>Package Costs</span><strong>{{ money(report.packageCosts) }}</strong></div>@for (entry of categoryEntries(report); track entry[0]) {<div class="flex justify-between"><span>{{ categoryLabel(entry[0]) }}</span><strong>{{ money(entry[1]) }}</strong></div>}<div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Total Operating Costs</span><strong>{{ money(totalOperatingCosts(report)) }}</strong></div></div></section>
        <section class="card lg:col-span-2"><div class="card-header"><h2 class="card-title">Nayax Card Settlement</h2></div><div class="card-body max-w-xl space-y-3"><div class="flex justify-between"><span>Card Sales</span><strong>{{ money(report.cardSales) }}</strong></div><div class="flex justify-between"><span>- Nayax Fees</span><strong>{{ money(report.nayaxFeesIncludingGst) }}</strong></div><div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Net Reimbursement</span><strong>{{ money(report.netSettlement) }}</strong></div></div><div class="card-footer">Net reimbursement is the amount paid by Nayax for card sales and is not business profit.</div></section>
        <section class="card"><div class="card-header"><h2 class="card-title">GST</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>GST on Sales</span><strong>{{ money(report.gstOnSales) }}</strong></div><div class="flex justify-between"><span>GST on Nayax Fees</span><strong>{{ money(report.gstOnFees) }}</strong></div></div></section>
        <section class="card"><div class="card-header"><h2 class="card-title">Cash Reconciliation</h2></div><div class="card-body"><div class="flex justify-between"><span>Cash Sales</span><strong>{{ money(report.cashSales) }}</strong></div></div><div class="card-footer">Cash collection data is not currently recorded.</div></section>
      </div>
    }
    @if (quality().length) { <div class="alert alert-warning"><p class="alert-title">Data quality notes</p><ul class="list-disc pl-5">@for (note of quality(); track note) {<li>{{ note }}</li>}</ul></div> }
  </div>
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
