import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { HistoricalCostRecoveryWorkflowComponent } from './historical-cost-recovery-workflow.component';

/**
 * Dedicated page for the Nayax Historical Cost Recovery workflow, split out of `AdminComponent`
 * (issue #390, Admin split 3/3). Per docs/architecture.md § Page composition boundary (issue
 * #191) the page stays a composition boundary: it renders the page heading and composes
 * `HistoricalCostRecoveryWorkflowComponent`, which owns the dry-run/apply actions, the
 * confirmation, the returned counts and the loading/error state. `ReportingService.backfillNayaxSaleCosts`
 * remains the sole authority for what is eligible, what cost is recovered and what is persisted,
 * and no eligibility rule, cost precedence or COGS calculation exists on this page.
 */
@Component({
  selector: 'app-historical-cost-recovery',
  standalone: true,
  imports: [CommonModule, RouterLink, HistoricalCostRecoveryWorkflowComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Nayax Historical Cost Recovery</h1>
          <p class="page-subtitle">This action can change historical cost of goods sold. Run the dry run first.</p>
        </div>
      </header>

      <app-historical-cost-recovery-workflow></app-historical-cost-recovery-workflow>
    </div>
  `
})
export class HistoricalCostRecoveryComponent {}
