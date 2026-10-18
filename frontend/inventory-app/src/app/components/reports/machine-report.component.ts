import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
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
    @if (quality().length) { <div class="alert alert-warning" data-testid="data-quality-notes"><p class="alert-title">Data quality notes</p><ul class="list-disc pl-5">@for (note of quality(); track note) {<li>{{ note }}</li>}</ul></div> }
    <p class="value-muted">Direct profit excludes shared business overhead that has not been allocated to this machine.</p>
    <div class="card overflow-x-auto"><table class="table"><thead class="table-head"><tr><th scope="col" class="table-cell">Machine</th><th scope="col" class="table-cell">Commission</th><th scope="col" class="table-cell">Nayax Fees</th><th scope="col" class="table-cell">Direct Expenses</th><th scope="col" class="table-cell">Transactions</th><th scope="col" class="table-cell">Sales</th><th scope="col" class="table-cell">COGS</th><th scope="col" class="table-cell">Gross Profit</th><th scope="col" class="table-cell">Direct Profit</th><th scope="col" class="table-cell">Direct Margin</th></tr></thead><tbody>@for (row of report.rows; track row.machineId) {<tr class="table-row"><td class="table-cell">{{ row.machineName }}</td><td class="table-cell">{{ (row.commissionPercent ?? 0) * 100 | number:'1.2-2' }}% ({{ moneyOrState(row.siteCommission, salesState()) }})</td><td class="table-cell">{{ moneyOrState(row.nayaxProcessingFees?.totalFeeIncGst, feeState()) }}@if (row.nayaxProcessingFees?.hasEstimatedFees) {<div class="text-md-warning-text">Includes estimated fees</div>}</td><td class="table-cell">{{ money(row.directOperatingExpenses) }}</td><td class="table-cell">{{ countOrState(row.transactionCount, salesState()) }}</td><td class="table-cell"><div>{{ moneyOrState(row.sales, salesState()) }}</div><div class="value-muted">Card {{ moneyOrState(row.cardSales, salesState()) }} · Cash {{ moneyOrState(row.cashSales, salesState()) }}</div></td><td class="table-cell">{{ row.isCogsComplete === false ? 'Partial ' + money(row.partialCostOfGoods) : moneyOrState(row.costOfGoods, salesState()) }}</td><td class="table-cell">{{ moneyOrState(row.grossProfit, salesState(), 'Profit unavailable') }}</td><td class="table-cell">{{ moneyOrState(row.directProfit, combinedState(), 'Profit unavailable') }}</td><td class="table-cell">{{ percentOrState(row.directMarginPercent, combinedState()) }}</td></tr>}</tbody></table></div>
    @if (!report.rows.length && availabilityBadge(salesState())) {
      <p class="value-muted" data-testid="empty-machine-list">{{ availabilityBadge(salesState()) }} - no machine can be listed for this period, which is not the same as a fleet that sold nothing.</p>
    }
  }
</div>
` })
export class MachineReportComponent extends ReportPageBase<MachineReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService, provider: ProviderAvailabilityService) { super(route, reports, machines, provider); }
  request(filter: ReportingFilter): Observable<MachineReport> { return this.reports.machines(filter); }

  /**
   * The single banner that says this period's provider figures are not a measured zero (issue
   * #731). The per-machine direct expenses are this business's own records and are unaffected,
   * which is the part a reader most needs told when the provider side is incomplete.
   */
  providerNotice(): { title: string; detail: string } | null {
    if (!this.providerIncomplete()) return null;
    return {
      title: 'Incomplete: provider data does not cover this whole period.',
      detail: 'A machine with no recorded sales does not appear here at all, so an absent machine is not a machine '
        + 'that sold nothing. Sales, cost of goods, gross profit and commission follow the sales state, the actual '
        + 'Nayax fees follow the imported settlement state, and direct profit follows the weaker of the two. Direct '
        + 'expenses come from your own records and are unaffected.'
    };
  }
}
