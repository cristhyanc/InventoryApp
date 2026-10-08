import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, ReconciliationReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';
import { IconComponent } from '../shared/icon.component';

@Component({ selector: 'app-reconciliation-report', standalone: true, imports: [CommonModule, ReportFiltersComponent, IconComponent], template: `
<div class="page">
  <header class="page-header">
    <h1 class="page-title">Nayax Reconciliation</h1>
    <div class="page-actions">
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('csv','reconciliation')"><app-icon name="download" [size]="18" />Export CSV</button>
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('xlsx','reconciliation')"><app-icon name="download" [size]="18" />Export XLSX</button>
    </div>
  </header>
  <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    @if (quality().length) { <div class="alert alert-warning">@for (note of quality(); track note) { <p>{{ note }}</p> }</div> }
    <section class="grid gap-4 md:grid-cols-3">
      <div class="card card-body"><p class="stat-card-label">Total Vending Sales</p><strong class="text-md-stat-value text-md-gray-800">{{ money(report.totalVendingSales) }}</strong><div class="mt-2 value-muted">Card {{ money(report.cardSales) }} · Cash {{ money(report.cashSales) }}<br>{{ report.totalTransactionCount }} transactions</div></div>
      <div class="card card-body"><p class="stat-card-label">Card Sales Reconciliation</p><div class="mt-1">Recorded card sales <strong>{{ money(report.cardTransactionSales) }}</strong></div><div>Nayax reported gross <strong>{{ money(report.nayaxReportedGrossCardSales) }}</strong></div><div>Difference <strong>{{ money(report.grossDifference) }}</strong></div><span class="badge mt-2 inline-block" [ngClass]="statusClass(report.grossStatus)">{{ report.grossStatus }}</span></div>
      <div class="card card-body"><p class="stat-card-label">Nayax Settlement Reconciliation</p><div class="mt-1">Expected net <strong>{{ money(report.expectedNetReimbursement) }}</strong></div><div>Actual net reimbursement <strong>{{ money(report.actualNetReimbursement) }}</strong></div><div>Difference <strong>{{ money(report.settlementDifference) }}</strong></div><span class="badge mt-2 inline-block" [ngClass]="statusClass(report.settlementStatus)">{{ report.settlementStatus }}</span></div>
    </section>
    <div class="card overflow-x-auto sm:-mx-6"><table class="table min-w-[1120px] xl:min-w-0"><thead class="table-head"><tr><th scope="col" class="table-cell px-2">Period</th><th scope="col" class="table-cell px-2">Vending sales</th><th scope="col" class="table-cell px-2">Card sales</th><th scope="col" class="table-cell px-2">Nayax gross card</th><th scope="col" class="table-cell px-2">Gross difference</th><th scope="col" class="table-cell px-2">Fees (ex GST)</th><th scope="col" class="table-cell px-2">Fee GST</th><th scope="col" class="table-cell px-2">Other fees</th><th scope="col" class="table-cell px-2">Expected net</th><th scope="col" class="table-cell px-2">Actual net</th><th scope="col" class="table-cell px-2">Settlement</th><th scope="col" class="table-cell px-2">Status</th></tr></thead><tbody>
    @for (row of report.periodRows; track row.from + row.to) { <tr class="table-row"><td class="table-cell px-2">{{ row.from | date:'dd/MM/yyyy' }} – {{ row.to | date:'dd/MM/yyyy' }}<div class="value-muted break-words">{{ row.payoutDate ? ('Payout ' + (row.payoutDate | date:'dd/MM/yyyy')) : 'Payout pending' }}</div></td><td class="table-cell px-2">{{ money(row.totalVendingSales) }}<div class="value-muted break-words">{{ row.totalTransactionCount }} transactions · {{ row.cashTransactionCount }} cash</div></td><td class="table-cell px-2">{{ money(row.cardTransactionSales) }}<div class="value-muted break-words">{{ row.cardTransactionCount }} card transactions</div></td><td class="table-cell px-2">{{ money(row.nayaxReportedGrossCardSales) }}<div class="value-muted break-words">{{ row.nayaxReportedCardTransactionCount }} transactions</div></td><td class="table-cell px-2">{{ money(row.grossDifference) }}<span class="badge" [ngClass]="statusClass(row.grossStatus)">{{ row.grossStatus }}</span></td><td class="table-cell px-2">{{ money(row.processingFeesExGst) }}</td><td class="table-cell px-2">{{ money(row.feeGst) }}</td><td class="table-cell px-2">{{ money(row.otherFees) }}</td><td class="table-cell px-2">{{ money(row.expectedNetReimbursement) }}</td><td class="table-cell px-2">{{ money(row.actualNetReimbursement) }}</td><td class="table-cell px-2">{{ money(row.settlementDifference) }}<span class="badge" [ngClass]="statusClass(row.settlementStatus)">{{ row.settlementStatus }}</span></td><td class="table-cell px-2 font-semibold"><span class="badge" [ngClass]="statusClass(row.status)">{{ row.status }}</span></td></tr> }
    </tbody>@if (report.totals) { <tfoot><tr class="table-row bg-md-gray-100 font-semibold"><td class="table-cell px-2">Total</td><td class="table-cell px-2">{{ money(report.totals.totalVendingSales) }}</td><td class="table-cell px-2">{{ money(report.totals.cardTransactionSales) }}</td><td class="table-cell px-2">{{ money(report.totals.nayaxReportedGrossCardSales) }}</td><td class="table-cell px-2">{{ money(report.totals.grossDifference) }}</td><td class="table-cell px-2">{{ money(report.totals.processingFeesExGst) }}</td><td class="table-cell px-2">{{ money(report.totals.feeGst) }}</td><td class="table-cell px-2">{{ money(report.totals.otherFees) }}</td><td class="table-cell px-2">{{ money(report.totals.expectedNetReimbursement) }}</td><td class="table-cell px-2">{{ money(report.totals.actualNetReimbursement) }}</td><td class="table-cell px-2">{{ money(report.totals.settlementDifference) }}</td><td class="table-cell px-2"><span class="badge" [ngClass]="statusClass(report.totals.status)">{{ report.totals.status }}</span></td></tr></tfoot> }</table></div>
    <div class="card card-body"><div class="grid gap-3 md:grid-cols-5"><div>Processing fees ex GST<strong class="block">{{ money(report.processingFeesExGst) }}</strong></div><div>Fee GST<strong class="block">{{ money(report.feeGst) }}</strong></div><div>Other fees<strong class="block">{{ money(report.otherFees) }}</strong></div><div>Adjustments<strong class="block">{{ money(report.adjustments) }} <span class="font-normal value-muted">{{ report.adjustmentsSupported ? '' : '(unsupported)' }}</span></strong></div><div>Overall status<strong class="block"><span class="badge" [ngClass]="statusClass(report.status)">{{ report.status }}</span></strong></div></div></div>
  }
</div>
` })
export class ReconciliationReportComponent extends ReportPageBase<ReconciliationReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<ReconciliationReport> { return this.reports.reconciliation(filter); }
  statusClass(status: string): string {
    if (status === 'Reconciled') return 'badge-success';
    if (status === 'Mismatch') return 'badge-danger';
    return 'badge-warning';
  }
}
