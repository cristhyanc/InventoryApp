import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ImportService } from '../../../services/import.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * Dedicated page for the supported import workflows, split out of `AdminComponent`
 * (issue #389, Admin split 2/3): the Nayax sales file upload and its CSV template, the product
 * catalogue refresh and the pending reimbursement XML import. `ImportService` remains the sole
 * authority for every import call, so no import, deduplication, transaction-parsing or
 * reimbursement rule is reimplemented or reinterpreted here - the entry points moved, the
 * contracts, client-side validation and messages did not.
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
            <p class="mt-1 text-sm value-muted">Refresh the product catalogue from the configured source.</p>
            <button type="button" class="btn btn-primary mt-4" [disabled]="loading || sessionActive" (click)="importProducts()">Import products</button>
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

  importProducts(): void {
    if (this.sessionActive) return;
    this.loading = true;
    this.importService.importProducts().subscribe({
      next: () => { this.loading = false; this.toast.success('Products imported successfully.'); },
      error: () => { this.loading = false; this.toast.error('Failed to import products.'); }
    });
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
