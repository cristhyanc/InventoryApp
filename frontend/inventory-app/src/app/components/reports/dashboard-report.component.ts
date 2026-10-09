import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, DashboardReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';
import { IconComponent } from '../shared/icon.component';

@Component({ selector: 'app-dashboard-report', standalone: true, imports: [CommonModule, ReportFiltersComponent, IconComponent], template: `
<div class="page">
  <header class="page-header"><h1 class="page-title">Reporting Dashboard</h1></header>
  <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    <section class="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
      <article class="stat-card">
        <div class="stat-card-head">
          <div class="stat-card-content"><p class="stat-card-label">Total Sales</p><p data-testid="dashboard-total-sales" class="stat-card-value">{{ money(report.totalSales ?? report.sales) }}</p></div>
          <span class="icon-tile icon-tile-dark"><app-icon name="attach_money" /></span>
        </div>
        <p class="stat-card-footer"><span class="value-muted">{{ money(report.cardSales) }} Card · {{ money(report.cashSales) }} Cash</span></p>
      </article>
      <article class="stat-card">
        <div class="stat-card-head">
          <div class="stat-card-content"><p class="stat-card-label">Gross Profit</p><p data-testid="dashboard-gross-profit" class="stat-card-value">{{ moneyOrUnavailable(report.grossProfit) }}</p></div>
          <span class="icon-tile icon-tile-dark"><app-icon name="bar_chart" /></span>
        </div>
        <p class="stat-card-footer"><span data-testid="dashboard-gross-margin" class="value-muted">{{ report.grossMarginPercent == null ? 'COGS incomplete' : (report.grossMarginPercent | number:'1.1-1') + '% margin' }}</span></p>
      </article>
      <article class="stat-card">
        <div class="stat-card-head">
          <div class="stat-card-content"><p class="stat-card-label">{{ machineId ? 'Direct Profit' : 'Net Profit' }}</p><p data-testid="dashboard-net-profit" class="stat-card-value">{{ moneyOrUnavailable(machineId ? report.directProfit : report.netProfit) }}</p></div>
          <span class="icon-tile icon-tile-success"><app-icon name="trending_up" /></span>
        </div>
        <p class="stat-card-footer"><span class="value-muted">{{ (machineId ? report.directMarginPercent : report.netMarginPercent) == null ? 'Profit unavailable' : ((machineId ? report.directMarginPercent : report.netMarginPercent) | number:'1.1-1') + '% margin' }}@if (report.nayaxProcessingFees?.hasEstimatedFees) { · Includes estimated fees}</span></p>
      </article>
      <article class="stat-card">
        <div class="stat-card-head">
          <div class="stat-card-content"><p class="stat-card-label">Transactions</p><p class="stat-card-value">{{ report.transactions }}</p></div>
          <span class="icon-tile icon-tile-dark"><app-icon name="assignment" /></span>
        </div>
        <p class="stat-card-footer"><span class="value-muted">{{ money(report.averageSale) }} average sale</span></p>
      </article>
    </section>
    <section class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-5">
      <div class="card card-body text-center"><p class="stat-card-label">Card Sales</p><p class="text-md-card-title font-semibold text-md-gray-800">{{ money(report.cardSales) }}</p></div>
      <div class="card card-body text-center"><p class="stat-card-label">Cash Sales</p><p class="text-md-card-title font-semibold text-md-gray-800">{{ money(report.cashSales) }}</p></div>
      <div class="card card-body text-center"><p class="stat-card-label">Cost of Goods Sold</p><p data-testid="dashboard-cost-of-goods" class="text-md-card-title font-semibold text-md-gray-800">{{ report.isCogsComplete === false ? 'Partial ' + money(report.partialCostOfGoods) : money(report.costOfGoodsSold) }}</p></div>
      <div class="card card-body text-center"><p class="stat-card-label">Site Commission</p><p class="text-md-card-title font-semibold text-md-gray-800">{{ money(report.siteCommission) }}</p></div>
      <div class="card card-body text-center"><p class="stat-card-label">Nayax Fees</p><p class="text-md-card-title font-semibold text-md-gray-800">{{ money(report.nayaxFeesIncludingGst ?? report.nayaxFeesExGst) }}</p></div>
    </section>
    @if (machineId) { <p class="value-muted">Net profit is unavailable because shared business overhead is not allocated to individual machines.</p> }
    <section class="card">
      <div class="card-header"><h2 class="card-title">Nayax Reconciliation</h2><span class="badge" [ngClass]="statusClass(report.reconciliationStatus)">{{ report.reconciliationStatus }} {{ report.isReconciled ? '✓' : '' }}</span></div>
      <div class="card-body max-w-xl space-y-3">
        <div class="flex justify-between"><span>Card Sales</span><strong>{{ money(report.cardSales) }}</strong></div>
        <div class="flex justify-between"><span>Nayax Fees</span><strong>-{{ money(report.nayaxFeesIncludingGst ?? report.nayaxFeesExGst) }}</strong></div>
        <div class="flex justify-between border-t border-md-gray-200 pt-3"><span class="font-semibold">Expected Reimbursement</span><strong>{{ money(report.expectedReimbursement) }}</strong></div>
        <div class="flex justify-between"><span>Actual Reimbursement</span><strong>{{ report.reconciliationStatus === 'Pending' ? 'Pending' : money(report.actualReimbursement) }}</strong></div>
        <div class="flex justify-between"><span>Difference</span><strong>{{ report.reconciliationStatus === 'Pending' ? '—' : money(report.reimbursementDifference) }}</strong></div>
      </div>
      <div class="card-footer"><span class="value-muted">Reconciled when the absolute difference is at most {{ money(report.reconciliationTolerance ?? 0.01) }}. Reimbursements are matched using their covered transaction period.</span></div>
    </section>
  }
</div>
` })
export class DashboardReportComponent extends ReportPageBase<DashboardReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<DashboardReport> { return this.reports.dashboard(filter); }
  statusClass(status?: string): string {
    return status === 'Reconciled' ? 'badge-success' : 'badge-warning';
  }
}
