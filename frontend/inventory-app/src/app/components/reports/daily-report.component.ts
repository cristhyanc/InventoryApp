import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { ReportingFilter, ReportingService, DailyReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';
import { IconComponent } from '../shared/icon.component';

@Component({ selector: 'app-daily-report', standalone: true, imports: [CommonModule, ReportFiltersComponent, IconComponent], template: `
<div class="page">
  <header class="page-header">
    <h1 class="page-title">Daily Sales / Reimbursement</h1>
    <div class="page-actions">
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('csv','daily')"><app-icon name="download" [size]="18" />Export CSV</button>
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('xlsx','daily')"><app-icon name="download" [size]="18" />Export XLSX</button>
    </div>
  </header>
  <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [machinesError]="machinesError" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    @if (providerNotice(); as notice) {
      <div class="alert alert-warning" role="status" data-testid="provider-availability">
        <p class="alert-title">{{ notice.title }}</p>
        <p>{{ notice.detail }}</p>
      </div>
    }
    @if (quality().length) { <div class="alert alert-warning">@for (note of quality(); track note) { <p>{{ note }}</p> }</div> }
    <div class="card overflow-x-auto"><table class="table">
    <thead class="table-head"><tr><th scope="col" class="table-cell">Date</th><th scope="col" class="table-cell">Transactions</th><th scope="col" class="table-cell">Gross sales</th><th scope="col" class="table-cell">COGS</th><th scope="col" class="table-cell">Gross margin</th><th scope="col" class="table-cell">Fees / reimbursement</th><th scope="col" class="table-cell">Status</th></tr></thead>
    <tbody>
    @for (row of report.rows; track row.date) {
    <tr class="table-row">
      <td class="table-cell">{{ row.date | date:'dd/MM/yyyy' }}</td>
      <td class="table-cell">{{ countOrState(row.transactionCount, salesState()) }}<div class="value-muted">Avg {{ moneyOrState(row.averageSale, salesState()) }}</div>@if ((row.pendingTransactionCount ?? 0) > 0) {<div class="text-md-warning-text">{{ row.pendingTransactionCount }} pending</div>}</td>
      <td class="table-cell">{{ moneyOrState(row.grossSales ?? row.sales, salesState()) }}<div class="value-muted">Card {{ moneyOrState(row.cardSales, salesState()) }} · Cash {{ moneyOrState(row.cashSales, salesState()) }}</div></td>
      <td class="table-cell"><span [class.text-md-warning-text]="row.isCogsComplete === false">{{ row.isCogsComplete === false ? 'Partial ' + money(row.partialCostOfGoods) : moneyOrState(row.costOfGoods, salesState()) }}</span>@if (row.isCogsComplete === false) {<div class="text-md-warning-text">Cost data incomplete: {{ row.uncostedTransactionCount }} uncosted ({{ money(row.uncostedSalesAmount) }})</div>}</td>
      <td class="table-cell"><span [class.text-md-warning-text]="row.isCogsComplete === false">{{ row.isCogsComplete === false ? 'Profit unavailable' : moneyOrState(row.grossProfit, salesState(), 'Profit unavailable') }}</span>@if (row.isCogsComplete !== false) {<div class="value-muted">{{ percentOrState(row.grossMarginPercent, salesState()) }}</div>} @else {<div class="text-md-warning-text">COGS incomplete</div>}</td>
      <td class="table-cell"><div>{{ moneyOrState(row.nayaxFeesIncludingGst, feeState()) }} fees</div><div class="value-muted">{{ row.nayaxFeeSource || 'None' }}</div></td>
      <td class="table-cell"><span class="badge" [ngClass]="reconciliationStatusClass(row.reconciliationStatus)">{{ row.reconciliationStatus || 'Pending' }}</span></td>
    </tr>
    }
    @if (report.totals) {
    <tr class="table-row bg-md-gray-100 font-semibold" data-testid="daily-totals"><td class="table-cell">Total</td><td class="table-cell">{{ countOrState(report.totals.transactionCount, salesState()) }}<div class="font-normal value-muted">Avg {{ moneyOrState(report.totals.averageSale, salesState()) }}</div></td><td class="table-cell" data-testid="totals-gross-sales">{{ moneyOrState(report.totals.grossSales, salesState()) }}<div class="font-normal value-muted">Card {{ moneyOrState(report.totals.cardSales, salesState()) }} · Cash {{ moneyOrState(report.totals.cashSales, salesState()) }}</div></td><td class="table-cell">{{ report.totals.isCogsComplete ? moneyOrState(report.totals.costOfGoods, salesState()) : 'Partial ' + money(report.totals.partialCostOfGoods) }}@if (!report.totals.isCogsComplete) {<div class="font-normal text-md-warning-text">Incomplete</div>}</td><td class="table-cell">{{ report.totals.isCogsComplete ? moneyOrState(report.totals.grossProfit, salesState(), 'Profit unavailable') : 'Profit unavailable' }}@if (report.totals.isCogsComplete) {<div class="font-normal value-muted">{{ percentOrState(report.totals.grossMarginPercent, salesState()) }}</div>} @else {<div class="font-normal text-md-warning-text">Incomplete</div>}</td><td class="table-cell" data-testid="totals-fees">{{ moneyOrState(report.totals.nayaxFeesIncludingGst, feeState()) }} fees<div class="font-normal value-muted">{{ moneyOrState(report.totals.importedReimbursement, feeState()) }} reimbursement · net {{ moneyOrState(report.totals.netReimbursement, feeState()) }}</div></td><td class="table-cell">—</td></tr>
    }
    </tbody></table></div>
  }
</div>
` })
export class DailyReportComponent extends ReportPageBase<DailyReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService, provider: ProviderAvailabilityService) { super(route, reports, machines, provider); }
  request(filter: ReportingFilter): Observable<DailyReport> { return this.reports.daily(filter); }
  reconciliationStatusClass(status?: string): string {
    if (status === 'Reconciled') return 'badge-success';
    if (status === 'Warning' || status === 'Pending') return 'badge-warning';
    if (status === 'Mismatch') return 'badge-danger';
    return '';
  }

  /**
   * The single banner that says this period's provider figures are not a measured zero (issue
   * #731). It repeats nothing the API's data-quality notes already say in detail; it exists so the
   * state is visible above the table rather than only underneath it.
   */
  providerNotice(): { title: string; detail: string } | null {
    if (!this.providerIncomplete()) return null;
    return {
      title: 'Incomplete: provider data does not cover this whole period.',
      detail: 'A day with no recorded sales data is shown with its state instead of a figure - it is not a day that '
        + 'sold nothing. Gross sales, the card/cash split, cost of goods and gross profit follow the sales state, and '
        + 'the fees and reimbursement follow the imported settlement state.'
    };
  }
}
