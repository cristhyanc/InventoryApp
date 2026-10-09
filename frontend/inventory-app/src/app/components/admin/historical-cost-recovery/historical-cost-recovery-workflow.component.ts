import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NayaxCostBackfillResult, ReportingService } from '../../../services/reporting.service';

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
          <button type="button" class="btn btn-primary" [disabled]="loading" (click)="backfillNayax(true)">Dry Run</button>
          <button type="button" class="btn btn-primary" [disabled]="loading" (click)="applyNayaxBackfill()">Apply Nayax Cost Backfill</button>
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
export class HistoricalCostRecoveryWorkflowComponent {
  loading = false;
  error = '';
  nayaxBackfillResult: NayaxCostBackfillResult | null = null;

  constructor(private readonly reportingService: ReportingService) {}

  applyNayaxBackfill(): void {
    if (window.confirm('Apply transaction-level Nayax historical costs to eligible pending completed sales?')) {
      this.backfillNayax(false);
    }
  }

  backfillNayax(dryRun: boolean): void {
    this.loading = true;
    this.error = '';
    this.reportingService.backfillNayaxSaleCosts(dryRun).subscribe({
      next: result => { this.nayaxBackfillResult = result; this.loading = false; },
      error: err => { this.error = typeof err?.error === 'string' ? err.error : 'Unable to run the Nayax cost backfill.'; this.loading = false; }
    });
  }
}
