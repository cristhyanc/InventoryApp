import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ImportService } from '../../../services/import.service';
import { ToastService } from '../../../services/toast.service';

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
    <div class="mb-6">
      <a routerLink="/admin" class="text-sm text-blue-600 hover:underline">&larr; Back to Admin</a>
      <h1 class="mt-2 text-2xl font-semibold text-slate-800">Imports</h1>
      <p class="mt-1 text-sm text-slate-500">Supported imports. These actions can change application data.</p>
    </div>

    <div class="grid gap-5 md:grid-cols-2">
      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Import Nayax sales</h2>
        <p class="mt-1 text-sm text-slate-500">Import .xlsx, .xls, or .csv sales data.</p>
        <input #salesFile type="file" accept=".xlsx,.xls,.csv" class="hidden" (change)="onSalesSelected($event)" />
        <div class="mt-4 flex flex-wrap gap-2">
          <button type="button" class="rounded-md border border-slate-300 px-3 py-2 text-sm" (click)="downloadTemplate()">Download template</button>
          <button type="button" class="rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="salesFile.click()">{{ loading ? 'Importing...' : 'Choose sales file' }}</button>
        </div>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Import products</h2>
        <p class="mt-1 text-sm text-slate-500">Refresh the product catalogue from the configured source.</p>
        <button type="button" class="mt-4 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="importProducts()">Import products</button>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Import XML files</h2>
        <p class="mt-1 text-sm text-slate-500">Process pending reimbursement XML files.</p>
        <button type="button" class="mt-4 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="importXml()">Import XML files</button>
      </section>
    </div>
  `
})
export class AdminImportsComponent {
  loading = false;

  constructor(private readonly importService: ImportService, private readonly toast: ToastService) {}

  onSalesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
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
    this.loading = true;
    this.importService.importProducts().subscribe({
      next: () => { this.loading = false; this.toast.success('Products imported successfully.'); },
      error: () => { this.loading = false; this.toast.error('Failed to import products.'); }
    });
  }

  importXml(): void {
    this.loading = true;
    this.importService.importPendingXmlFiles().subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.importedFiles} file(s) and ${result.importedReimbursements} reimbursement(s).`); },
      error: err => { this.loading = false; this.toast.error(err?.error ?? 'The XML import could not be started.'); }
    });
  }
}
