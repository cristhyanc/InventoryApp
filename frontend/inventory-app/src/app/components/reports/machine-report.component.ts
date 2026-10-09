import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, MachineReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';
import { IconComponent } from '../shared/icon.component';

@Component({ selector: 'app-machine-report', standalone: true, imports: [CommonModule, ReportFiltersComponent, IconComponent], template: `
<div class="page">
  <header class="page-header">
    <h1 class="page-title">Machine Profitability</h1>
    <div class="page-actions">
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('csv','machine-profitability')"><app-icon name="download" [size]="18" />Export CSV</button>
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('xlsx','machine-profitability')"><app-icon name="download" [size]="18" />Export XLSX</button>
    </div>
  </header>
  <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    <p class="value-muted">Direct profit excludes shared business overhead that has not been allocated to this machine.</p>
    <div class="card overflow-x-auto"><table class="table"><thead class="table-head"><tr><th scope="col" class="table-cell">Machine</th><th scope="col" class="table-cell">Commission</th><th scope="col" class="table-cell">Nayax Fees</th><th scope="col" class="table-cell">Direct Expenses</th><th scope="col" class="table-cell">Transactions</th><th scope="col" class="table-cell">Sales</th><th scope="col" class="table-cell">COGS</th><th scope="col" class="table-cell">Gross Profit</th><th scope="col" class="table-cell">Direct Profit</th><th scope="col" class="table-cell">Direct Margin</th></tr></thead><tbody>@for (row of report.rows; track row.machineId) {<tr class="table-row"><td class="table-cell">{{ row.machineName }}</td><td class="table-cell">{{ (row.commissionPercent ?? 0) * 100 | number:'1.2-2' }}% ({{ money(row.siteCommission) }})</td><td class="table-cell">{{ money(row.nayaxProcessingFees?.totalFeeIncGst) }}@if (row.nayaxProcessingFees?.hasEstimatedFees) {<div class="text-md-warning-text">Includes estimated fees</div>}</td><td class="table-cell">{{ money(row.directOperatingExpenses) }}</td><td class="table-cell">{{ row.transactionCount }}</td><td class="table-cell"><div>{{ money(row.sales) }}</div><div class="value-muted">Card {{ money(row.cardSales) }} · Cash {{ money(row.cashSales) }}</div></td><td class="table-cell">{{ row.isCogsComplete === false ? 'Partial ' + money(row.partialCostOfGoods) : money(row.costOfGoods) }}</td><td class="table-cell">{{ moneyOrUnavailable(row.grossProfit) }}</td><td class="table-cell">{{ moneyOrUnavailable(row.directProfit) }}</td><td class="table-cell">{{ row.directMarginPercent == null ? 'Profit unavailable' : (row.directMarginPercent | number:'1.2-2') + '%' }}</td></tr>}</tbody></table></div>
  }
</div>
` })
export class MachineReportComponent extends ReportPageBase<MachineReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<MachineReport> { return this.reports.machines(filter); }
}
