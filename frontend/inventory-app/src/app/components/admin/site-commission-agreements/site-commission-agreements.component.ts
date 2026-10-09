import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ReportingService, SiteCommissionAgreement } from '../../../services/reporting.service';
import { ToastService } from '../../../services/toast.service';
import { Site } from '../../../models/models';
import { SiteService } from '../../../services/site.service';

/**
 * Dedicated page for the site commission agreement workflow, split out of `AdminComponent`
 * (issue #388, Admin split 1/3). Owns the agreement form and the current-agreements table;
 * `ReportingService` remains the sole authority for persisting and loading agreements, so no
 * commission formula or rate conversion is reimplemented here.
 */
@Component({
  selector: 'app-site-commission-agreements',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
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
              <select id="commission-site" [(ngModel)]="commissionSiteId"><option [ngValue]="null">Select a site</option>@for (site of sites; track site.siteId) { <option [ngValue]="site.siteId">{{ site.siteName }}</option> }</select>
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
          <button type="button" class="btn btn-primary mt-3" [disabled]="loading" (click)="saveCommissionAgreement()">Save agreement</button>
          @if (commissionAgreements.length) {
            <div class="mt-5 overflow-x-auto border-t border-md-gray-200 pt-4">
              <h3 class="mb-2 text-sm font-semibold text-md-gray-800">Current agreements</h3>
              <table class="table">
                <thead class="table-head"><tr><th scope="col" class="table-cell">Site</th><th scope="col" class="table-cell">Effective</th><th scope="col" class="table-cell table-num">Rate</th><th scope="col" class="table-cell">Frequency</th><th scope="col" class="table-cell">Basis</th><th scope="col" class="table-cell table-num">Due days</th></tr></thead>
                <tbody>@for (agreement of commissionAgreements; track agreement.id) { <tr class="table-row"><td class="table-cell">{{ siteName(agreement.siteId) }}</td><td class="table-cell">{{ agreement.effectiveFrom | date:'dd/MM/yyyy' }}@if (agreement.effectiveTo) { - {{ agreement.effectiveTo | date:'dd/MM/yyyy' }}}</td><td class="table-cell table-num">{{ agreement.commissionRate * 100 | number:'1.2-2' }}%</td><td class="table-cell">{{ frequencyLabel(agreement.frequency) }}</td><td class="table-cell">{{ basisLabel(agreement.basis) }}</td><td class="table-cell table-num">{{ agreement.paymentDueDaysAfterPeriodEnd ?? '—' }}</td></tr> }</tbody>
              </table>
            </div>
          } @else { <p class="mt-4 text-sm value-muted">No site commission agreements have been configured.</p> }
        </div>
      </section>
    </div>
  `
})
export class SiteCommissionAgreementsComponent {
  loading = false;
  sites: Site[] = [];
  commissionSiteId: number | null = null;
  commissionRate = 0;
  commissionEffectiveFrom = new Date().toISOString().slice(0, 10);
  commissionFrequency = 0;
  commissionBasis = 0;
  commissionDueDays: number | null = null;
  commissionAgreements: SiteCommissionAgreement[] = [];

  constructor(
    private readonly reportingService: ReportingService,
    private readonly toast: ToastService,
    private readonly siteService: SiteService
  ) {
    this.loadCommissionAgreements();
    this.siteService.getAll().subscribe({ next: sites => this.sites = sites, error: () => this.sites = [] });
  }

  saveCommissionAgreement(): void {
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

  loadCommissionAgreements(): void {
    this.reportingService.siteCommissionAgreements().subscribe({
      next: agreements => this.commissionAgreements = agreements,
      error: () => this.toast.error('Unable to load site commission agreements.')
    });
  }

  siteName(siteId: number): string { return this.sites.find(site => site.siteId === siteId)?.siteName ?? `Site ${siteId}`; }
  frequencyLabel(frequency: number): string { return ['None', 'Monthly', 'Quarterly'][frequency] ?? 'Unknown'; }
  basisLabel(basis: number): string { return ['Gross Sales', 'Card Sales', 'Sales ex GST'][basis] ?? 'Unknown'; }

  private extractErrorMessage(err: unknown, fallback: string): string {
    const body = (err as { error?: unknown } | undefined)?.error;
    if (typeof body === 'string') return body;
    const problem = body as { message?: string; title?: string } | undefined;
    return problem?.message ?? problem?.title ?? fallback;
  }
}
