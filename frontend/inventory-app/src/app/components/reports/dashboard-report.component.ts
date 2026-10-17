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
    @if (providerNotice(); as notice) {
      <div class="alert alert-warning" role="status" data-testid="provider-availability">
        <p class="alert-title">{{ notice.title }}</p>
        <p>{{ notice.detail }}</p>
      </div>
    }
    <section class="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
      <article class="stat-card">
        <div class="stat-card-head">
          <div class="stat-card-content"><p class="stat-card-label">Total Sales</p><p data-testid="dashboard-total-sales" class="stat-card-value">{{ moneyOrState(report.totalSales ?? report.sales, salesState()) }}</p></div>
          <span class="icon-tile icon-tile-dark"><app-icon name="attach_money" /></span>
        </div>
        <p class="stat-card-footer"><span class="value-muted">{{ moneyOrState(report.cardSales, salesState()) }} Card · {{ moneyOrState(report.cashSales, salesState()) }} Cash</span></p>
        @if (availabilityBadge(salesState()); as badge) {<p class="stat-card-footer"><span class="value-muted" data-testid="dashboard-sales-availability">{{ badge }}</span></p>}
      </article>
      <article class="stat-card">
        <div class="stat-card-head">
          <div class="stat-card-content"><p class="stat-card-label">Gross Profit</p><p data-testid="dashboard-gross-profit" class="stat-card-value">{{ moneyOrState(report.grossProfit, salesState(), 'Profit unavailable') }}</p></div>
          <span class="icon-tile icon-tile-dark"><app-icon name="bar_chart" /></span>
        </div>
        <p class="stat-card-footer"><span data-testid="dashboard-gross-margin" class="value-muted">{{ grossMarginLabel(report) }}</span></p>
      </article>
      <article class="stat-card">
        <div class="stat-card-head">
          <div class="stat-card-content"><p class="stat-card-label">{{ machineId ? 'Direct Profit' : 'Net Profit' }}</p><p data-testid="dashboard-net-profit" class="stat-card-value">{{ moneyOrState(machineId ? report.directProfit : report.netProfit, combinedState(), 'Profit unavailable') }}</p></div>
          <span class="icon-tile icon-tile-success"><app-icon name="trending_up" /></span>
        </div>
        <p class="stat-card-footer"><span class="value-muted">{{ percentOrState(machineId ? report.directMarginPercent : report.netMarginPercent, combinedState()) }}@if (report.nayaxProcessingFees?.hasEstimatedFees) { · Includes estimated fees}</span></p>
      </article>
      <article class="stat-card">
        <div class="stat-card-head">
          <div class="stat-card-content"><p class="stat-card-label">Transactions</p><p class="stat-card-value" data-testid="dashboard-transactions">{{ countOrState(report.transactions, salesState()) }}</p></div>
          <span class="icon-tile icon-tile-dark"><app-icon name="assignment" /></span>
        </div>
        <p class="stat-card-footer"><span class="value-muted">{{ moneyOrState(report.averageSale, salesState()) }} average sale</span></p>
      </article>
    </section>
    <section class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-5">
      <div class="card card-body text-center"><p class="stat-card-label">Card Sales</p><p class="text-md-card-title font-semibold text-md-gray-800">{{ moneyOrState(report.cardSales, salesState()) }}</p></div>
      <div class="card card-body text-center"><p class="stat-card-label">Cash Sales</p><p class="text-md-card-title font-semibold text-md-gray-800">{{ moneyOrState(report.cashSales, salesState()) }}</p></div>
      <div class="card card-body text-center"><p class="stat-card-label">Cost of Goods Sold</p><p data-testid="dashboard-cost-of-goods" class="text-md-card-title font-semibold text-md-gray-800">{{ report.isCogsComplete === false ? 'Partial ' + money(report.partialCostOfGoods) : moneyOrState(report.costOfGoodsSold, salesState()) }}</p></div>
      <div class="card card-body text-center"><p class="stat-card-label">Site Commission</p><p class="text-md-card-title font-semibold text-md-gray-800">{{ moneyOrState(report.siteCommission, salesState()) }}</p></div>
      <div class="card card-body text-center"><p class="stat-card-label">Nayax Fees</p><p class="text-md-card-title font-semibold text-md-gray-800" data-testid="dashboard-nayax-fees">{{ moneyOrState(report.nayaxFeesIncludingGst ?? report.nayaxFeesExGst, feeState()) }}</p>@if (availabilityBadge(feeState()); as badge) {<p class="value-muted" data-testid="dashboard-fee-availability">{{ badge }}</p>}</div>
    </section>
    @if (machineId) { <p class="value-muted">Net profit is unavailable because shared business overhead is not allocated to individual machines.</p> }
    <section class="card">
      <div class="card-header"><h2 class="card-title">Nayax Reconciliation</h2><span class="badge" [ngClass]="statusClass(report.reconciliationStatus)">{{ report.reconciliationStatus }} {{ report.isReconciled ? '✓' : '' }}</span></div>
      <div class="card-body max-w-xl space-y-3">
        <div class="flex justify-between"><span>Card Sales</span><strong>{{ moneyOrState(report.cardSales, salesState()) }}</strong></div>
        <div class="flex justify-between"><span>Nayax Fees</span><strong>-{{ moneyOrState(report.nayaxFeesIncludingGst ?? report.nayaxFeesExGst, feeState()) }}</strong></div>
        <div class="flex justify-between border-t border-md-gray-200 pt-3"><span class="font-semibold">Expected Reimbursement</span><strong>{{ moneyOrState(report.expectedReimbursement, combinedState()) }}</strong></div>
        <div class="flex justify-between"><span>Actual Reimbursement</span><strong>{{ report.reconciliationStatus === 'Pending' ? 'Pending' : moneyOrState(report.actualReimbursement, feeState()) }}</strong></div>
        <div class="flex justify-between"><span>Difference</span><strong>{{ report.reconciliationStatus === 'Pending' ? '—' : moneyOrState(report.reimbursementDifference, combinedState()) }}</strong></div>
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

  /**
   * The gross-margin footer under the Gross Profit card: the margin, or the sales state in place of
   * a `0.0%` nothing measured. The word "margin" is only appended to a real percentage - a state
   * badge reads as a label, not as a quantity.
   */
  grossMarginLabel(report: DashboardReport): string {
    if (report.grossMarginPercent == null) return 'COGS incomplete';
    const availability = this.salesState();
    const label = this.percentOrState(report.grossMarginPercent, availability);
    return this.availabilityBadge(availability) ? label : `${label} margin`;
  }

  /**
   * The single banner that says this period's provider figures are not a measured zero (issue
   * #731). These are the states the bookkeeping report decided; this summary composes that report,
   * so the two pages can never disagree about whether a zero was measured.
   */
  providerNotice(): { title: string; detail: string } | null {
    if (!this.providerIncomplete()) return null;
    return {
      title: 'Incomplete: provider data does not cover this whole period.',
      detail: 'Sales, the card/cash split, the transaction count, cost of goods, site commission, the Nayax fees, '
        + 'the reimbursement and every profit and margin built on them are shown with their state instead of a '
        + 'figure wherever nothing was recorded - they are not a measured zero. Delivery, packaging and operating '
        + 'expenses come from your own records and are unaffected.'
    };
  }
}
