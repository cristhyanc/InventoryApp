import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subscription } from 'rxjs';
import { NayaxCostBackfillResult, ReportingService } from '../../../services/reporting.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * The Nayax Historical Cost Recovery workflow as a dedicated feature component, composed by the
 * routed `HistoricalCostRecoveryComponent` page per docs/architecture.md § Page composition
 * boundary (issue #191): the dry-run/apply actions, the confirmation, the returned counts and the
 * loading/error lifecycle live here rather than in the page. The workflow itself is unchanged by
 * that move and by the earlier split out of `AdminComponent` (issue #390, Admin split 3/3): a dry
 * run and an apply, both through `ReportingService.backfillNayaxSaleCosts`, which remains the sole
 * authority for what is eligible, what cost is recovered and what is persisted. No eligibility
 * rule, cost precedence or COGS calculation is reimplemented or reinterpreted here.
 */
@Component({
  selector: 'app-historical-cost-recovery-workflow',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (error) { <div class="alert alert-danger mb-5">{{ error }}</div> }

    <section class="card">
      <div class="card-header">
        <h2 class="card-title">Nayax Historical Cost Recovery</h2>
      </div>
      <div class="card-body">
        <p class="text-sm value-muted">Recover pending completed-sale COGS from transaction-level Nayax Product Cost Price without replacing finalized inventory-ledger costs.</p>
        <div class="mt-4 flex flex-wrap gap-2">
          <button type="button" class="btn btn-primary" [disabled]="loading || sessionActive" (click)="backfillNayax(true)">Dry Run</button>
          <button type="button" class="btn btn-primary" [disabled]="loading || sessionActive" (click)="applyNayaxBackfill()">Apply Nayax Cost Backfill</button>
        </div>
        @if (nayaxBackfillResult) {
          <div class="mt-4 grid grid-cols-2 gap-2 text-sm value-muted">
            <div>Pending completed sales: <strong>{{ nayaxBackfillResult.salesWouldBeCosted + nayaxBackfillResult.salesStillPending }}</strong></div>
            <div>With Nayax historical cost: <strong>{{ nayaxBackfillResult.salesWithNayaxCost }}</strong></div>
            <div>Recoverable: <strong>{{ nayaxBackfillResult.salesWouldBeCosted }}</strong></div>
            <div>Still missing cost: <strong>{{ nayaxBackfillResult.salesStillPending }}</strong></div>
            <div>Already costed: <strong>{{ nayaxBackfillResult.salesAlreadyCosted }}</strong></div>
            <div>Invalid cost rows: <strong>{{ nayaxBackfillResult.invalidCostRows }}</strong></div>
          </div>
        }
      </div>
    </section>
  `
})
export class HistoricalCostRecoveryWorkflowComponent implements OnDestroy {
  loading = false;
  error = '';
  nayaxBackfillResult: NayaxCostBackfillResult | null = null;

  /**
   * Whether a support session is active (issue #661): both actions are disabled while it is,
   * because a session grants `DataRepair` - the capability this page and the backfill endpoint are
   * gated on - and the backend refuses it only by its read-only method rule, never by withholding
   * the capability. The *dry run* is guarded too: it is the same `POST`, so that rule refuses it
   * as surely as the apply. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(private readonly reportingService: ReportingService, supportSession: SupportSessionService) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  applyNayaxBackfill(): void {
    if (this.sessionActive) return;
    if (window.confirm('Apply transaction-level Nayax historical costs to eligible pending completed sales?')) {
      this.backfillNayax(false);
    }
  }

  backfillNayax(dryRun: boolean): void {
    // Checked here as well as disabled in the template, so the request is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) return;
    this.loading = true;
    this.error = '';
    this.reportingService.backfillNayaxSaleCosts(dryRun).subscribe({
      next: result => { this.nayaxBackfillResult = result; this.loading = false; },
      error: err => { this.error = typeof err?.error === 'string' ? err.error : 'Unable to run the Nayax cost backfill.'; this.loading = false; }
    });
  }
}
