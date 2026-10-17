import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { ReportAvailability } from '../../features/reports/shared/models/reporting.models';
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
      @if (providerNotice(report); as notice) {
        <div class="alert alert-warning" role="status" data-testid="provider-availability">
          <p class="alert-title">{{ notice.title }}</p>
          <p>{{ notice.detail }}</p>
        </div>
      }
      <section class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">Gross Sales</p><p class="stat-card-value" data-testid="gross-sales">{{ moneyOrState(report.sales, salesAvailability(report)) }}</p></div><span class="icon-tile icon-tile-dark"><app-icon name="attach_money" /></span></div>
          <p class="stat-card-footer"><span class="value-muted">Card {{ moneyOrState(report.cardSales, salesAvailability(report)) }} · Cash {{ moneyOrState(report.cashSales, salesAvailability(report)) }}</span></p>
          @if (availabilityBadge(salesAvailability(report)); as badge) {<p class="stat-card-footer"><span class="value-muted" data-testid="sales-availability">{{ badge }}</span></p>}
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">Gross Profit</p><p class="stat-card-value" data-testid="gross-profit">{{ moneyOrState(report.grossProfit, salesAvailability(report), 'Profit unavailable') }}</p></div><span class="icon-tile icon-tile-dark"><app-icon name="bar_chart" /></span></div>
          <p class="stat-card-footer"><span class="value-muted">{{ grossMarginLabel(report) }}</span></p>
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">{{ machineId ? 'Direct Profit' : 'Net Profit' }}</p><p class="stat-card-value" data-testid="net-profit">{{ moneyOrState(machineId ? report.directProfit : report.netProfit, combinedAvailability(report), 'Profit unavailable') }}</p></div><span class="icon-tile icon-tile-success"><app-icon name="trending_up" /></span></div>
          @if (report.nayaxProcessingFees?.hasEstimatedFees) {<p class="stat-card-footer"><span class="value-muted">Includes {{ moneyOrState(report.nayaxProcessingFees?.estimatedFeeIncGst, combinedAvailability(report)) }} estimated Nayax fees</span></p>}
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">{{ machineId ? 'Direct Margin' : 'Net Margin' }}</p><p class="stat-card-value" data-testid="net-margin">{{ percentOrState(machineId ? report.directMarginPercent : report.netMarginPercent, combinedAvailability(report)) }}</p></div><span class="icon-tile icon-tile-dark"><app-icon name="trending_up" /></span></div>
        </article>
      </section>
      @if (machineId) { <p class="value-muted">Net profit is unavailable because shared business overhead is not allocated to individual machines.</p> }
      <div class="grid gap-5 lg:grid-cols-2">
        <section class="card"><div class="card-header"><h2 class="card-title">Cost of Sales</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>Gross Sales</span><strong>{{ moneyOrState(report.sales, salesAvailability(report)) }}</strong></div><div class="flex justify-between pl-4"><span class="value-muted">Card Sales</span><strong>{{ moneyOrState(report.cardSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between pl-4"><span class="value-muted">Cash Sales</span><strong>{{ moneyOrState(report.cashSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between"><span>COGS</span><strong>{{ report.isCogsComplete === false ? 'Partial ' + money(report.partialCostOfGoods) : moneyOrState(report.costOfGoods, salesAvailability(report)) }}</strong></div>@if (report.isCogsComplete === false) {<div class="text-md-warning-text">{{ report.uncostedTransactionCount }} completed sale(s) uncosted ({{ money(report.uncostedSalesAmount) }})</div>}<div class="flex justify-between border-t border-md-gray-200 pt-3"><span class="font-semibold">Gross Profit</span><strong>{{ moneyOrState(report.grossProfit, salesAvailability(report), 'Profit unavailable') }}</strong></div><div class="flex justify-between"><span class="value-muted">Gross Margin</span><strong>{{ report.grossProfit == null ? 'Profit unavailable' : percentOrState(margin(report.sales, report.grossProfit), salesAvailability(report)) }}</strong></div></div></section>
        <section class="card"><div class="card-header"><h2 class="card-title">Operating Costs</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>Total Nayax Processing Fees</span><strong data-testid="nayax-fees">{{ moneyOrState(report.nayaxFeesIncludingGst, feesAvailability(report)) }}</strong></div><div class="pl-2"><span class="value-muted">@if (availabilityBadge(feesAvailability(report)); as badge) { <span data-testid="fees-availability">{{ badge }}</span> } @else if (report.nayaxProcessingFees?.hasEstimatedFees) { {{ money(report.nayaxProcessingFees?.actualFeeIncGst) }} actual · {{ money(report.nayaxProcessingFees?.estimatedFeeIncGst) }} estimated } @else {Actual}</span></div><div class="flex justify-between"><span>Site Commission</span><strong data-testid="site-commission">{{ moneyOrState(report.siteCommission, salesAvailability(report)) }}</strong></div><div class="flex justify-between"><span>Delivery Costs</span><strong data-testid="delivery-costs">{{ money(report.deliveryCosts) }}</strong></div><div class="flex justify-between"><span>Package Costs</span><strong data-testid="package-costs">{{ money(report.packageCosts) }}</strong></div>@for (entry of categoryEntries(report); track entry[0]) {<div class="flex justify-between"><span>{{ categoryLabel(entry[0]) }}</span><strong>{{ money(entry[1]) }}</strong></div>}<div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Total Operating Costs</span><strong data-testid="total-operating-costs">{{ moneyOrState(totalOperatingCosts(report), combinedAvailability(report)) }}</strong></div></div></section>
        <section class="card lg:col-span-2"><div class="card-header"><h2 class="card-title">Nayax Card Settlement</h2></div><div class="card-body max-w-xl space-y-3"><div class="flex justify-between"><span>Card Sales</span><strong data-testid="settlement-card-sales">{{ moneyOrState(report.cardSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between"><span>- Nayax Fees</span><strong data-testid="settlement-fees">{{ moneyOrState(report.nayaxFeesIncludingGst, feesAvailability(report)) }}</strong></div><div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Net Reimbursement</span><strong data-testid="net-reimbursement">{{ moneyOrState(report.netSettlement, combinedAvailability(report)) }}</strong></div></div><div class="card-footer">Net reimbursement is the amount paid by Nayax for card sales and is not business profit.</div></section>
        <section class="card"><div class="card-header"><h2 class="card-title">GST</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>GST on Sales (estimated)</span><strong>{{ moneyOrState(report.gstOnSales, salesAvailability(report)) }}</strong></div><p class="value-muted" data-testid="gst-on-sales-estimate">Estimated GST on sales - assumes all included sales are taxable at 10% (GST-inclusive).</p><div class="flex justify-between"><span>GST on Nayax Fees</span><strong>{{ moneyOrState(report.gstOnFees, feesAvailability(report)) }}</strong></div></div></section>
        <section class="card"><div class="card-header"><h2 class="card-title">Cash Reconciliation</h2></div><div class="card-body"><div class="flex justify-between"><span>Cash Sales</span><strong data-testid="reconciliation-cash-sales">{{ moneyOrState(report.cashSales, salesAvailability(report)) }}</strong></div></div><div class="card-footer">Cash collection data is not currently recorded.</div></section>
      </div>
      @if (excludedTransactions(report).length) {
        <section class="card" data-testid="excluded-transactions">
          <div class="card-header"><h2 class="card-title">Transactions excluded from sales</h2></div>
          <div class="card-body">
            <ul class="list-disc pl-5 value-muted">
              @for (entry of excludedTransactions(report); track entry[0]) {<li>{{ entry[0] }}: {{ entry[1] }}</li>}
            </ul>
          </div>
          <div class="card-footer">Only approved, completed transactions are counted as sales. These are the other outcomes recorded for this period.</div>
        </section>
      }
      <details class="card card-body" data-testid="calculation-help">
        <summary class="card-title cursor-pointer">How this report is calculated</summary>
        <div class="space-y-3 pt-3">
          <p>Sales count only approved, completed Nayax transactions. Pending, refunded, cancelled and declined transactions are ordinary payment outcomes and are left out of every figure here.</p>
          <p>Cost of goods and profit use the cost recorded on each sale when that sale was costed, so an earlier period keeps the cost history it was built from. A completed sale with no recorded cost stays uncosted: cost of goods is shown as partial and profit is left unavailable rather than estimated from today's product cost.</p>
          <p>Site commission comes from the effective-dated site commission agreement that covered each sale's date, so a rate that changed part-way through the period applies from the date it took effect.</p>
          <p>Nayax processing fees use the imported fee amounts for the dates those cover, and the configured rate for the remaining dates; any estimated part is shown beside the fee total.</p>
          <p>GST on sales is an estimate: GST classification is not recorded per sale, so every included sale is assumed taxable at 10% (GST-inclusive). GST on Nayax fees and operating-expense GST come from imported and recorded amounts, and are not estimated this way.</p>
        </div>
      </details>
    }
    @if (quality().length) { <div class="alert alert-warning" data-testid="data-quality-notes"><p class="alert-title">Data quality notes</p><ul class="list-disc pl-5">@for (note of quality(); track note) {<li>{{ note }}</li>}</ul></div> }
  </div>
  `
})
export class BookkeepingReportComponent extends ReportPageBase<BookkeepingReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<BookkeepingReport> { return this.reports.bookkeeping(filter); }
  margin(sales: number, profit: number | null | undefined): number { return sales && profit != null ? profit / sales * 100 : 0; }
  categoryEntries(report: BookkeepingReport): [string, number][] { return Object.entries(report.operatingExpensesByCategory ?? {}); }
  /**
   * The non-completed transaction counts the API reported for this period and machine scope
   * (issue #476), shown as neutral scope information. Pending, refunded and cancelled/declined rows
   * are ordinary payment outcomes, so they never appear as a data-quality warning; the two genuine
   * problems - an unrecognised status ID and no status ID at all - are reported by the API's own
   * conditional data-quality notes as well, and are listed here only to account for every row.
   */
  excludedTransactions(report: BookkeepingReport): [string, number][] {
    const counts: [string, number][] = [
      ['Pending', report.pendingTransactionCount ?? 0],
      ['Refunded', report.refundedTransactionCount ?? 0],
      ['Cancelled or declined', report.declinedOrCancelledTransactionCount ?? 0],
      ['Unrecognised status', report.unknownStatusTransactionCount ?? 0],
      ['No status recorded', report.missingStatusTransactionCount ?? 0]
    ];
    return counts.filter(entry => entry[1] > 0);
  }
  categoryLabel(category: string): string { return category.replace(/([a-z])([A-Z])/g, '$1 $2'); }
  totalOperatingCosts(report: BookkeepingReport): number { return (report.nayaxFeesIncludingGst ?? 0) + (report.siteCommission ?? 0) + (report.deliveryCosts ?? 0) + (report.packageCosts ?? 0) + (report.structuredOperatingExpenses ?? report.otherOperatingExpenses ?? 0); }

  /** The state the API decided for every figure derived from stored provider sales (issue #730). */
  salesAvailability(report: BookkeepingReport): ReportAvailability | undefined {
    return report.providerAvailability?.sales;
  }

  /** The state the API decided for every figure derived from imported settlement data. */
  feesAvailability(report: BookkeepingReport): ReportAvailability | undefined {
    return report.providerAvailability?.fees;
  }

  /**
   * The state for a figure that depends on both families - net/direct profit and their margins, the
   * net card settlement and the total of operating costs. The API decides it with the Domain
   * least-complete rule, so this never combines two states here (AGENTS.md § Architecture rules).
   * Falling back to the sales state keeps an older response, which reported only two families, from
   * reading as complete.
   */
  combinedAvailability(report: BookkeepingReport): ReportAvailability | undefined {
    return report.providerAvailability?.combined ?? this.salesAvailability(report);
  }

  /**
   * The gross-margin footer under the Gross Profit card: the margin, or the sales state in place of
   * a `0.0%` nothing measured. The word "margin" is only appended to a real percentage - a state
   * badge reads as a label, not as a quantity.
   */
  grossMarginLabel(report: BookkeepingReport): string {
    if (report.grossProfit == null) return 'COGS incomplete';
    const availability = this.salesAvailability(report);
    const label = this.percentOrState(this.margin(report.sales, report.grossProfit), availability);
    return this.availabilityBadge(availability) ? label : `${label} margin`;
  }

  /**
   * The single banner that says this period's provider figures are not a measured zero. The
   * business's own delivery, packaging and operating-expense figures are unaffected and stay exact,
   * which is the part a reader most needs told when the provider side is incomplete.
   */
  providerNotice(report: BookkeepingReport): { title: string; detail: string } | null {
    const incomplete = [this.salesAvailability(report), this.feesAvailability(report)]
      .filter(availability => availability && availability.state !== 'Complete');
    if (!incomplete.length) return null;
    return {
      title: 'Incomplete: provider data does not cover this whole period.',
      detail: 'Sales, the card/cash split, cost of goods, site commission, the Nayax fees, the net card settlement '
        + 'and every profit and margin built on them are shown with their state instead of a figure wherever '
        + 'nothing was recorded - they are not a measured zero. Delivery, packaging and operating expenses come '
        + 'from your own records and are unaffected.'
    };
  }
}
