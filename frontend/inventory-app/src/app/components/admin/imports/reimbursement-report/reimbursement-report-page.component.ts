import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SupportSessionService } from '../../../../support-session/support-session.service';
import { ReimbursementImportJobsComponent } from './reimbursement-import-jobs.component';
import { ReimbursementReportUploadComponent } from './reimbursement-report-upload.component';

/**
 * The dedicated Reimbursement Report page (issue #1152): `/admin/imports/reimbursement-report`, the
 * one place a reimbursement report is imported from.
 *
 * <p>Uploading the ZIP Nayax emails is #1149's own workflow - accepted in seconds, imported in the
 * background, followed through the job status endpoints - and it does not belong as a fourth card on
 * Admin &rarr; Imports beside the Nayax sales upload. #888 set the precedent with
 * `/products/import`, and this is the same move: the Imports page keeps a short card that links
 * here, and the Nayax sales upload and the sale timestamp repair are untouched.</p>
 *
 * <p>Per docs/architecture.md &sect; Page composition boundary this page is the composition
 * boundary: the upload (its own file, request and refusal) and the recent uploads list (its own
 * read, refresh lifecycle and per-row expansion) are each their own feature component, wired through
 * one `@Output` and one `@Input`. What the page itself owns is the support-session answer and the
 * reload token.</p>
 *
 * <p><strong>The upload is the only way in</strong> (issue #1237). The **Import XML files** button
 * that stood here ran `POST api/imports/pending-xml`, which imported whatever XML somebody with
 * server access had left in a folder under the API's web root; that import is retired, and the
 * button with it.</p>
 *
 * <p>The route carries exactly the three guards `/admin/imports` carries
 * (`MsalGuard`, `accountStateGuard`, `requireCapability('Imports.Run')`), and no provider guard: the
 * page makes no Nayax call at all, reading and writing only this business's own records. The sidebar
 * entry is hidden without a usable provider for the same reason the Imports entry is - with no
 * provider there is no reimbursement report being emailed - and it shares the Imports menu switch
 * (`visibilityKey: 'admin.imports'`, issue #1151) rather than having a key of its own.</p>
 */
@Component({
  selector: 'app-reimbursement-report-page',
  standalone: true,
  imports: [CommonModule, RouterLink, ReimbursementReportUploadComponent, ReimbursementImportJobsComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin/imports" class="btn-link text-sm">&larr; Back to Imports</a>
          <h1 class="page-title mt-2">Reimbursement Report</h1>
          <p class="page-subtitle">
            Upload the reimbursement report ZIP Nayax emails you, and follow what each upload
            imported.
          </p>
        </div>
      </header>

      @if (sessionActive) {
        <div class="alert alert-info" role="status" data-testid="reimbursement-support-session-notice">
          <p class="alert-title">Imports are unavailable during a support session.</p>
          <p>The uploads already made are still shown.</p>
        </div>
      }

      <app-reimbursement-report-upload [disabled]="sessionActive" (uploaded)="onUploaded()" />

      <app-reimbursement-import-jobs [reloadToken]="reloadToken" />
    </div>
  `
})
export class ReimbursementReportPageComponent implements OnDestroy {
  /**
   * Whether a support session is active (issue #661). A session grants `Imports.Run` - the
   * capability this page and its endpoints are gated on - and the backend refuses the write itself
   * only by its read-only method rule, so the upload on this page is withheld. The job status
   * reads are `GET`s a session may make, so the list is left exactly as it is.
   */
  sessionActive = false;

  /**
   * Changed whenever an upload is accepted, which is how the list is asked to read again without
   * either card reaching into the other.
   */
  reloadToken = 0;

  private readonly sessionActiveSubscription: Subscription;

  constructor(supportSession: SupportSessionService) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  /** An accepted upload is a new job, so the list reads again rather than waiting for a refresh. */
  onUploaded(): void {
    this.reloadToken++;
  }
}
