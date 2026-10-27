import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ImportService } from '../../../services/import.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * Dedicated page for the supported import workflows, split out of `AdminComponent`
 * (issue #389, Admin split 2/3): the Nayax sales file upload and its CSV template, and the pending
 * reimbursement XML import. `ImportService` remains the sole authority for both calls, so no
 * import, deduplication, transaction-parsing or reimbursement rule is reimplemented or
 * reinterpreted here.
 *
 * <p><strong>Importing the product catalogue happens on its own page</strong> (issue #888):
 * `/products/import` (`ProductImportPageComponent`) hosts the catalogue review of issue #739, so
 * this page's Import products card is a link to it and renders no review of its own. It stopped
 * reading `?review=catalogue` (issue #875) with it, which that issue's own entry point - All
 * Products' Import products button - now points straight at the new page instead.</p>
 */
@Component({
  selector: 'app-admin-imports',
  standalone: true,
  imports: [CommonModule, RouterLink],
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
            <!--
              A link to the review's own page (issue #888) rather than a button that opened it here.
              It is not withheld or disabled during a support session: the destination renders the
              support notice instead of the review, so the import cannot be started either way.
            -->
            <a routerLink="/products/import" class="btn btn-primary mt-4">Import products</a>
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
    </div>
  `
})
export class AdminImportsComponent implements OnDestroy {
  loading = false;

  /**
   * Whether a support session is active (issue #661): both imports this page runs are disabled
   * while it is, because a session grants `ImportsRun` - the capability this page and its endpoints
   * are gated on - and the backend refuses the write itself only by its read-only method rule,
   * never by withholding the capability. See `SupportSessionService.active$`. The Import products
   * link is not one of them: it starts nothing, and `/products/import` applies the same rule itself
   * (issue #888).
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

  importXml(): void {
    if (this.sessionActive) return;
    this.loading = true;
    this.importService.importPendingXmlFiles().subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.importedFiles} file(s) and ${result.importedReimbursements} reimbursement(s).`); },
      error: err => { this.loading = false; this.toast.error(err?.error ?? 'The XML import could not be started.'); }
    });
  }
}
