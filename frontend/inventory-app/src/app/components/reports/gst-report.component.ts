import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, GstReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';

@Component({ selector: 'app-gst-report', standalone: true, imports: [CommonModule, ReportFiltersComponent], template: `
<div class="mb-5 flex items-center justify-between"><div><h1 class="text-2xl font-semibold text-slate-800">GST / BAS Accounting Aid</h1><p class="mt-1 text-sm text-amber-700">Accounting aid only; not a replacement for BAS or accounting advice.</p></div><div class="flex gap-2"><button class="rounded-md border px-3 py-2 text-sm" (click)="export('csv','gst')">Export CSV</button><button class="rounded-md border px-3 py-2 text-sm" (click)="export('xlsx','gst')">Export XLSX</button></div></div>
<app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500">Loading report...</div> } @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> } @else if (report) { <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-6">@for (card of [['Taxable sales',report.taxableSales],['GST on sales',report.gstOnSales],['Taxable fees',report.taxableFees],['GST on fees',report.gstOnFees],['GST on expenses',report.operatingExpenseGst ?? 0],['Estimated GST payable',report.netGst]]; track card[0]) {<div class="rounded-xl bg-white p-5 shadow-sm"><div class="text-xl font-semibold">{{ money($any(card[1])) }}</div><div class="mt-1 text-sm text-slate-500">{{ card[0] }}</div></div>}</div> }
` })
export class GstReportComponent extends ReportPageBase<GstReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<GstReport> { return this.reports.gst(filter); }
}
