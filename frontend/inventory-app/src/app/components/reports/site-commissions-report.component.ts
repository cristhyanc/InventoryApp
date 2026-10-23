import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { SiteListItem } from '../../models/models';
import { ReportingService, SiteCommissionReport, SiteCommissionRow } from '../../services/reporting.service';
import { SiteService } from '../../services/site.service';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { loadProviderList } from '../../provider/provider-list-load';
import { SupportSessionService } from '../../support-session/support-session.service';

@Component({
  selector: 'app-site-commissions-report',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
<div class="page">
  <header class="page-header"><h1 class="page-title">Site Commissions</h1></header>
  <div class="card">
    <div class="card-body flex flex-wrap items-center gap-2">
      @for (option of periods; track option[0]) { <button type="button" class="btn btn-sm" [class.btn-primary]="period === option[0]" [class.btn-secondary]="period !== option[0]" (click)="selectPeriod(option[0])">{{ option[1] }}</button> }
      @if (sitesError) {
        <p data-testid="site-filter-error" class="ml-auto mr-2 text-md-body text-md-danger-text">{{ sitesError }}</p>
      } @else if (sites.length > 0) {
        <label class="ml-auto mr-2 text-md-body text-md-gray-600">Site <select class="ml-2 inline-block w-auto" [(ngModel)]="siteId"><option [ngValue]="null">All Sites</option>@for (site of sites; track site.siteId) { <option [ngValue]="site.siteId">{{ site.name }}</option> }</select></label>
      }
      <button type="button" class="btn btn-primary ml-auto" (click)="load()">Apply</button>
    </div>
    @if (period === 'custom') { <div class="card-footer flex flex-wrap items-center gap-3"><label class="text-md-body text-md-gray-600">From <input class="ml-1 inline-block w-auto" type="date" [(ngModel)]="from" /></label><label class="text-md-body text-md-gray-600">To <input class="ml-1 inline-block w-auto" type="date" [(ngModel)]="to" /></label></div> }
  </div>
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report && unavailable) {
    <div class="alert alert-warning" role="status" data-testid="commission-unavailable">
      <p class="alert-title">Site commission is unavailable for this business right now.</p>
      <p>{{ unavailableDetail }}</p>
      <p>Nothing has changed about the commission agreements or the payments already recorded; they are read back as soon as the provider is usable again.</p>
    </div>
  }
  @else if (report) {
    <div class="card overflow-x-auto"><table class="table"><thead class="table-head"><tr><th scope="col" class="table-cell">Site</th><th scope="col" class="table-cell">Frequency</th><th scope="col" class="table-cell">Basis</th><th scope="col" class="table-cell">Eligible Sales</th><th scope="col" class="table-cell">Rate</th><th scope="col" class="table-cell">Due</th><th scope="col" class="table-cell">Paid</th><th scope="col" class="table-cell">Outstanding</th><th scope="col" class="table-cell">Status</th><th scope="col" class="table-cell">Details</th></tr></thead><tbody>@for (row of report.rows; track row.siteId) { <tr class="table-row"><td class="table-cell">{{ row.siteName }}</td><td class="table-cell">{{ row.frequency }}</td><td class="table-cell">{{ row.basis }}</td><td class="table-cell">{{ money(row.eligibleSales) }}</td><td class="table-cell">{{ row.commissionRate * 100 | number:'1.2-2' }}%</td><td class="table-cell">{{ money(row.commissionDue) }}</td><td class="table-cell">{{ money(row.paid) }}</td><td class="table-cell">{{ money(row.outstanding) }}</td><td class="table-cell">{{ row.status }}</td><td class="table-cell"><button type="button" class="btn btn-link" (click)="selected = row">Details</button></td></tr> }</tbody></table></div>
    @if (selected) {
      <section class="card">
        <div class="card-header">
          <div><h2 class="card-title">{{ selected.siteName }}</h2><p class="page-subtitle">{{ selected.periodStart | date:'dd/MM/yyyy' }} - {{ selected.periodEnd | date:'dd/MM/yyyy' }}</p></div>
          <button type="button" class="btn btn-sm btn-secondary" (click)="selected = null">Close</button>
        </div>
        <div class="card-body">
          <div class="grid gap-2 sm:grid-cols-2"><div>Gross Sales <strong class="float-right">{{ money(selected.grossSales) }}</strong></div><div>Card Sales <strong class="float-right">{{ money(selected.cardSales) }}</strong></div><div>Cash Sales <strong class="float-right">{{ money(selected.cashSales) }}</strong></div><div>Commissionable Sales <strong class="float-right">{{ money(selected.eligibleSales) }}</strong></div><div>Commission Due <strong class="float-right">{{ money(selected.commissionDue) }}</strong></div><div>Outstanding <strong class="float-right">{{ money(selected.outstanding) }}</strong></div></div>
          <h3 class="card-title mt-5">Machine breakdown</h3>
          <table class="table mt-2"><thead class="table-head"><tr><th scope="col" class="table-cell">Machine</th><th scope="col" class="table-cell table-num">Transactions</th><th scope="col" class="table-cell table-num">Sales</th><th scope="col" class="table-cell table-num">Commission Due</th></tr></thead><tbody>@for (machine of selected.machines; track machine.machineId) { <tr class="table-row"><td class="table-cell">{{ machine.machineName }}</td><td class="table-cell table-num">{{ machine.transactionCount }}</td><td class="table-cell table-num">{{ money(machine.grossSales) }}</td><td class="table-cell table-num">{{ money(machine.commissionDue) }}</td></tr> }</tbody></table>
          <h3 class="card-title mt-5">Payment history</h3>
          @if (!selected.payments.length) { <p class="value-muted">No payments recorded for this period.</p> } @else { @for (payment of selected.payments; track payment.id) { <div class="mt-2">{{ payment.paymentDate | date:'dd/MM/yyyy' }} - {{ money(payment.amount) }} {{ payment.notes }}</div> } }
          @if (selected.outstanding > 0 && !sessionActive) { <div class="mt-4 flex flex-wrap items-end gap-2 border-t border-md-gray-200 pt-4"><label class="field-label">Payment date <input class="mt-1" type="date" [(ngModel)]="paymentDate" /></label><label class="field-label">Amount <input class="mt-1 w-24" type="number" min="0.01" step="0.01" [(ngModel)]="paymentAmount" /></label><label class="field-label">Notes <input class="mt-1" [(ngModel)]="paymentNotes" /></label><button type="button" class="btn btn-primary" (click)="recordPayment()">Record Payment</button></div> }
          @if (selected.dataQuality) { <p class="mt-4 text-md-warning-text">{{ selected.dataQuality }}</p> }
        </div>
      </section>
    }
  }
  @if (selected && !unavailable) {
    <div><button type="button" class="btn btn-sm btn-secondary" (click)="printDetails()">Print details</button></div>
    <details class="card card-body">
      <summary class="card-title cursor-pointer">Products sold</summary>
      <table class="table mt-4">
        <thead class="table-head"><tr><th scope="col" class="table-cell">Product</th><th scope="col" class="table-cell table-num">Total Vends</th></tr></thead>
        <tbody>@for (product of selected.products; track product.productName) { <tr class="table-row"><td class="table-cell">{{ product.productName }}</td><td class="table-cell table-num">{{ product.totalVends }}</td></tr> }</tbody>
      </table>
    </details>
  }
</div>
` })
export class SiteCommissionsReportComponent implements OnInit, OnDestroy {
  /**
   * The site filter's options, from the light site list (issue #841): the filter submits a site id
   * and shows a site name, so it reads the endpoint that returns exactly those instead of the site
   * summaries, whose stock and revenue figures cost one Nayax read per machine in the fleet.
   */
  sites: SiteListItem[] = []; report: SiteCommissionReport | null = null; selected: SiteCommissionRow | null = null;
  /**
   * Set when the site list could not be read for a reason that is not an availability answer
   * (issue #728). Sites come from the live Nayax fleet, so a business with no provider has no site
   * filter at all - which is not the same thing as a site read that failed, and must not look like
   * "All Sites" either way.
   */
  sitesError = '';
  siteId: number | null = null; loading = false; error = ''; period = 'thisMonth';
  paymentDate = this.date(new Date()); paymentAmount = 0; paymentNotes = '';
  from = this.date(new Date(new Date().getFullYear(), new Date().getMonth(), 1)); to = this.date(new Date());
  periods = [['thisMonth', 'This Month'], ['lastMonth', 'Last Month'], ['thisQuarter', 'This Quarter'], ['lastQuarter', 'Last Quarter'], ['currentFy', 'Current FY'], ['previousFy', 'Previous FY'], ['custom', 'Custom']];

  /**
   * Whether a support session is active (issue #659, extending #554): Record Payment hides
   * itself while it is, because a session grants `SiteCommissionsManage` - the capability this
   * page would otherwise gate the write on - and the backend refuses the write itself only by
   * its read-only method rule, never by withholding the capability. See
   * `SupportSessionService.active$`.
   */
  sessionActive = false;

  /**
   * Whether the API answered that commission could not be calculated at all (issue #731). A site is
   * known only from the live Nayax machine fleet, so when the provider is not usable the response
   * carries no rows - and an empty table here would read as "no site is owed anything", which is
   * the opposite of what happened. The page shows the reason instead of a table.
   *
   * <para>Only an explicit `false` withholds the table: a response that carries no flag at all is
   * an older API, which only ever returned an available report.</para>
   */
  get unavailable(): boolean {
    return this.report?.isAvailable === false;
  }

  /** The sentence the API gave for an unavailable report, with a safe fallback. */
  get unavailableDetail(): string {
    return this.report?.unavailableDetail
      ?? 'Nayax is not usable for this business right now, so no machine can be matched to a site. The amounts are '
      + 'not a measured zero.';
  }

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly reports: ReportingService,
    private readonly siteService: SiteService,
    private readonly provider: ProviderAvailabilityService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }
  ngOnInit(): void {
    loadProviderList(this.provider, 'machineFleet', () => this.siteService.getList()).subscribe(outcome => {
      this.sites = outcome.kind === 'listed' ? outcome.items : [];
      this.sitesError = outcome.kind === 'failed'
        ? 'The site list could not be loaded, so this report cannot be narrowed to one site.'
        : '';
    });
    this.load();
  }
  ngOnDestroy(): void { this.sessionActiveSubscription.unsubscribe(); }
  load(): void { this.loading = true; this.error = ''; this.selected = null; this.reports.siteCommissions({ from: this.from, to: this.to }, this.siteId).subscribe({ next: report => { this.report = report; this.loading = false; }, error: () => { this.error = 'Unable to load site commissions.'; this.loading = false; } }); }
  selectPeriod(period: string): void { this.period = period; const now = new Date(); const month = now.getMonth(); const year = now.getFullYear(); if (period === 'thisMonth') { this.from = this.date(new Date(year, month, 1)); this.to = this.date(now); } else if (period === 'lastMonth') { this.from = this.date(new Date(year, month - 1, 1)); this.to = this.date(new Date(year, month, 0)); } else if (period.includes('Quarter')) { const quarter = Math.floor(month / 3) + (period === 'lastQuarter' ? -1 : 0); this.from = this.date(new Date(year, quarter * 3, 1)); this.to = this.date(period === 'thisQuarter' ? now : new Date(year, quarter * 3 + 3, 0)); } else if (period === 'currentFy' || period === 'previousFy') { const startYear = year - (month < 6 ? 1 : 0) - (period === 'previousFy' ? 1 : 0); this.from = this.date(new Date(startYear, 6, 1)); this.to = this.date(period === 'currentFy' ? now : new Date(startYear + 1, 5, 30)); } }
  // Checked here as well as hidden in the template, so the write is actually unavailable rather
  // than merely invisible - the endpoint refuses it either way.
  recordPayment(): void { if (this.sessionActive || !this.selected || this.paymentAmount <= 0) { return; } this.reports.recordCommissionPayment(this.selected.siteId, this.from, this.to, this.paymentDate, this.paymentAmount, this.paymentNotes).subscribe({ next: () => { this.paymentAmount = 0; this.paymentNotes = ''; this.load(); }, error: () => this.error = 'Unable to record commission payment.' }); }
  printDetails(): void {
    if (!this.selected) return;
    const row = this.selected;
    const escape = (value: string) => value.replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]!));
    const money = (value: number) => this.money(value);
    const machines = row.machines.map(machine => `<tr><td>${escape(machine.machineName)}</td><td>${machine.transactionCount}</td><td>${money(machine.grossSales)}</td><td>${money(machine.commissionDue)}</td></tr>`).join('');
    const products = row.products.map(product => `<tr><td>${escape(product.productName)}</td><td>${product.totalVends}</td><td>${money(product.totalSales)}</td></tr>`).join('');
    const payments = row.payments.map(payment => `<tr><td>${escape(payment.paymentDate.slice(0, 10))}</td><td>${money(payment.amount)}</td><td>${escape(payment.notes ?? '')}</td></tr>`).join('');
    const popup = window.open('', '_blank', 'width=900,height=700');
    if (!popup) return;
    popup.document.write(`<html><head><title>${escape(row.siteName)} commission details</title><style>body{font-family:Arial,sans-serif;margin:32px;color:#1e293b}table{width:100%;border-collapse:collapse;margin:16px 0}th,td{padding:8px;border-bottom:1px solid #cbd5e1;text-align:left}th{text-align:left;background:#f8fafc}td:not(:first-child),th:not(:first-child){text-align:right}.summary{display:grid;grid-template-columns:repeat(2,1fr);gap:8px}</style></head><body><h1>${escape(row.siteName)} - Site Commission Details</h1><p>${escape(row.periodStart.slice(0, 10))} to ${escape(row.periodEnd.slice(0, 10))}</p><div class="summary"><div>Gross Sales: <strong>${money(row.grossSales)}</strong></div><div>Card Sales: <strong>${money(row.cardSales)}</strong></div><div>Cash Sales: <strong>${money(row.cashSales)}</strong></div><div>Eligible Sales: <strong>${money(row.eligibleSales)}</strong></div><div>Commission Due: <strong>${money(row.commissionDue)}</strong></div><div>Outstanding: <strong>${money(row.outstanding)}</strong></div></div><h2>Machine breakdown</h2><table><thead><tr><th>Machine</th><th>Transactions</th><th>Sales</th><th>Commission Due</th></tr></thead><tbody>${machines}</tbody></table><h2>Products sold</h2><table><thead><tr><th>Product</th><th>Total Vends</th><th>Total Sales</th></tr></thead><tbody>${products}</tbody></table><h2>Payment history</h2><table><thead><tr><th>Date</th><th>Amount</th><th>Notes</th></tr></thead><tbody>${payments}</tbody></table></body></html>`);
    popup.document.close();
    popup.focus();
    popup.print();
  }
  money(value: number): string { return new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' }).format(value); }
  private date(value: Date): string { return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`; }
}
