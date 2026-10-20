import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ImportService } from '../../../services/import.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { CatalogueReviewComponent } from './catalogue-review/catalogue-review.component';

/**
 * Dedicated page for the supported import workflows, split out of `AdminComponent`
 * (issue #389, Admin split 2/3): the Nayax sales file upload and its CSV template, the product
 * catalogue import and the pending reimbursement XML import. `ImportService` remains the sole
 * authority for the sales and reimbursement import calls, so no import, deduplication,
 * transaction-parsing or reimbursement rule is reimplemented or reinterpreted here.
 *
 * <p><strong>Importing the product catalogue is the catalogue review</strong> (issue #739, parent
 * #329 decision C2). There is no direct catalogue import any more: the button opens
 * `CatalogueReviewComponent`, which previews Nayax's catalogue against the business's own rows and
 * applies the operator's decisions per entry. That is what lets a business that maintains its own
 * products import at all - the direct import had to be refused for one (issue #736), because it
 * recognised a product only by Nayax's id and would have duplicated every manual row. The page
 * composes the review and owns nothing of its workflow (AGENTS.md § Frontend rules).</p>
 */
@Component({
  selector: 'app-admin-imports',
  standalone: true,
  imports: [CommonModule, RouterLink, CatalogueReviewComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Imports</h1>
          <p class="page-subtitle">Supported imports. These actions can change application data.</p>
        </div>
      </header>

      <div class="grid gap-5 md:grid-cols-2">
        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Import Nayax sales</h2>
            <p class="mt-1 text-sm value-muted">Import .xlsx, .xls, or .csv sales data.</p>
            <input #salesFile type="file" accept=".xlsx,.xls,.csv" class="hidden" (change)="onSalesSelected($event)" />
            <div class="mt-4 flex flex-wrap gap-2">
              <button type="button" class="btn btn-secondary" (click)="downloadTemplate()">Download template</button>
              <button type="button" class="btn btn-primary" [disabled]="loading || sessionActive" (click)="salesFile.click()">{{ loading ? 'Importing...' : 'Choose sales file' }}</button>
            </div>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Import products</h2>
            <p class="mt-1 text-sm value-muted">
              Review the Nayax catalogue against your own products and categories, then link, create
              or update them per entry. Nothing is written until you apply the review.
            </p>
            <button type="button" class="btn btn-primary mt-4" [disabled]="loading || sessionActive || reviewing" (click)="importProducts()">Import products</button>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Import XML files</h2>
            <p class="mt-1 text-sm value-muted">Process pending reimbursement XML files.</p>
            <button type="button" class="btn btn-primary mt-4" [disabled]="loading || sessionActive" (click)="importXml()">Import XML files</button>
          </div>
        </section>
      </div>

      @if (reviewing) {
        <app-catalogue-review (closed)="closeReview()" />
      }
    </div>
  `
})
export class AdminImportsComponent implements OnDestroy {
  loading = false;

  /** Whether the catalogue review is on screen (issue #739). It owns its own loading lifecycle. */
  reviewing = false;

  /**
   * Whether a support session is active (issue #661): every import on this page is disabled while
   * it is, because a session grants `ImportsRun` - the capability this page and its endpoints are
   * gated on - and the backend refuses the write itself only by its read-only method rule, never
   * by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly importService: ImportService,
    private readonly toast: ToastService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  onSalesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    // Checked here as well as disabled in the template, so the import is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (!/\.(xlsx|xls|csv)$/i.test(file.name)) {
      this.toast.error('Only .xlsx, .xls, or .csv files are supported.');
      return;
    }
    this.loading = true;
    this.importService.importNayaxSales(file).subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.imported} sales, updated ${result.updated}, skipped ${result.skipped}.`); },
      error: err => { this.loading = false; this.toast.error(err?.error?.message ?? err?.error ?? 'Failed to import Nayax sales.'); }
    });
  }

  downloadTemplate(): void {
    const headers = ['TransactionID', 'TransactionStatusId', 'MachineID', 'NayaxProductId', 'MachineName', 'SettlementValue', 'PaymentMethod', 'ProductName', 'Product Cost Price', 'MachineAuthorizationTime'];
    const sample = ['1001', '12', '42', '987654', 'Machine A', '12.50', 'Card', 'Coke Zero', '1.100000', '2026-09-02 14:30:00'];
    const url = URL.createObjectURL(new Blob([[headers.join(','), sample.join(',')].join('\n')], { type: 'text/csv;charset=utf-8;' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'nayax-sales-import-template.csv'; anchor.click(); URL.revokeObjectURL(url);
  }

  /**
   * Opens the catalogue review (issue #739). It is the product catalogue import: the review reads
   * Nayax's catalogue and writes nothing until the operator applies their decisions, so this page
   * starts no import of its own and reports no result. Guarded here as well as disabled in the
   * template, so the action is actually unavailable in a support session rather than merely inert.
   */
  importProducts(): void {
    if (this.sessionActive) return;
    this.reviewing = true;
  }

  closeReview(): void {
    this.reviewing = false;
  }

  importXml(): void {
    if (this.sessionActive) return;
    this.loading = true;
    this.importService.importPendingXmlFiles().subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.importedFiles} file(s) and ${result.importedReimbursements} reimbursement(s).`); },
      error: err => { this.loading = false; this.toast.error(err?.error ?? 'The XML import could not be started.'); }
    });
  }
}
