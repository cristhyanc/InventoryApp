import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NayaxProcessingFeeRate, NayaxSettingsService } from '../../../services/nayax-settings.service';
import { ToastService } from '../../../services/toast.service';

/**
 * Dedicated page for the Nayax processing-fee rate configuration workflow, split out of
 * `AdminComponent` (issue #388, Admin split 1/3). Owns the fee-rate form, the configured-rate
 * history and the current/future/previous status presentation; `NayaxSettingsService` remains
 * the sole authority for persisting and loading rates, so no fee formula is reimplemented here.
 */
@Component({
  selector: 'app-nayax-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <div class="mb-6">
      <a routerLink="/admin" class="text-sm text-blue-600 hover:underline">&larr; Back to Admin</a>
      <h1 class="mt-2 text-2xl font-semibold text-slate-800">Nayax Settings</h1>
      <p class="mt-1 text-sm text-slate-500">Used for current-period reporting when actual Nayax processing fee data has not yet been imported. Imported reimbursement data always takes precedence.</p>
    </div>

    <section class="rounded-xl bg-white p-6 shadow-sm">
      <h2 class="text-lg font-semibold text-slate-800">Estimated processing fee rate</h2>
      <div class="mt-4 grid gap-3 sm:grid-cols-3">
        <label class="text-sm text-slate-700 sm:col-span-2">Estimated Processing Fee per Card Transaction (ex GST)
          <input type="number" min="0" step="0.0001" required class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="feeExGst" />
        </label>
        <label class="text-sm text-slate-700">Effective from
          <input type="date" required class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="effectiveFrom" />
        </label>
      </div>
      <button type="button" class="mt-3 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="saveFeeRate()">Save rate</button>
      <div class="mt-5 border-t border-slate-100 pt-4">
        <h3 class="mb-2 text-sm font-semibold text-slate-700">Configured settings</h3>
        @if (feeRates.length) {
          <div class="overflow-x-auto">
            <table class="min-w-full text-left text-sm">
              <thead class="bg-slate-50 text-xs text-slate-600">
                <tr><th class="px-3 py-2">Effective from</th><th class="px-3 py-2">Fee per card transaction (ex GST)</th><th class="px-3 py-2">Status</th></tr>
              </thead>
              <tbody>
                @for (rate of feeRates; track rate.id ?? rate.effectiveFrom) {
                  <tr class="border-t border-slate-100">
                    <td class="px-3 py-2">{{ rate.effectiveFrom | date:'dd/MM/yyyy' }}</td>
                    <td class="px-3 py-2">{{ rate.feeExGst | currency:'AUD':'symbol':'1.4-4' }}</td>
                    <td class="px-3 py-2">
                      @if (isCurrentFeeRate(rate)) {
                        <span class="rounded-full bg-emerald-100 px-2 py-1 text-xs font-medium text-emerald-700">Current</span>
                      } @else if (isFutureFeeRate(rate)) {
                        <span class="rounded-full bg-blue-100 px-2 py-1 text-xs font-medium text-blue-700">Scheduled</span>
                      } @else {
                        <span class="text-xs text-slate-500">Previous</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          <p class="text-sm text-slate-500">No Nayax processing-fee settings configured.</p>
        }
      </div>
    </section>
  `
})
export class NayaxSettingsComponent {
  loading = false;
  feeRates: NayaxProcessingFeeRate[] = [];
  feeExGst = 0.17;
  effectiveFrom = new Date().toISOString().slice(0, 10);

  constructor(private readonly nayaxSettings: NayaxSettingsService, private readonly toast: ToastService) {
    this.loadFeeRates();
  }

  isCurrentFeeRate(rate: NayaxProcessingFeeRate): boolean {
    return this.currentFeeRate?.effectiveFrom === rate.effectiveFrom;
  }

  isFutureFeeRate(rate: NayaxProcessingFeeRate): boolean {
    return new Date(`${rate.effectiveFrom.slice(0, 10)}T00:00:00`).getTime() > new Date().setHours(0, 0, 0, 0);
  }

  private get currentFeeRate(): NayaxProcessingFeeRate | undefined {
    const today = new Date().setHours(23, 59, 59, 999);
    return this.feeRates.find(rate =>
      new Date(`${rate.effectiveFrom.slice(0, 10)}T00:00:00`).getTime() <= today);
  }

  loadFeeRates(): void {
    this.nayaxSettings.getRates().subscribe({
      next: rates => {
        this.feeRates = rates;
        if (rates[0]) this.feeExGst = rates[0].feeExGst;
      },
      error: () => this.toast.error('Failed to load Nayax settings.')
    });
  }

  saveFeeRate(): void {
    if (this.feeExGst === null || this.feeExGst < 0 || !this.effectiveFrom) {
      this.toast.error('Enter a non-negative fee and effective date.');
      return;
    }
    this.loading = true;
    this.nayaxSettings.saveRate({ effectiveFrom: this.effectiveFrom, feeExGst: this.feeExGst }).subscribe({
      next: () => { this.loading = false; this.toast.success('Nayax processing-fee rate saved.'); this.loadFeeRates(); },
      error: err => { this.loading = false; this.toast.error(err?.error ?? 'Failed to save Nayax settings.'); }
    });
  }
}
