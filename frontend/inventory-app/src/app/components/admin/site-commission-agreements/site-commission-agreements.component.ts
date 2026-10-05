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
    <div class="mb-6">
      <a routerLink="/admin" class="text-sm text-blue-600 hover:underline">&larr; Back to Admin</a>
      <h1 class="mt-2 text-2xl font-semibold text-slate-800">Site Commission Agreements</h1>
      <p class="mt-1 text-sm text-slate-500">Rates are effective-dated and apply to sales from the selected date onward.</p>
    </div>

    <section class="rounded-xl bg-white p-6 shadow-sm">
      <h2 class="text-lg font-semibold text-slate-800">Site Commission Agreement</h2>
      <div class="mt-4 grid gap-3 sm:grid-cols-2">
        <label class="text-sm text-slate-700">Site<select class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionSiteId"><option [ngValue]="null">Select a site</option>@for (site of sites; track site.siteId) { <option [ngValue]="site.siteId">{{ site.siteName }}</option> }</select></label>
        <label class="text-sm text-slate-700">Rate (%)<input type="number" min="0" max="100" step="0.01" class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionRate" /></label>
        <label class="text-sm text-slate-700">Effective from<input type="date" class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionEffectiveFrom" /></label>
        <label class="text-sm text-slate-700">Frequency<select class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionFrequency"><option [ngValue]="0">None</option><option [ngValue]="1">Monthly</option><option [ngValue]="2">Quarterly</option></select></label>
        <label class="text-sm text-slate-700">Basis<select class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionBasis"><option [ngValue]="0">Gross Sales</option><option [ngValue]="1">Card Sales</option><option [ngValue]="2">Sales ex GST</option></select></label>
        <label class="text-sm text-slate-700">Due days after period end (optional)<input type="number" min="0" class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionDueDays" /></label>
      </div>
      <button type="button" class="mt-3 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="saveCommissionAgreement()">Save agreement</button>
      @if (commissionAgreements.length) {
        <div class="mt-5 overflow-x-auto border-t border-slate-100 pt-4">
          <h3 class="mb-2 text-sm font-semibold text-slate-700">Current agreements</h3>
          <table class="min-w-full text-left text-xs">
            <thead class="bg-slate-50 text-slate-600"><tr><th class="px-2 py-2">Site</th><th class="px-2 py-2">Effective</th><th class="px-2 py-2">Rate</th><th class="px-2 py-2">Frequency</th><th class="px-2 py-2">Basis</th><th class="px-2 py-2">Due days</th></tr></thead>
            <tbody>@for (agreement of commissionAgreements; track agreement.id) { <tr class="border-t"><td class="px-2 py-2">{{ siteName(agreement.siteId) }}</td><td class="px-2 py-2">{{ agreement.effectiveFrom | date:'dd/MM/yyyy' }}@if (agreement.effectiveTo) { - {{ agreement.effectiveTo | date:'dd/MM/yyyy' }}}</td><td class="px-2 py-2">{{ agreement.commissionRate * 100 | number:'1.2-2' }}%</td><td class="px-2 py-2">{{ frequencyLabel(agreement.frequency) }}</td><td class="px-2 py-2">{{ basisLabel(agreement.basis) }}</td><td class="px-2 py-2">{{ agreement.paymentDueDaysAfterPeriodEnd ?? '—' }}</td></tr> }</tbody>
          </table>
        </div>
      } @else { <p class="mt-4 text-sm text-slate-500">No site commission agreements have been configured.</p> }
    </section>
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
    private reportingService: ReportingService,
    private toast: ToastService,
    private siteService: SiteService
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
