import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { NayaxSaleTimestampRepairWorkflowComponent } from './nayax-sale-timestamp-repair-workflow.component';

/**
 * Dedicated page for the Nayax Sale Timestamp Repair maintenance workflow (issue #487, the Admin UI
 * over #472's Preview/Apply API), reached from the sidebar's Admin group and from the Admin landing
 * page.
 *
 * Per docs/architecture.md § Page composition boundary (issue #191) the page is a composition
 * boundary only: the source form, the optional reconciliation window, the Preview and Apply
 * requests, the confirmation, the expiry handling and every reported outcome belong to
 * `NayaxSaleTimestampRepairWorkflowComponent` and the display components it composes. No evidence
 * rule, timestamp decision, business-date conversion, revenue or costing figure or stale-plan rule
 * exists on this page - or anywhere in the frontend.
 */
@Component({
  selector: 'app-nayax-sale-timestamp-repair',
  standalone: true,
  imports: [CommonModule, RouterLink, NayaxSaleTimestampRepairWorkflowComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Nayax Sale Timestamp Repair</h1>
          <p class="page-subtitle">
            Repairs stored Nayax sale instants that were never the authoritative ones, from verified
            source evidence and one transaction at a time. Preview what would change, review it row
            by row, then confirm: nothing is written until you do, and a sale no source covers is
            left exactly as it is.
          </p>
        </div>
      </header>

      <app-nayax-sale-timestamp-repair-workflow></app-nayax-sale-timestamp-repair-workflow>
    </div>
  `
})
export class NayaxSaleTimestampRepairComponent {}
