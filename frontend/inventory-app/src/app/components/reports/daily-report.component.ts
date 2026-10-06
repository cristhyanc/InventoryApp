import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
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
  <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    @if (quality().length) { <div class="alert alert-warning">@for (note of quality(); track note) { <p>{{ note }}</p> }</div> }
    <div class="card overflow-x-auto"><table class="table">
    <thead class="table-head"><tr><th scope="col" class="table-cell">Date</th><th scope="col" class="table-cell">Transactions</th><th scope="col" class="table-cell">Gross sales</th><th scope="col" class="table-cell">COGS</th><th scope="col" class="table-cell">Gross margin</th><th scope="col" class="table-cell">Fees / reimbursement</th><th scope="col" class="table-cell">Status</th></tr></thead>
    <tbody>
    @for (row of report.rows; track row.date) {
    <tr class="table-row">
      <td class="table-cell">{{ row.date | date:'dd/MM/yyyy' }}</td>
      <td class="table-cell">{{ row.transactionCount }}<div class="value-muted">Avg {{ money(row.averageSale) }}</div>@if ((row.pendingTransactionCount ?? 0) > 0) {<div class="text-md-warning-text">{{ row.pendingTransactionCount }} pending</div>}</td>
      <td class="table-cell">{{ money(row.grossSales ?? row.sales) }}<div class="value-muted">Card {{ money(row.cardSales) }} · Cash {{ money(row.cashSales) }}</div></td>
      <td class="table-cell"><span [class.text-md-warning-text]="row.isCogsComplete === false">{{ row.isCogsComplete === false ? 'Partial ' + money(row.partialCostOfGoods) : money(row.costOfGoods) }}</span>@if (row.isCogsComplete === false) {<div class="text-md-warning-text">Cost data incomplete: {{ row.uncostedTransactionCount }} uncosted ({{ money(row.uncostedSalesAmount) }})</div>}</td>
      <td class="table-cell"><span [class.text-md-warning-text]="row.isCogsComplete === false">{{ row.isCogsComplete === false ? 'Profit unavailable' : money(row.grossProfit) }}</span>@if (row.isCogsComplete !== false) {<div class="value-muted">{{ percentOrUnavailable(row.grossMarginPercent, ' margin') }}</div>} @else {<div class="text-md-warning-text">COGS incomplete</div>}</td>
      <td class="table-cell"><div>{{ money(row.nayaxFeesIncludingGst) }} fees</div><div class="value-muted">{{ row.nayaxFeeSource || 'None' }}</div></td>
      <td class="table-cell"><span class="badge" [ngClass]="reconciliationStatusClass(row.reconciliationStatus)">{{ row.reconciliationStatus || 'Pending' }}</span></td>
    </tr>
    }
    @if (report.totals) {
    <tr class="table-row bg-md-gray-100 font-semibold"><td class="table-cell">Total</td><td class="table-cell">{{ report.totals.transactionCount }}<div class="font-normal value-muted">Avg {{ money(report.totals.averageSale) }}</div></td><td class="table-cell">{{ money(report.totals.grossSales) }}<div class="font-normal value-muted">Card {{ money(report.totals.cardSales) }} · Cash {{ money(report.totals.cashSales) }}</div></td><td class="table-cell">{{ report.totals.isCogsComplete ? money(report.totals.costOfGoods) : 'Partial ' + money(report.totals.partialCostOfGoods) }}@if (!report.totals.isCogsComplete) {<div class="font-normal text-md-warning-text">Incomplete</div>}</td><td class="table-cell">{{ report.totals.isCogsComplete ? money(report.totals.grossProfit) : 'Profit unavailable' }}@if (report.totals.isCogsComplete) {<div class="font-normal value-muted">{{ percentOrUnavailable(report.totals.grossMarginPercent) }}</div>} @else {<div class="font-normal text-md-warning-text">Incomplete</div>}</td><td class="table-cell">{{ money(report.totals.nayaxFeesIncludingGst) }} fees<div class="font-normal value-muted">{{ money(report.totals.importedReimbursement) }} reimbursement · net {{ money(report.totals.netReimbursement) }}</div></td><td class="table-cell">—</td></tr>
    }
    </tbody></table></div>
  }
</div>
` })
export class DailyReportComponent extends ReportPageBase<DailyReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<DailyReport> { return this.reports.daily(filter); }
  reconciliationStatusClass(status?: string): string {
    if (status === 'Reconciled') return 'badge-success';
    if (status === 'Warning' || status === 'Pending') return 'badge-warning';
    if (status === 'Mismatch') return 'badge-danger';
    return '';
  }
}
