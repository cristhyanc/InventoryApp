import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { HistoricalGstClassificationWorkflowComponent } from './historical-gst-classification-workflow.component';

/**
 * Dedicated page for the Historical GST Classification maintenance workflow (issue #433), reached
 * from the sidebar's Admin group and from the Admin landing page.
 *
 * Per docs/architecture.md § Page composition boundary (issue #191) the page is a composition
 * boundary only: the Preview/Apply actions, their state, the API calls, the confirmation and the
 * notifications all belong to `HistoricalGstClassificationWorkflowComponent`. No classification
 * rule, precedence, GST calculation or stale-preview rule exists on this page - or anywhere in the
 * frontend.
 */
@Component({
  selector: 'app-historical-gst-classification',
  standalone: true,
  imports: [CommonModule, RouterLink, HistoricalGstClassificationWorkflowComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Historical GST Classification</h1>
          <p class="page-subtitle">Applies your configured product and supplier GST rules to purchase components that are still not classified. Preview and confirm before applying; nothing is written until you do.</p>
        </div>
      </header>

      <app-historical-gst-classification-workflow></app-historical-gst-classification-workflow>
    </div>
  `
})
export class HistoricalGstClassificationComponent {}
