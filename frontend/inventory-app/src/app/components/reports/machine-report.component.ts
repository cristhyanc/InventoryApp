import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, MachineReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';

@Component({ selector: 'app-machine-report', standalone: true, imports: [CommonModule, ReportFiltersComponent], template: `
<div class="mb-5 flex items-center justify-between"><h1 class="text-2xl font-semibold text-slate-800">Machine Profitability</h1><div class="flex gap-2"><button class="rounded-md border px-3 py-2 text-sm" (click)="export('csv','machine-profitability')">Export CSV</button><button class="rounded-md border px-3 py-2 text-sm" (click)="export('xlsx','machine-profitability')">Export XLSX</button></div></div>
<app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500">Loading report...</div> } @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> } @else if (report) { <p class="mb-3 text-sm text-slate-500">Direct profit excludes shared business overhead that has not been allocated to this machine.</p><div class="overflow-x-auto rounded-xl bg-white shadow-sm"><table class="min-w-full text-sm"><thead><tr class="bg-slate-50 text-left"><th class="px-4 py-3">Machine</th><th class="px-4 py-3">Commission</th><th class="px-4 py-3">Nayax Fees</th><th class="px-4 py-3">Direct Expenses</th><th class="px-4 py-3">Transactions</th><th class="px-4 py-3">Sales</th><th class="px-4 py-3">COGS</th><th class="px-4 py-3">Gross Profit</th><th class="px-4 py-3">Direct Profit</th><th class="px-4 py-3">Direct Margin</th></tr></thead><tbody>@for (row of report.rows; track row.machineId) {<tr class="border-t"><td class="px-4 py-3">{{ row.machineName }}</td><td class="px-4 py-3">{{ (row.commissionPercent ?? 0) * 100 | number:'1.2-2' }}% ({{ money(row.siteCommission) }})</td><td class="px-4 py-3">{{ money(row.nayaxProcessingFees?.totalFeeIncGst) }}@if (row.nayaxProcessingFees?.hasEstimatedFees) {<div class="text-xs text-amber-700">Includes estimated fees</div>}</td><td class="px-4 py-3">{{ money(row.directOperatingExpenses) }}</td><td class="px-4 py-3">{{ row.transactionCount }}</td><td class="px-4 py-3"><div>{{ money(row.sales) }}</div><div class="text-xs text-slate-500">Card {{ money(row.cardSales) }} · Cash {{ money(row.cashSales) }}</div></td><td class="px-4 py-3">{{ row.isCogsComplete === false ? 'Partial ' + money(row.partialCostOfGoods) : money(row.costOfGoods) }}</td><td class="px-4 py-3">{{ moneyOrUnavailable(row.grossProfit) }}</td><td class="px-4 py-3">{{ moneyOrUnavailable(row.directProfit) }}</td><td class="px-4 py-3">{{ row.directMarginPercent == null ? 'Profit unavailable' : (row.directMarginPercent | number:'1.2-2') + '%' }}</td></tr>}</tbody></table></div> }
` })
export class MachineReportComponent extends ReportPageBase<MachineReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<MachineReport> { return this.reports.machines(filter); }
}
