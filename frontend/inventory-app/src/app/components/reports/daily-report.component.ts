import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, DailyReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';

@Component({ selector: 'app-daily-report', standalone: true, imports: [CommonModule, ReportFiltersComponent], template: `
<div class="mb-5 flex items-center justify-between"><h1 class="text-2xl font-semibold text-slate-800">Daily Sales / Reimbursement</h1><div class="flex gap-2"><button class="rounded-md border px-3 py-2 text-sm" (click)="export('csv','daily')">Export CSV</button><button class="rounded-md border px-3 py-2 text-sm" (click)="export('xlsx','daily')">Export XLSX</button></div></div>
<app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500">Loading report...</div> } @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> } @else if (report) {
@if (quality().length) { <div class="mb-4 rounded-lg bg-amber-50 p-3 text-xs text-amber-800">@for (note of quality(); track note) { <div>{{ note }}</div> }</div> }
<div class="overflow-x-auto rounded-xl bg-white shadow-sm"><table class="min-w-full text-sm">
<thead><tr class="bg-slate-50 text-left"><th class="px-4 py-3">Date</th><th class="px-4 py-3">Transactions</th><th class="px-4 py-3">Gross sales</th><th class="px-4 py-3">COGS</th><th class="px-4 py-3">Gross margin</th><th class="px-4 py-3">Fees / reimbursement</th><th class="px-4 py-3">Status</th></tr></thead>
<tbody>
@for (row of report.rows; track row.date) {
<tr class="border-t align-top">
  <td class="px-4 py-3">{{ row.date | date:'dd/MM/yyyy' }}</td>
  <td class="px-4 py-3">{{ row.transactionCount }}<div class="text-xs text-slate-500">Avg {{ money(row.averageSale) }}</div>@if ((row.pendingTransactionCount ?? 0) > 0) {<div class="text-xs text-amber-700">{{ row.pendingTransactionCount }} pending</div>}</td>
  <td class="px-4 py-3">{{ money(row.grossSales ?? row.sales) }}<div class="text-xs text-slate-500">Card {{ money(row.cardSales) }} · Cash {{ money(row.cashSales) }}</div></td>
  <td class="px-4 py-3"><span [class.text-amber-700]="row.isCogsComplete === false">{{ row.isCogsComplete === false ? '—' : money(row.costOfGoods) }}</span>@if (row.isCogsComplete === false) {<div class="text-xs text-amber-700">Cost data incomplete: {{ row.uncostedTransactionCount }} uncosted ({{ money(row.uncostedSalesAmount) }})</div>}</td>
  <td class="px-4 py-3"><span [class.text-amber-700]="row.isCogsComplete === false">{{ row.isCogsComplete === false ? '—' : money(row.grossProfit) }}</span>@if (row.isCogsComplete !== false) {<div class="text-xs text-slate-500">{{ row.grossMarginPercent ?? 0 | number:'1.1-1' }}% margin</div>} @else {<div class="text-xs text-amber-700">Incomplete</div>}</td>
  <td class="px-4 py-3"><div>{{ row.reconciliationStatus === 'Pending' ? 'Pending' : money(row.nayaxFeesIncludingGst) + ' fees' }}</div><div class="text-xs text-slate-500">{{ row.reconciliationStatus === 'Pending' ? 'Reimbursement not imported' : money(row.importedReimbursement) + ' reimbursement · net ' + money(row.netReimbursement) }}</div></td>
  <td class="px-4 py-3"><span [class.text-emerald-700]="row.reconciliationStatus === 'Reconciled'" [class.text-amber-700]="row.reconciliationStatus === 'Warning' || row.reconciliationStatus === 'Pending'" [class.text-red-700]="row.reconciliationStatus === 'Mismatch'">{{ row.reconciliationStatus || 'Pending' }}</span></td>
</tr>
}
@if (report.totals) {
<tr class="border-t-2 bg-slate-50 font-semibold"><td class="px-4 py-3">Total</td><td class="px-4 py-3">{{ report.totals.transactionCount }}<div class="text-xs font-normal text-slate-500">Avg {{ money(report.totals.averageSale) }}</div></td><td class="px-4 py-3">{{ money(report.totals.grossSales) }}<div class="text-xs font-normal text-slate-500">Card {{ money(report.totals.cardSales) }} · Cash {{ money(report.totals.cashSales) }}</div></td><td class="px-4 py-3">{{ report.totals.isCogsComplete ? money(report.totals.costOfGoods) : '—' }}@if (!report.totals.isCogsComplete) {<div class="text-xs font-normal text-amber-700">Incomplete</div>}</td><td class="px-4 py-3">{{ report.totals.isCogsComplete ? money(report.totals.grossProfit) : '—' }}@if (report.totals.isCogsComplete) {<div class="text-xs font-normal text-slate-500">{{ report.totals.grossMarginPercent | number:'1.1-1' }}%</div>} @else {<div class="text-xs font-normal text-amber-700">Incomplete</div>}</td><td class="px-4 py-3">{{ money(report.totals.nayaxFeesIncludingGst) }} fees<div class="text-xs font-normal text-slate-500">{{ money(report.totals.importedReimbursement) }} reimbursement · net {{ money(report.totals.netReimbursement) }}</div></td><td class="px-4 py-3">—</td></tr>
}
</tbody></table></div>
}
` })
export class DailyReportComponent extends ReportPageBase<DailyReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<DailyReport> { return this.reports.daily(filter); }
}
