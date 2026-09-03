import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, DashboardReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';

@Component({ selector: 'app-dashboard-report', standalone: true, imports: [CommonModule, ReportFiltersComponent], template: `
<h1 class="mb-5 text-2xl font-semibold text-slate-800">Reporting Dashboard</h1>
<app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500 shadow-sm">Loading report...</div> } @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> } @else if (report) { <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">@for (card of [['Gross Sales',report.sales],['Gross Profit',report.grossProfit],['Net Profit',report.netProfit],['Nayax Fees',report.nayaxFeesExGst],['Net Reimbursement',report.netReimbursement],['Site Commission',report.siteCommission],['Transactions',report.transactions]]; track card[0]) {<div class="rounded-xl bg-white p-5 shadow-sm"><div class="text-2xl font-semibold text-blue-600">{{ card[0] === 'Transactions' ? card[1] : money($any(card[1])) }}</div><div class="mt-1 text-sm text-slate-500">{{ card[0] }}</div></div>}</div> }
` })
export class DashboardReportComponent extends ReportPageBase<DashboardReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<DashboardReport> { return this.reports.dashboard(filter); }
}
