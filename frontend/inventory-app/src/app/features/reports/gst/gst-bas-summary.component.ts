import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReportingFilter, ReportingService } from '../../../services/reporting.service';
import { availabilityBadge, money, moneyOrState } from '../../../components/reports/report-formatting';
import { ReportAvailability } from '../shared/models/reporting.models';
import { GstBasSummary, GstBasSummaryFigures } from './models/gst-bas-summary.model';

/**
 * The GST and BAS summary shown inside Bookkeeping for a business registered for GST (issue #1318).
 *
 * <p>It is its own component rather than more markup on the Bookkeeping page because it is a
 * distinct workflow with its own request, loading and error lifecycle (AGENTS.md § Frontend rules:
 * page components are composition boundaries). The page hands it the filter it actually reported
 * on - never the live filter controls - so the summary always describes the same period as the
 * figures beside it.</p>
 *
 * <p>It calculates nothing. Every amount, the credit total, the net estimate, its
 * payable/credit/nil answer, whether the estimate is incomplete and whether there is a summary at
 * all come from the server's one shared GST calculation.</p>
 */
@Component({
  selector: 'app-gst-bas-summary',
  standalone: true,
  imports: [CommonModule],
  template: `
  @if (loading) {
    <section class="card" data-testid="gst-bas-summary-loading"><div class="card-body text-center"><span class="value-muted">Loading GST summary...</span></div></section>
  } @else if (error) {
    <div class="alert alert-danger" data-testid="gst-bas-summary-error">{{ error }}</div>
  } @else if (machineFiltered) {
    <section class="card" data-testid="gst-bas-summary-machine-filtered">
      <div class="card-header"><h2 class="card-title">GST summary</h2></div>
      <div class="card-body">
        <p>Select <strong>All machines</strong> to see the GST summary. GST credits on stock purchases and operating expenses are recorded for the whole business and are not split between machines, so there is no correct per-machine figure to show.</p>
      </div>
    </section>
  }
  @if (figures; as gst) {
    <section class="card" data-testid="gst-bas-summary">
      <div class="card-header">
        <h2 class="card-title">GST summary (BAS labels)</h2>
        <span class="badge" [ngClass]="gst.isIncomplete ? 'badge-warning' : 'badge-success'" data-testid="gst-bas-summary-state">{{ gst.isIncomplete ? 'Estimate incomplete' : 'Estimate complete' }}</span>
      </div>
      <div class="card-body max-w-2xl space-y-3">
        @if (gst.isIncomplete) {
          <div class="alert alert-warning" role="status" data-testid="gst-bas-summary-incomplete">
            <p class="alert-title">This estimate is not final.</p>
            <p>Some of the figures below rest on records that are missing, unclassified or estimated. The notes under this summary say which. Settle those before using the figures to review a BAS.</p>
          </div>
        }
        <div class="flex justify-between"><span>G1 — Total sales (including GST)</span><strong data-testid="bas-g1">{{ moneyOrState(gst.totalSales, salesState) }}</strong></div>
        <div class="flex justify-between pl-4"><span class="value-muted">of which GST-free sales</span><strong data-testid="bas-gst-free-sales">{{ moneyOrState(gst.gstFreeSales, salesState) }}</strong></div>
        <div class="flex justify-between border-t border-md-gray-200 pt-3"><span>1A — GST on sales</span><strong data-testid="bas-1a">{{ moneyOrState(gst.gstOnSales, salesState) }}</strong></div>
        @if (gst.noGstRuleSales > 0) {
          <p class="value-muted" data-testid="bas-assumed-taxable">{{ money(gst.noGstRuleSales) }} of those sales are products with no GST rule yet. They are treated as taxable, so 1A includes GST on them. Setting those products' GST rules is what makes this figure final.</p>
        }
        <div class="flex justify-between border-t border-md-gray-200 pt-3"><span>1B — GST credits</span><strong data-testid="bas-1b">{{ moneyOrState(gst.gstCredits, feeState) }}</strong></div>
        <div class="flex justify-between pl-4"><span class="value-muted">Stock purchases, delivery and packaging</span><strong data-testid="bas-1b-purchases">{{ money(gst.purchaseGstCredits) }}</strong></div>
        <div class="flex justify-between pl-4"><span class="value-muted">Operating expenses</span><strong data-testid="bas-1b-expenses">{{ money(gst.operatingExpenseGstCredits) }}</strong></div>
        <div class="flex justify-between pl-4"><span class="value-muted">Nayax processing fees</span><strong data-testid="bas-1b-fees">{{ moneyOrState(gst.nayaxFeeGstCredits, feeState) }}</strong></div>
        @if (gst.purchaseUnresolvedComponentCount > 0) {
          <p class="value-muted" data-testid="bas-unresolved-purchases">{{ gst.purchaseUnresolvedComponentCount }} purchase component(s) totalling {{ money(gst.purchaseUnresolvedAmount) }} have no GST classification. They add no credit at all and are never estimated from an amount.</p>
        }
        <div class="flex justify-between border-t border-md-gray-200 pt-3 text-md-card-title"><span class="font-semibold">{{ netGstLabel(gst) }}</span><strong data-testid="bas-net-gst">{{ moneyOrState(gst.netGst, netGstState) }}</strong></div>
        @if (availabilityBadge(netGstState); as badge) {
          <p class="value-muted" data-testid="bas-net-gst-availability">{{ badge }}</p>
        }
        @if (notes.length) {
          <div class="alert alert-warning" data-testid="gst-bas-summary-notes">
            <p class="alert-title">What this estimate rests on</p>
            <ul class="list-disc pl-5">@for (note of notes; track note) {<li>{{ note }}</li>}</ul>
          </div>
        }
      </div>
      <div class="card-footer">
        <p data-testid="bas-scope">These are Restockroo's own figures for <strong>{{ summary!.from | date: 'd MMM y' }}</strong> to <strong>{{ summary!.to | date: 'd MMM y' }}</strong>, counted on each sale's business date in your business's time zone, across the whole business and every machine. Sales come from completed Nayax transactions; GST credits come from the purchases, operating expenses and Nayax fees recorded here.</p>
        <p>They are an <strong>estimate for reviewing the GST section of a BAS</strong>, not a lodgement figure and not accounting advice. Restockroo has no cash or non-cash accounting mode: everything is counted on the date it was recorded here. If you report on a cash basis, or you have GST on income or purchases that Restockroo does not hold, reconcile these figures against your accounting records before you lodge.</p>
      </div>
    </section>
  }
  `
})
export class GstBasSummaryComponent implements OnChanges {
  /**
   * The period and scope the page reported on. The parent sets it when a report loads, never from
   * the filter controls as they are edited, so this summary and the figures beside it can never
   * describe two different periods. `null` loads nothing.
   */
  @Input() filter: ReportingFilter | null = null;

