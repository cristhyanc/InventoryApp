import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { NayaxCostBackfillResult, ReportingService } from '../../../services/reporting.service';

/**
 * Dedicated page for the Nayax Historical Cost Recovery workflow, split out of `AdminComponent`
 * (issue #390, Admin split 3/3). The workflow itself is unchanged: a dry run and an apply, both
 * through `ReportingService.backfillNayaxSaleCosts`, which remains the sole authority for what is
 * eligible, what cost is recovered and what is persisted. The page owns only the dry-run/apply
 * actions, the returned counts and the error message - no eligibility rule, cost precedence or
 * COGS calculation is reimplemented or reinterpreted here.
 */
@Component({
  selector: 'app-historical-cost-recovery',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <div class="mb-6">
      <a routerLink="/admin" class="text-sm text-blue-600 hover:underline">&larr; Back to Admin</a>
      <h1 class="mt-2 text-2xl font-semibold text-slate-800">Nayax Historical Cost Recovery</h1>
      <p class="mt-1 text-sm text-slate-500">This action can change historical cost of goods sold. Run the dry run first.</p>
    </div>

    @if (error) { <div class="mb-5 rounded-lg bg-red-50 p-4 text-sm text-red-700">{{ error }}</div> }

    <section class="rounded-xl bg-white p-6 shadow-sm">
      <h2 class="text-lg font-semibold text-slate-800">Nayax Historical Cost Recovery</h2>
      <p class="mt-1 text-sm text-slate-500">Recover pending completed-sale COGS from transaction-level Nayax Product Cost Price without replacing finalized inventory-ledger costs.</p>
      <div class="mt-4 flex flex-wrap gap-2">
        <button type="button" class="rounded-md border border-blue-300 px-3 py-2 text-sm text-blue-700" [disabled]="loading" (click)="backfillNayax(true)">Dry Run</button>
        <button type="button" class="rounded-md bg-amber-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="applyNayaxBackfill()">Apply Nayax Cost Backfill</button>
      </div>
      @if (nayaxBackfillResult) {
        <div class="mt-4 grid grid-cols-2 gap-2 text-sm text-slate-600">
          <div>Pending completed sales: <strong>{{ nayaxBackfillResult.salesWouldBeCosted + nayaxBackfillResult.salesStillPending }}</strong></div>
          <div>With Nayax historical cost: <strong>{{ nayaxBackfillResult.salesWithNayaxCost }}</strong></div>
          <div>Recoverable: <strong>{{ nayaxBackfillResult.salesWouldBeCosted }}</strong></div>
          <div>Still missing cost: <strong>{{ nayaxBackfillResult.salesStillPending }}</strong></div>
          <div>Already costed: <strong>{{ nayaxBackfillResult.salesAlreadyCosted }}</strong></div>
          <div>Invalid cost rows: <strong>{{ nayaxBackfillResult.invalidCostRows }}</strong></div>
        </div>
      }
    </section>
  `
})
export class HistoricalCostRecoveryComponent {
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
