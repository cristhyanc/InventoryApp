import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { ReportAvailability } from '../../features/reports/shared/models/reporting.models';
import { MachineService } from '../../services/machine.service';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
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
  <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [machinesError]="machinesError" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    @if (report.purchaseGstIncomplete) {
      <div class="alert alert-warning" role="status" data-testid="purchase-gst-incomplete">
        <p class="alert-title">Incomplete: purchase GST is unresolved.</p>
        <p>{{ report.purchaseUnresolvedComponentCount ?? 0 }} purchase component(s) totalling {{ money(report.purchaseUnresolvedAmount) }} have no GST classification, so purchase input GST and estimated GST payable below are incomplete. Classify them before using these figures for a BAS.</p>
      </div>
    }
    @if (providerNotice(report); as notice) {
      <div class="alert alert-warning" role="status" data-testid="provider-availability">
        <p class="alert-title">{{ notice.title }}</p>
        <p>{{ notice.detail }}</p>
      </div>
    }
    <section class="grid gap-4 sm:grid-cols-2 lg:grid-cols-6">@for (card of cards(report); track card.label) {<div class="card card-body"><p class="text-md-card-title font-semibold text-md-gray-800">{{ moneyOrState(card.value, card.availability) }}</p><p class="stat-card-label text-left">{{ card.label }}</p>@if (availabilityBadge(card.availability); as badge) {<p class="value-muted text-left" [attr.data-testid]="'availability-' + card.key">{{ badge }}</p>}</div>}</section>
    <section class="card"><div class="card-header"><h2 class="card-title">Purchase input GST</h2></div><div class="card-body max-w-xl space-y-3"><div class="flex justify-between"><span>Product lines</span><strong>{{ money(report.purchaseLineGst) }}</strong></div><div class="flex justify-between"><span>Delivery and package charges</span><strong>{{ money(report.purchaseChargeGst) }}</strong></div><div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">Total purchase input GST</span><strong>{{ money(report.inventoryPurchaseGst) }}</strong></div><div class="flex justify-between"><span class="value-muted">Unclassified components</span><strong data-testid="purchase-unresolved">{{ report.purchaseUnresolvedComponentCount ?? 0 }} ({{ money(report.purchaseUnresolvedAmount) }})</strong></div></div><div class="card-footer">Unclassified components contribute no input GST; they are never estimated from an amount.</div></section>
  }
  @if (quality().length) { <div class="alert alert-warning"><p class="alert-title">Data quality notes</p><ul class="list-disc pl-5">@for (note of quality(); track note) {<li>{{ note }}</li>}</ul></div> }
</div>
` })
export class GstReportComponent extends ReportPageBase<GstReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService, provider: ProviderAvailabilityService) { super(route, reports, machines, provider); }
  request(filter: ReportingFilter): Observable<GstReport> { return this.reports.gst(filter); }

  /**
   * Label/value/availability triples only: every amount is read straight from the API result and
   * every state is the one the API decided, never derived here.
   *
   * The two provider families carry their own state (issue #730). GST on expenses and purchase
   * input GST deliberately carry none: they come from this business's own records, so they stay
   * visible and exact even when no provider data exists at all. Net GST carries the combined
   * state, because it subtracts both provider figures.
   */
  cards(report: GstReport): GstCard[] {
    const provider = report.providerAvailability;
    return [
      { key: 'taxable-sales', label: 'Taxable sales', value: report.taxableSales, availability: provider?.sales },
      { key: 'gst-on-sales', label: 'GST on sales', value: report.gstOnSales, availability: provider?.sales },
      { key: 'taxable-fees', label: 'Taxable fees', value: report.taxableFees, availability: provider?.fees },
      { key: 'gst-on-fees', label: 'GST on fees', value: report.gstOnFees, availability: provider?.fees },
      { key: 'expense-gst', label: 'GST on expenses', value: report.operatingExpenseGst },
      { key: 'purchase-gst', label: 'Purchase input GST', value: report.inventoryPurchaseGst },
      {
        key: 'net-gst',
        label: report.purchaseGstIncomplete ? 'Estimated GST payable (incomplete)' : 'Estimated GST payable',
        value: report.netGst,
        availability: report.netGstAvailability
      }
    ];
  }

  /**
   * The single banner that says this period's provider figures are not a measured zero. It repeats
   * nothing the API's own data-quality notes already say in detail; it exists so the state is
   * visible beside the figures rather than only at the bottom of the page.
   */
  providerNotice(report: GstReport): { title: string; detail: string } | null {
    const states = [report.providerAvailability?.sales, report.providerAvailability?.fees]
      .filter(availability => availability && availability.state !== 'Complete');
    if (!states.length) return null;
    return {
      title: 'Incomplete: provider data does not cover this whole period.',
      detail: 'Sales GST and Nayax fee GST below are shown with their state instead of a figure where nothing was '
        + 'recorded. Purchase input GST and GST on expenses come from your own records and are unaffected. '
        + 'Estimated GST payable is incomplete until the provider data for this period is complete.'
    };
  }
}

interface GstCard {
  key: string;
  label: string;
  value: number | null | undefined;
  availability?: ReportAvailability;
}
