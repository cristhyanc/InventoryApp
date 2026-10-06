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
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Nayax Settings</h1>
          <p class="page-subtitle">Used for current-period reporting when actual Nayax processing fee data has not yet been imported. Imported reimbursement data always takes precedence.</p>
        </div>
      </header>

      <section class="card">
        <div class="card-header">
          <h2 class="card-title">Estimated processing fee rate</h2>
        </div>
        <div class="card-body">
          <div class="grid gap-4 sm:grid-cols-3">
            <div class="field sm:col-span-2">
              <label class="field-label" for="nayax-fee-ex-gst">Estimated Processing Fee per Card Transaction (ex GST)</label>
              <input id="nayax-fee-ex-gst" type="number" min="0" step="0.0001" required [(ngModel)]="feeExGst" />
            </div>
            <div class="field">
              <label class="field-label" for="nayax-effective-from">Effective from</label>
              <input id="nayax-effective-from" type="date" required [(ngModel)]="effectiveFrom" />
            </div>
          </div>
          <button type="button" class="btn btn-primary mt-3" [disabled]="loading" (click)="saveFeeRate()">Save rate</button>
          <div class="mt-5 border-t border-md-gray-200 pt-4">
            <h3 class="mb-2 text-sm font-semibold text-md-gray-800">Configured settings</h3>
            @if (feeRates.length) {
              <div class="overflow-x-auto">
                <table class="table">
                  <thead class="table-head">
                    <tr><th scope="col" class="table-cell">Effective from</th><th scope="col" class="table-cell">Fee per card transaction (ex GST)</th><th scope="col" class="table-cell">Status</th></tr>
                  </thead>
                  <tbody>
                    @for (rate of feeRates; track rate.id ?? rate.effectiveFrom) {
                      <tr class="table-row">
                        <td class="table-cell">{{ rate.effectiveFrom | date:'dd/MM/yyyy' }}</td>
                        <td class="table-cell">{{ rate.feeExGst | currency:'AUD':'symbol':'1.4-4' }}</td>
                        <td class="table-cell">
                          @if (isCurrentFeeRate(rate)) {
                            <span class="badge badge-success">Current</span>
                          } @else if (isFutureFeeRate(rate)) {
                            <span class="badge badge-info">Scheduled</span>
                          } @else {
                            <span class="text-xs value-muted">Previous</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <p class="text-sm value-muted">No Nayax processing-fee settings configured.</p>
            }
          </div>
        </div>
      </section>
    </div>
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