  summary: GstBasSummary | null = null;
  loading = false;
  error = '';

  constructor(private reports: ReportingService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['filter']) this.load();
  }

  private load(): void {
    this.summary = null;
    this.error = '';
    if (!this.filter) return;
    this.loading = true;
    this.reports.gstBasSummary(this.filter).subscribe({
      next: value => { this.summary = value; this.loading = false; },
      error: () => { this.error = 'Unable to load the GST summary.'; this.loading = false; }
    });
  }

  /** The figures, or nothing at all - which is what a business that is not registered sees. */
  get figures(): GstBasSummaryFigures | null {
    return !this.loading && !this.error && this.summary?.status === 'Available'
      ? this.summary.figures ?? null
      : null;
  }

  /** Whether the server declined to summarise because a machine filter is active. */
  get machineFiltered(): boolean {
    return this.summary?.status === 'MachineFiltered';
  }

  get salesState(): ReportAvailability | undefined {
    return this.figures?.providerAvailability?.sales;
  }

  get feeState(): ReportAvailability | undefined {
    return this.figures?.providerAvailability?.fees;
  }

  /**
   * The state governing the net estimate. The server decides it with the Domain least-complete
   * rule; falling back to the sales state keeps a response that reported neither from reading as
   * complete.
   */
  get netGstState(): ReportAvailability | undefined {
    return this.figures?.netGstAvailability ?? this.salesState;
  }

  get notes(): string[] {
    return this.summary?.dataQuality?.notes ?? [];
  }

  /**
   * The name of the net figure, from the answer the server decided. Nothing here looks at the
   * sign: payable, credit and nil are its three answers and this only spells them.
   */
  netGstLabel(figures: GstBasSummaryFigures): string {
    switch (figures.netGstOutcome) {
      case 'Payable':
        return 'Estimated GST payable';
      case 'Credit':
        return 'Estimated GST credit (shown as a negative amount)';
      default:
        return 'Estimated net GST (nil)';
    }
  }

  money(value: number | null | undefined): string {
    return money(value);
  }

  moneyOrState(value: number | null | undefined, availability?: ReportAvailability | null): string {
    return moneyOrState(value, availability);
  }

  availabilityBadge(availability?: ReportAvailability | null): string {
    return availabilityBadge(availability);
  }
}
