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
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500">Loading report...</div> } @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> } @else if (report) {
  @if (report.purchaseGstIncomplete) {
    <div class="mb-5 rounded-xl border border-amber-300 bg-amber-50 p-5 text-sm text-amber-800" role="status" data-testid="purchase-gst-incomplete">
      <strong>Incomplete: purchase GST is unresolved.</strong>
      <p class="mt-1">{{ report.purchaseUnresolvedComponentCount ?? 0 }} purchase component(s) totalling {{ money(report.purchaseUnresolvedAmount) }} have no GST classification, so purchase input GST and estimated GST payable below are incomplete. Classify them before using these figures for a BAS.</p>
    </div>
  }
  <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-6">@for (card of cards(report); track card[0]) {<div class="rounded-xl bg-white p-5 shadow-sm"><div class="text-xl font-semibold">{{ money($any(card[1])) }}</div><div class="mt-1 text-sm text-slate-500">{{ card[0] }}</div></div>}</div>
  <section class="mt-5 rounded-xl border border-slate-200 bg-white p-5 shadow-sm"><h2 class="mb-4 text-lg font-semibold text-slate-800">Purchase input GST</h2><div class="max-w-xl space-y-3 text-sm"><div class="flex justify-between"><span>Product lines</span><strong>{{ money(report.purchaseLineGst) }}</strong></div><div class="flex justify-between"><span>Delivery and package charges</span><strong>{{ money(report.purchaseChargeGst) }}</strong></div><div class="flex justify-between border-t border-slate-200 pt-3 text-base"><span class="font-semibold">Total purchase input GST</span><strong>{{ money(report.inventoryPurchaseGst) }}</strong></div><div class="flex justify-between text-slate-500"><span>Unclassified components</span><strong data-testid="purchase-unresolved">{{ report.purchaseUnresolvedComponentCount ?? 0 }} ({{ money(report.purchaseUnresolvedAmount) }})</strong></div></div><p class="mt-3 text-xs text-slate-500">Unclassified components contribute no input GST; they are never estimated from an amount.</p></section>
}
@if (quality().length) { <div class="mt-6 rounded-xl bg-amber-50 p-5 text-sm text-amber-800"><strong>Data quality notes</strong><ul class="mt-2 list-disc pl-5">@for (note of quality(); track note) {<li>{{ note }}</li>}</ul></div> }
` })
export class GstReportComponent extends ReportPageBase<GstReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<GstReport> { return this.reports.gst(filter); }

  // Label/value pairs only: every amount is read straight from the API result, never derived here.
  cards(report: GstReport): [string, number | null | undefined][] {
    return [
      ['Taxable sales', report.taxableSales],
      ['GST on sales', report.gstOnSales],
      ['Taxable fees', report.taxableFees],
      ['GST on fees', report.gstOnFees],
      ['GST on expenses', report.operatingExpenseGst],
      ['Purchase input GST', report.inventoryPurchaseGst],
      [report.purchaseGstIncomplete ? 'Estimated GST payable (incomplete)' : 'Estimated GST payable', report.netGst]
    ];
  }
}
