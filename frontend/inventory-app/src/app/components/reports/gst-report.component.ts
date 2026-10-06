import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, GstReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';
import { IconComponent } from '../shared/icon.component';

@Component({ selector: 'app-gst-report', standalone: true, imports: [CommonModule, ReportFiltersComponent, IconComponent], template: `
<div class="page">
  <header class="page-header">
    <div>
      <h1 class="page-title">GST / BAS Accounting Aid</h1>
      <p class="page-subtitle text-md-warning-text">Accounting aid only; not a replacement for BAS or accounting advice.</p>
    </div>
    <div class="page-actions">
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('csv','gst')"><app-icon name="download" [size]="18" />Export CSV</button>
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('xlsx','gst')"><app-icon name="download" [size]="18" />Export XLSX</button>
    </div>
  </header>
  <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    @if (report.purchaseGstIncomplete) {
      <div class="alert alert-warning" role="status" data-testid="purchase-gst-incomplete">
        <p class="alert-title">Incomplete: purchase GST is unresolved.</p>
        <p>{{ report.purchaseUnresolvedComponentCount ?? 0 }} purchase component(s) totalling {{ money(report.purchaseUnresolvedAmount) }} have no GST classification, so purchase input GST and estimated GST payable below are incomplete. Classify them before using these figures for a BAS.</p>
      </div>
    }
    <section class="grid gap-4 sm:grid-cols-2 lg:grid-cols-6">@for (card of cards(report); track card[0]) {<div class="card card-body"><p class="text-md-card-title font-semibold text-md-gray-800">{{ money($any(card[1])) }}</p><p class="stat-card-label text-left">{{ card[0] }}</p></div>}</section>
    <section class="card"><div class="card-header"><h2 class="card-title">Purchase input GST</h2></div><div class="card-body max-w-xl space-y-3"><div class="flex justify-between"><span>Product lines</span><strong>{{ money(report.purchaseLineGst) }}</strong></div><div class="flex justify-between"><span>Delivery and package charges</span><strong>{{ money(report.purchaseChargeGst) }}</strong></div><div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Total purchase input GST</span><strong>{{ money(report.inventoryPurchaseGst) }}</strong></div><div class="flex justify-between"><span class="value-muted">Unclassified components</span><strong data-testid="purchase-unresolved">{{ report.purchaseUnresolvedComponentCount ?? 0 }} ({{ money(report.purchaseUnresolvedAmount) }})</strong></div></div><div class="card-footer">Unclassified components contribute no input GST; they are never estimated from an amount.</div></section>
  }
  @if (quality().length) { <div class="alert alert-warning"><p class="alert-title">Data quality notes</p><ul class="list-disc pl-5">@for (note of quality(); track note) {<li>{{ note }}</li>}</ul></div> }
</div>
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
