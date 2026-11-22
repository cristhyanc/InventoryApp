import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { ReportAvailability } from '../../features/reports/shared/models/reporting.models';
import { ImportedReimbursementPeriod } from '../../features/reports/bookkeeping/models/bookkeeping-report.model';
import { MachineService } from '../../services/machine.service';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { ReportingFilter, ReportingService, BookkeepingReport } from '../../services/reporting.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';
import { GstBasSummaryComponent } from '../../features/reports/gst/gst-bas-summary.component';
import { IconComponent } from '../shared/icon.component';

@Component({
  selector: 'app-bookkeeping-report',
  standalone: true,
  imports: [CommonModule, ReportFiltersComponent, GstBasSummaryComponent, IconComponent],
  template: `
  <div class="page">
    <header class="page-header">
      <h1 class="page-title">Monthly Bookkeeping</h1>
      <div class="page-actions">
        <button type="button" class="btn btn-sm btn-secondary" (click)="export('csv','bookkeeping')"><app-icon name="download" [size]="18" />Export CSV</button>
        <button type="button" class="btn btn-sm btn-secondary" (click)="export('xlsx','bookkeeping')"><app-icon name="download" [size]="18" />Export XLSX</button>
      </div>
    </header>
    <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [machinesError]="machinesError" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
    @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
    @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
    @else if (report) {
      @if (providerNotice(report); as notice) {
        <div class="alert alert-warning" role="status" data-testid="provider-availability">
          <p class="alert-title">{{ notice.title }}</p>
          <p>{{ notice.detail }}</p>
        </div>
      }
      <section class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">Gross Sales</p><p class="stat-card-value" data-testid="gross-sales">{{ moneyOrState(report.sales, salesAvailability(report)) }}</p></div><span class="icon-tile icon-tile-dark"><app-icon name="attach_money" /></span></div>
          <p class="stat-card-footer"><span class="value-muted">Card {{ moneyOrState(report.cardSales, salesAvailability(report)) }} · Cash {{ moneyOrState(report.cashSales, salesAvailability(report)) }}</span></p>
          @if (availabilityBadge(salesAvailability(report)); as badge) {<p class="stat-card-footer"><span class="value-muted" data-testid="sales-availability">{{ badge }}</span></p>}
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">Gross Profit</p><p class="stat-card-value" data-testid="gross-profit">{{ moneyOrState(report.grossProfit, salesAvailability(report), 'Profit unavailable') }}</p></div><span class="icon-tile icon-tile-dark"><app-icon name="bar_chart" /></span></div>
          <p class="stat-card-footer"><span class="value-muted" data-testid="gross-margin">{{ grossMarginLabel(report) }}</span></p>
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">{{ machineId ? 'Direct Profit' : 'Net Profit' }}</p><p class="stat-card-value" data-testid="net-profit">{{ moneyOrState(machineId ? report.directProfit : report.netProfit, combinedAvailability(report), 'Profit unavailable') }}</p></div><span class="icon-tile icon-tile-success"><app-icon name="trending_up" /></span></div>
          @if (report.nayaxProcessingFees?.hasEstimatedFees) {<p class="stat-card-footer"><span class="value-muted">Includes {{ moneyOrState(report.nayaxProcessingFees?.estimatedFeeIncGst, combinedAvailability(report)) }} estimated Nayax fees</span></p>}
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">{{ machineId ? 'Direct Margin' : 'Net Margin' }}</p><p class="stat-card-value" data-testid="net-margin">{{ percentOrState(machineId ? report.directMarginPercent : report.netMarginPercent, combinedAvailability(report)) }}</p></div><span class="icon-tile icon-tile-dark"><app-icon name="trending_up" /></span></div>
        </article>
        <article class="stat-card">
          <div class="stat-card-head"><div class="stat-card-content"><p class="stat-card-label">Transactions</p><p class="stat-card-value" data-testid="transactions">{{ countOrState(report.transactionCount, salesAvailability(report)) }}</p></div><span class="icon-tile icon-tile-dark"><app-icon name="assignment" /></span></div>
          <p class="stat-card-footer"><span class="value-muted"><span data-testid="average-sale">{{ moneyOrState(report.averageSale, salesAvailability(report)) }}</span> average sale</span></p>
        </article>
      </section>
      @if (machineId) { <p class="value-muted">Net profit is unavailable because shared business overhead is not allocated to individual machines.</p> }
      <div class="grid gap-5 lg:grid-cols-2">
        <section class="card"><div class="card-header"><h2 class="card-title">Cost of Sales</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>Gross Sales</span><strong>{{ moneyOrState(report.sales, salesAvailability(report)) }}</strong></div><div class="flex justify-between pl-4"><span class="value-muted">Card Sales</span><strong>{{ moneyOrState(report.cardSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between pl-4"><span class="value-muted">Cash Sales</span><strong>{{ moneyOrState(report.cashSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between"><span>COGS</span><strong data-testid="cost-of-goods">{{ report.isCogsComplete === false ? 'Partial ' + money(report.partialCostOfGoods) : moneyOrState(report.costOfGoods, salesAvailability(report)) }}</strong></div>@if (report.isCogsComplete === false) {<div class="text-md-warning-text">{{ report.uncostedTransactionCount }} completed sale(s) uncosted ({{ money(report.uncostedSalesAmount) }})</div>}<div class="flex justify-between border-t border-md-gray-200 pt-3"><span class="font-semibold">Gross Profit</span><strong>{{ moneyOrState(report.grossProfit, salesAvailability(report), 'Profit unavailable') }}</strong></div><div class="flex justify-between"><span class="value-muted">Gross Margin</span><strong>{{ report.grossProfit == null ? 'Profit unavailable' : percentOrState(margin(report.sales, report.grossProfit), salesAvailability(report)) }}</strong></div></div></section>
        <section class="card"><div class="card-header"><h2 class="card-title">Operating Costs</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>Total Nayax Processing Fees</span><strong data-testid="nayax-fees">{{ moneyOrState(report.nayaxFeesIncludingGst, feesAvailability(report)) }}</strong></div><div class="pl-2"><span class="value-muted">@if (availabilityBadge(feesAvailability(report)); as badge) { <span data-testid="fees-availability">{{ badge }}</span> } @else if (report.nayaxProcessingFees?.hasEstimatedFees) { {{ money(report.nayaxProcessingFees?.actualFeeIncGst) }} actual · {{ money(report.nayaxProcessingFees?.estimatedFeeIncGst) }} estimated } @else {Actual}</span></div><div class="flex justify-between"><span>Site Commission</span><strong data-testid="site-commission">{{ moneyOrState(report.siteCommission, salesAvailability(report)) }}</strong></div><div class="flex justify-between"><span>Delivery Costs</span><strong data-testid="delivery-costs">{{ money(report.deliveryCosts) }}</strong></div><div class="flex justify-between"><span>Package Costs</span><strong data-testid="package-costs">{{ money(report.packageCosts) }}</strong></div>@for (entry of categoryEntries(report); track entry[0]) {<div class="flex justify-between"><span>{{ categoryLabel(entry[0]) }}</span><strong>{{ money(entry[1]) }}</strong></div>}<div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Total Operating Costs</span><strong data-testid="total-operating-costs">{{ moneyOrState(totalOperatingCosts(report), combinedAvailability(report)) }}</strong></div></div></section>
        <section class="card lg:col-span-2"><div class="card-header"><h2 class="card-title">Nayax Card Settlement</h2></div><div class="card-body max-w-xl space-y-3"><div class="flex justify-between"><span>Card Sales</span><strong data-testid="settlement-card-sales">{{ moneyOrState(report.cardSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between"><span>- Nayax Fees</span><strong data-testid="settlement-fees">{{ moneyOrState(report.nayaxFeesIncludingGst, feesAvailability(report)) }}</strong></div><div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Net Reimbursement</span><strong data-testid="net-reimbursement">{{ moneyOrState(report.netSettlement, combinedAvailability(report)) }}</strong></div>@if (report.isNetSettlementEstimated) {<p class="value-muted" data-testid="net-reimbursement-estimated">Estimated: no imported payout covers the whole of this range, so this is card sales less the Nayax fees rather than an amount Nayax paid.</p>}</div><div class="card-footer">Net reimbursement is the amount paid by Nayax for card sales and is not business profit.</div></section>
        <section class="card lg:col-span-2" data-testid="reimbursement-reconciliation">
          <div class="card-header"><h2 class="card-title">Nayax Reimbursement Reconciliation</h2><span class="badge" [ngClass]="reconciliationBadgeClass(report.reconciliationStatus)" data-testid="reconciliation-status">{{ report.reconciliationStatus || 'Pending' }}{{ report.isReconciled ? ' ✓' : '' }}</span></div>
          <div class="card-body max-w-xl space-y-3">
            <div class="flex justify-between"><span>Expected Reimbursement (calculated)</span><strong data-testid="expected-reimbursement">{{ moneyOrState(report.expectedReimbursement, combinedAvailability(report)) }}</strong></div>
            <div class="flex justify-between"><span>Actual Reimbursement (imported)</span><strong data-testid="actual-reimbursement">{{ actualReimbursementLabel(report) }}</strong></div>
            @if (report.reimbursementPeriods?.length) {
              <p class="value-muted" data-testid="reimbursement-periods">Payout period@if (report.reimbursementPeriods!.length > 1) {s}: @for (period of report.reimbursementPeriods; track period.periodStart + period.periodEnd) {<span>{{ period.periodStart | date:'dd/MM/yyyy' }} – {{ period.periodEnd | date:'dd/MM/yyyy' }}{{ $last ? '' : ', ' }}</span>}</p>
            }
            <div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Difference</span><strong data-testid="reimbursement-difference">{{ reimbursementDifferenceLabel(report) }}</strong></div>
          </div>
          @if (report.partlyCoveredReimbursements?.length) {
            <div class="card-body max-w-xl" data-testid="partly-covered-payouts">
              <p class="font-semibold">Payouts partly outside this range</p>
              <ul class="list-disc pl-5 value-muted">
                @for (period of report.partlyCoveredReimbursements; track period.periodStart + period.periodEnd) {
                  <li>{{ period.periodStart | date:'dd/MM/yyyy' }} – {{ period.periodEnd | date:'dd/MM/yyyy' }}: {{ payoutTotalLabel(period) }}</li>
                }
              </ul>
              <p class="value-muted">These payouts settle days outside the selected dates, so they are not counted in the actual reimbursement, the net reimbursement or the difference. Select the dates a payout covers to reconcile it.</p>
            </div>
          }
          <div class="card-footer">Expected reimbursement is calculated from this period's card sales less the Nayax fees including GST. Actual reimbursement is what imported Nayax settlements recorded for the period: only a payout whose own settlement period lies entirely inside the dates you selected is counted, because a payout that also settles days outside them is not this period's money. Reconciled when the absolute difference is at most {{ money(report.reconciliationTolerance ?? 0.01) }}; with no payout lying entirely inside these dates there is nothing to compare, so the actual reimbursement is shown as not available.</div>
        </section>
        <section class="card"><div class="card-header"><h2 class="card-title">GST</h2></div><div class="card-body space-y-3"><div class="flex justify-between"><span>Taxable Sales</span><strong data-testid="taxable-sales">{{ moneyOrState(report.taxableSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between"><span>GST-free Sales</span><strong data-testid="gst-free-sales">{{ moneyOrState(report.gstFreeSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between pl-4"><span class="value-muted">Sales of products with no GST rule</span><strong data-testid="no-gst-rule-sales">{{ moneyOrState(report.noGstRuleSales, salesAvailability(report)) }}</strong></div><div class="flex justify-between border-t border-md-gray-200 pt-3"><span>GST on Sales</span><strong>{{ moneyOrState(report.gstOnSales, salesAvailability(report)) }}</strong></div><p class="value-muted" data-testid="gst-on-sales-estimate">GST on sales is one eleventh of taxable sales (GST-inclusive), taken once from the period total. Sales of products whose GST rule is GST-free are left out; a product with no GST rule is taxable, and those sales are shown above so you can see how much of the total rests on it.</p><div class="flex justify-between"><span>GST on Nayax Fees</span><strong>{{ moneyOrState(report.gstOnFees, feesAvailability(report)) }}</strong></div></div></section>
        <section class="card"><div class="card-header"><h2 class="card-title">Cash Reconciliation</h2></div><div class="card-body"><div class="flex justify-between"><span>Cash Sales</span><strong data-testid="reconciliation-cash-sales">{{ moneyOrState(report.cashSales, salesAvailability(report)) }}</strong></div></div><div class="card-footer">Cash collection data is not currently recorded.</div></section>
      </div>
      <!--
        The GST and BAS summary, for a business registered for GST (issue #1318). It owns its own
        request, so it is a child component and is handed the filter this page actually reported on;
        a business that is not registered for GST is shown nothing at all, which the server decides.
      -->
      <app-gst-bas-summary [filter]="gstSummaryFilter" />
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
          <p>Sales count only approved, completed Nayax transactions. Pending, refunded, cancelled and declined transactions are ordinary payment outcomes and are left out of every figure here. The transaction count is how many of those completed transactions this period and machine scope holds, and the average sale is gross sales divided by it.</p>
          <p>Cost of goods and profit use the cost recorded on each sale when that sale was costed, so an earlier period keeps the cost history it was built from. A completed sale with no recorded cost stays uncosted: cost of goods is shown as partial and profit is left unavailable rather than estimated from today's product cost.</p>
          <p>Site commission comes from the effective-dated site commission agreement that covered each sale's date, so a rate that changed part-way through the period applies from the date it took effect.</p>
          <p>Nayax processing fees use the imported fee amounts for the dates those cover, and the configured rate for the remaining dates; any estimated part is shown beside the fee total.</p>
          <p>The expected reimbursement is calculated from this period's card sales less those Nayax fees including GST. The actual reimbursement is not calculated at all: it is the amount imported Nayax settlements recorded for the period, matched on the transaction period each settlement covers rather than on the date it was paid out or imported. Only a payout whose settlement period lies entirely inside the dates you selected is counted — a weekly payout is not a single day's money, and a payout the dates cut is listed separately instead of being added in. When no payout lies entirely inside the selected dates there is nothing to compare: the actual reimbursement is shown as not available rather than as a zero, and the net reimbursement beside it is labelled as an estimate because it is then card sales less the fees rather than an amount Nayax paid.</p>
          <p>GST on sales follows each product's own GST rule. Sales of products whose rule is GST-free are left out of taxable sales; a product with no GST rule yet is taxable, as is a sale whose product could not be matched, and those sales are reported separately so you can see how much of the figure rests on products nobody has classified. GST on sales is then one eleventh of taxable sales, taken once from the period total rather than per sale, so a month of small sales carries no rounding drift.</p>
          <p>Each product's GST rule as it stands today is used: no GST classification is recorded per sale and no history of a product's rule is kept, so changing a product's rule changes what this report says about its past sales. GST on Nayax fees and operating-expense GST come from imported and recorded amounts and are not derived this way.</p>
          <p>If your business is set to <strong>Registered for GST</strong> in Business Settings, the GST summary above restates these figures under the BAS labels G1, 1A and 1B and shows the estimated net GST for the dates selected. It is the same server calculation the GST / BAS Accounting Aid report uses, so both pages report the same period identically, and it covers the whole business — select all machines to see it. It is an estimate for reviewing the GST section of a BAS, not a lodgement figure: where the figures rest on records that are missing, unclassified or estimated the summary says so.</p>
        </div>
      </details>
    }
    @if (quality().length) { <div class="alert alert-warning" data-testid="data-quality-notes"><p class="alert-title">Data quality notes</p><ul class="list-disc pl-5">@for (note of quality(); track note) {<li>{{ note }}</li>}</ul></div> }
  </div>
  `
})
export class BookkeepingReportComponent extends ReportPageBase<BookkeepingReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService, provider: ProviderAvailabilityService, businessTimeZone: BusinessTimeZoneService) { super(route, reports, machines, provider, businessTimeZone); }
  request(filter: ReportingFilter): Observable<BookkeepingReport> { return this.reports.bookkeeping(filter); }

  /**
   * The period and scope the GST summary child reports on (issue #1318). It is set from the filter
   * this page submits, not from the filter controls as they are edited, so the summary can never
   * describe a different period from the figures beside it.
   *
   * Both sides are also on the *latest* applied filter whatever order the two requests answer in:
   * `ReportPageBase` cancels the report request this one supersedes, so a slower earlier response
   * can no longer arrive last and leave the figures on one period while the summary shows another.
   */
  gstSummaryFilter: ReportingFilter | null = null;

  override load(): void {
    this.gstSummaryFilter = { from: this.from, to: this.to, machineId: this.machineId };
    super.load();
  }

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

  /**
   * The badge class for the reimbursement comparison's status (issue #805), in the same two-state
   * vocabulary the Reporting Dashboard's own reconciliation card uses: only `Reconciled` is a
   * success, and both `Needs Review` and `Pending` are things to look at. The status itself is
   * decided on the server; nothing here recomputes it.
   */
  reconciliationBadgeClass(status?: string): string {
    return status === 'Reconciled' ? 'badge-success' : 'badge-warning';
  }

  /**
   * The imported actual reimbursement: the amount when a payout lying entirely inside the selected
   * dates recorded one, the settlement availability state in place of a zero nothing measured, and
   * `Not available` when the range has no such payout at all (issue #1253). The last case is a
   * different statement from a settlement that really paid nothing, and it is the one case where
   * this figure is not an amount - the API reports it as absent rather than as a zero.
   */
  actualReimbursementLabel(report: BookkeepingReport): string {
    return report.actualReimbursement == null
      ? 'Not available'
      : this.moneyOrState(report.actualReimbursement, this.feesAvailability(report));
  }

  /** The signed difference, or nothing to show when there is no actual to compare against. */
  reimbursementDifferenceLabel(report: BookkeepingReport): string {
    return report.reimbursementDifference == null
      ? '—'
      : this.moneyOrState(report.reimbursementDifference, this.combinedAvailability(report));
  }

  /**
   * One listed payout's own total. A payout whose import recorded no total at all is shown as such
   * rather than as `$0.00`, for the same reason the actual reimbursement is (issue #1253).
   */
  payoutTotalLabel(period: ImportedReimbursementPeriod): string {
    return period.total == null ? 'Not available' : this.money(period.total);
  }

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
      detail: 'Sales, the card/cash split, the transaction count and average sale, cost of goods, site commission, '
        + 'the Nayax fees, the net card settlement, the imported actual reimbursement and the reimbursement comparison '
        + 'and every profit and margin built on them are shown with their state instead of a figure wherever '
        + 'nothing was recorded - they are not a measured zero. Delivery, packaging and operating expenses come '
        + 'from your own records and are unaffected.'
    };
  }
}
