import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { ReceiptService } from '../../services/receipt.service';
import { ReportingService, SaleCostingBackfillResult } from '../../services/reporting.service';
import { ToastService } from '../../services/toast.service';

@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="mb-6">
      <h1 class="text-2xl font-semibold text-slate-800">Admin</h1>
      <p class="mt-1 text-sm text-slate-500">Imports and maintenance tools. These actions can change application data.</p>
    </div>

    @if (error) { <div class="mb-5 rounded-lg bg-red-50 p-4 text-sm text-red-700">{{ error }}</div> }
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

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">COGS backfill</h2>
        <p class="mt-1 text-sm text-slate-500">Preview historical sale costing before applying changes.</p>
        <div class="mt-4 flex flex-wrap gap-2">
          <button type="button" class="rounded-md border border-blue-300 px-3 py-2 text-sm text-blue-700" [disabled]="loading" (click)="backfill(true)">Dry run</button>
          <button type="button" class="rounded-md bg-amber-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="executeBackfill()">Execute backfill</button>
        </div>
        @if (backfillResult) {
          <div class="mt-4 grid grid-cols-2 gap-2 text-sm text-slate-600">
            <div>Costed: <strong>{{ backfillResult.costedCount }}</strong></div>
            <div>Estimated: <strong>{{ backfillResult.legacyEstimatedCount }}</strong></div>
            <div>Pending: <strong>{{ backfillResult.pendingCount }}</strong></div>
            <div>Error: <strong>{{ backfillResult.errorCount }}</strong></div>
          </div>
        }
      </section>
    </div>
  `
})
export class AdminComponent {
  loading = false;
  error = '';
  backfillResult: SaleCostingBackfillResult | null = null;

  constructor(
    private machineService: MachineService,
    private productService: ProductService,
    private receiptService: ReceiptService,
    private reportingService: ReportingService,
    private toast: ToastService
  ) {}

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
    this.machineService.importNayaxSales(file).subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.imported} sales, updated ${result.updated}, skipped ${result.skipped}.`); },
      error: err => { this.loading = false; this.toast.error(err?.error?.message ?? err?.error ?? 'Failed to import Nayax sales.'); }
    });
  }

  downloadTemplate(): void {
    const headers = ['TransactionID', 'TransactionStatusId', 'MachineID', 'NayaxProductId', 'MachineName', 'SettlementValue', 'PaymentMethod', 'ProductName', 'Quantity', 'MachineAuthorizationTime'];
    const sample = ['1001', '12', '42', '987654', 'Machine A', '12.50', 'Card', 'Coke Zero', '1', '2026-09-02 14:30:00'];
    const url = URL.createObjectURL(new Blob([[headers.join(','), sample.join(',')].join('\n')], { type: 'text/csv;charset=utf-8;' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'nayax-sales-import-template.csv'; anchor.click(); URL.revokeObjectURL(url);
  }

  importProducts(): void {
    this.loading = true;
    this.productService.importProducts().subscribe({
      next: () => { this.loading = false; this.toast.success('Products imported successfully.'); },
      error: () => { this.loading = false; this.toast.error('Failed to import products.'); }
    });
  }

  importXml(): void {
    this.loading = true;
    this.receiptService.importPendingFiles().subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.importedFiles} file(s) and ${result.importedReimbursements} reimbursement(s).`); },
      error: err => { this.loading = false; this.toast.error(err?.error ?? 'The XML import could not be started.'); }
    });
  }

  executeBackfill(): void {
    if (window.confirm('This will write historical sale costs to the database. Continue?')) this.backfill(false, true);
  }

  backfill(dryRun: boolean, force = false): void {
    this.loading = true;
    this.reportingService.backfillSaleCosts(dryRun, force).subscribe({
      next: result => { this.backfillResult = result; this.loading = false; },
      error: err => { this.error = typeof err?.error === 'string' ? err.error : 'Unable to run the COGS backfill.'; this.loading = false; }
    });
  }
}
