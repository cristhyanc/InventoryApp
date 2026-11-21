import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ReportingService, SiteCommissionAgreement } from '../../../services/reporting.service';
import { ToastService } from '../../../services/toast.service';
import { SiteListItem } from '../../../models/models';
import { SiteService } from '../../../services/site.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { BusinessTimeZoneService } from '../../../formatting/business-time-zone.service';
import { toDateValue, todaysBusinessDate } from '../../../formatting/business-time-zone';
import { EndCommissionAgreementComponent } from './end-commission-agreement.component';

/**
 * Dedicated page for the site commission agreement workflow, split out of `AdminComponent`
 * (issue #388, Admin split 1/3). Owns the agreement form and the current-agreements table;
 * `ReportingService` remains the sole authority for persisting and loading agreements, so no
 * commission formula or rate conversion is reimplemented here.
 *
 * Since issue #1254 each row of that table also offers **End agreement**, which is how a site's
 * rate changes: the add refuses any overlap, so an open-ended agreement blocked every later one for
 * ever. The action itself - its date picker, its past-date warning and its confirmation - is
 * `EndCommissionAgreementComponent`, composed in here; this page keeps the API call, the toast and
 * the reload.
 */
@Component({
  selector: 'app-site-commission-agreements',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, EndCommissionAgreementComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Site Commission Agreements</h1>
          <p class="page-subtitle">Rates are effective-dated and apply to sales from the selected date onward.</p>
        </div>
      </header>

      <section class="card">
        <div class="card-header">
          <h2 class="card-title">Site Commission Agreement</h2>
        </div>
        <div class="card-body">
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="field">
              <label class="field-label" for="commission-site">Site</label>
              <select id="commission-site" [(ngModel)]="commissionSiteId"><option [ngValue]="null">Select a site</option>@for (site of sites; track site.siteId) { <option [ngValue]="site.siteId">{{ site.name }}</option> }</select>
            </div>
            <div class="field">
              <label class="field-label" for="commission-rate">Rate (%)</label>
              <input id="commission-rate" type="number" min="0" max="100" step="0.01" [(ngModel)]="commissionRate" />
            </div>
            <div class="field">
              <label class="field-label" for="commission-effective-from">Effective from</label>
              <input id="commission-effective-from" type="date" [(ngModel)]="commissionEffectiveFrom" />
            </div>
            <div class="field">
              <label class="field-label" for="commission-frequency">Frequency</label>
              <select id="commission-frequency" [(ngModel)]="commissionFrequency"><option [ngValue]="0">None</option><option [ngValue]="1">Monthly</option><option [ngValue]="2">Quarterly</option></select>
            </div>
            <div class="field">
              <label class="field-label" for="commission-basis">Basis</label>
              <select id="commission-basis" [(ngModel)]="commissionBasis"><option [ngValue]="0">Gross Sales</option><option [ngValue]="1">Card Sales</option><option [ngValue]="2">Sales ex GST</option></select>
            </div>
            <div class="field">
              <label class="field-label" for="commission-due-days">Due days after period end (optional)</label>
              <input id="commission-due-days" type="number" min="0" [(ngModel)]="commissionDueDays" />
            </div>
          </div>
          <button type="button" class="btn btn-primary mt-3" [disabled]="loading || sessionActive" (click)="saveCommissionAgreement()">Save agreement</button>
          @if (commissionAgreements.length) {
            <div class="mt-5 overflow-x-auto border-t border-md-gray-200 pt-4">
              <h3 class="mb-2 text-sm font-semibold text-md-gray-800">Current agreements</h3>
              <table class="table">
                <thead class="table-head"><tr><th scope="col" class="table-cell">Site</th><th scope="col" class="table-cell">Effective</th><th scope="col" class="table-cell table-num">Rate</th><th scope="col" class="table-cell">Frequency</th><th scope="col" class="table-cell">Basis</th><th scope="col" class="table-cell table-num">Due days</th>@if (!sessionActive) { <th scope="col" class="table-cell">Actions</th> }</tr></thead>
                <tbody>@for (agreement of commissionAgreements; track agreement.id) { <tr class="table-row"><td class="table-cell">{{ siteName(agreement.siteId) }}</td><td class="table-cell">{{ agreement.effectiveFrom | date:'dd/MM/yyyy' }}@if (agreement.effectiveTo) { - {{ agreement.effectiveTo | date:'dd/MM/yyyy' }}}</td><td class="table-cell table-num">{{ agreement.commissionRate * 100 | number:'1.2-2' }}%</td><td class="table-cell">{{ frequencyLabel(agreement.frequency) }}</td><td class="table-cell">{{ basisLabel(agreement.basis) }}</td><td class="table-cell table-num">{{ agreement.paymentDueDaysAfterPeriodEnd ?? '—' }}</td>@if (!sessionActive) { <td class="table-cell"><app-end-commission-agreement [agreement]="agreement" [timeZoneId]="timeZoneId" [saving]="loading" (agreementEnded)="endAgreement(agreement, $event)" /></td> }</tr> }</tbody>
              </table>
            </div>
          } @else { <p class="mt-4 text-sm value-muted">No site commission agreements have been configured.</p> }
        </div>
      </section>
    </div>
  `
})
export class SiteCommissionAgreementsComponent implements OnDestroy {
  loading = false;

  /**
   * The site picker's options and the name each stored agreement is labelled with, from the light
   * site list (issue #841): this page needs a site id and a site name, so it reads the endpoint
   * that returns exactly those rather than the site summaries, whose stock and revenue figures cost
   * one Nayax read per machine in the fleet.
   */
  sites: SiteListItem[] = [];
  commissionSiteId: number | null = null;
  commissionRate = 0;

  /**
   * Today's date in the business's own time zone (issue #1273), never the UTC date - which, from
   * midnight until the business's own UTC offset has elapsed, is still yesterday in a zone ahead of
   * UTC (e.g. Sydney). Set in the constructor rather than here: it needs `businessTimeZone`, which a
   * field initializer runs before the constructor body assigns.
   */
  commissionEffectiveFrom: string;
  commissionFrequency = 0;
  commissionBasis = 0;
  commissionDueDays: number | null = null;
  commissionAgreements: SiteCommissionAgreement[] = [];

  /**
   * Whether a support session is active (issue #659, extending #554): Save agreement disables
   * itself while it is, because a session grants `SiteCommissionsManage` - the capability this
   * page would otherwise gate the write on - and the backend refuses the write itself only by
   * its read-only method rule, never by withholding the capability. See
   * `SupportSessionService.active$`.
   */
  sessionActive = false;

  /**
   * The business's own IANA time zone, handed to the End agreement action so "has this date already
   * passed?" is asked in the business's calendar rather than the browser's or UTC's (issue #499).
   * `null` while it is still unknown, which that action falls back on the browser's local date for.
   */
  timeZoneId: string | null = null;

  private readonly sessionActiveSubscription: Subscription;
  private readonly timeZoneSubscription: Subscription;

  constructor(
    private readonly reportingService: ReportingService,
    private readonly toast: ToastService,
    private readonly siteService: SiteService,
    supportSession: SupportSessionService,
    businessTimeZone: BusinessTimeZoneService
  ) {
    this.commissionEffectiveFrom = toDateValue(todaysBusinessDate(businessTimeZone.timeZoneId));
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    this.timeZoneSubscription = businessTimeZone.timeZoneId$.subscribe((zone) => (this.timeZoneId = zone));
    this.loadCommissionAgreements();
    this.siteService.getList().subscribe({ next: sites => this.sites = sites, error: () => this.sites = [] });
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
    this.timeZoneSubscription.unsubscribe();
  }

  saveCommissionAgreement(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
    if (this.commissionSiteId == null || this.commissionRate < 0 || this.commissionRate > 100 || !this.commissionEffectiveFrom || (this.commissionDueDays != null && this.commissionDueDays < 0)) {
      this.toast.error('Enter a site, valid commission rate, and effective date.');
      return;
    }
    this.loading = true;
    this.reportingService.saveSiteCommissionAgreement({ siteId: this.commissionSiteId, commissionRate: this.commissionRate / 100, effectiveFrom: this.commissionEffectiveFrom, frequency: this.commissionFrequency, basis: this.commissionBasis, paymentDueDaysAfterPeriodEnd: this.commissionDueDays }).subscribe({
      next: () => { this.loading = false; this.toast.success('Site commission agreement saved.'); this.loadCommissionAgreements(); },
      error: err => { this.loading = false; this.toast.error(this.extractErrorMessage(err, 'Unable to save the commission agreement.')); }
    });
  }

  /**
   * Ends one agreement on the confirmed date (issue #1254). The guard matches `saveCommissionAgreement`'s
   * and the rest of this build's: the control is hidden during a support session, and the handler
   * refuses as well, so the write is unavailable rather than merely invisible - the endpoint refuses
   * it either way, by the central read-only method rule.
   */
  endAgreement(agreement: SiteCommissionAgreement, endDate: string): void {
    if (this.sessionActive || agreement.id == null || !endDate) {
      return;
    }
    this.loading = true;
    this.reportingService.endSiteCommissionAgreement(agreement.id, endDate).subscribe({
      next: () => { this.loading = false; this.toast.success('Site commission agreement ended.'); this.loadCommissionAgreements(); },
      error: err => { this.loading = false; this.toast.error(this.extractErrorMessage(err, 'Unable to end the commission agreement.')); }
    });
  }

  loadCommissionAgreements(): void {
    this.reportingService.siteCommissionAgreements().subscribe({
      next: agreements => this.commissionAgreements = agreements,
      error: () => this.toast.error('Unable to load site commission agreements.')
    });
  }

  siteName(siteId: number): string { return this.sites.find(site => site.siteId === siteId)?.name ?? `Site ${siteId}`; }
  frequencyLabel(frequency: number): string { return ['None', 'Monthly', 'Quarterly'][frequency] ?? 'Unknown'; }
  basisLabel(basis: number): string { return ['Gross Sales', 'Card Sales', 'Sales ex GST'][basis] ?? 'Unknown'; }

  private extractErrorMessage(err: unknown, fallback: string): string {
    const body = (err as { error?: unknown } | undefined)?.error;
    if (typeof body === 'string') return body;
    const problem = body as { message?: string; title?: string } | undefined;
    return problem?.message ?? problem?.title ?? fallback;
  }
}
