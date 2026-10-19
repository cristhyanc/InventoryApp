import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { NayaxProcessingFeeRate, NayaxSettingsService } from '../../../services/nayax-settings.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { NayaxConnectionWizardComponent } from './nayax-connection-wizard.component';

/**
 * Dedicated page for the Owner-only Nayax integration settings, split out of `AdminComponent`
 * (issue #388, Admin split 1/3).
 *
 * It owns the fee-rate form, the configured-rate history and the current/future/previous status
 * presentation; `NayaxSettingsService` remains the sole authority for persisting and loading rates,
 * so no fee formula is reimplemented here.
 *
 * The connection wizard of issue #538 is **composed**, not grown into this page: per
 * docs/architecture.md § Page composition boundary (issue #191) its steps, its write-only token
 * field, its checklist, its support request and its save/re-test lifecycle belong to
 * `NayaxConnectionWizardComponent`. The two sections are independent - connecting Nayax and
 * configuring an estimated fee rate are different jobs - and neither reads the other's state.
 */
@Component({
  selector: 'app-nayax-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NayaxConnectionWizardComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <!-- "Integrations" since issue #729: the menu, the Admin hub card and this heading name
               one destination, so they say one name. The URL is unchanged. -->
          <h1 class="page-title mt-2">Integrations</h1>
          <p class="page-subtitle">Connect this business's own Nayax account, and configure the estimated processing fee rate used for current-period reporting until actual Nayax fee data is imported. Imported reimbursement data always takes precedence.</p>
        </div>
      </header>

      <app-nayax-connection-wizard class="mb-5 block"></app-nayax-connection-wizard>

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
          <button type="button" class="btn btn-primary mt-3" data-testid="save-fee-rate" [disabled]="loading || sessionActive" (click)="saveFeeRate()">Save rate</button>
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
export class NayaxSettingsComponent implements OnDestroy {
  loading = false;
  feeRates: NayaxProcessingFeeRate[] = [];
  feeExGst = 0.17;
  effectiveFrom = new Date().toISOString().slice(0, 10);

  /**
   * Whether a support session is active (issue #660): Save rate is disabled while it is, because a
   * session grants `IntegrationManage` - the capability this page's write would otherwise be gated
   * on - and the backend refuses the write itself only by its read-only method rule, never by
   * withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly nayaxSettings: NayaxSettingsService,
    private readonly toast: ToastService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    this.loadFeeRates();
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
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
    if (this.sessionActive) {
      return;
    }
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
