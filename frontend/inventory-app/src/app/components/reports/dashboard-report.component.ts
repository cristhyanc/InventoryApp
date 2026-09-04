import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReceiptService } from '../../services/receipt.service';
import { ToastService } from '../../services/toast.service';
import { ReportingFilter, ReportingService, DashboardReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';

@Component({ selector: 'app-dashboard-report', standalone: true, imports: [CommonModule, ReportFiltersComponent], template: `
<div class="mb-5 flex flex-wrap items-center justify-between gap-3"><h1 class="text-2xl font-semibold text-slate-800">Reporting Dashboard</h1><button type="button" [disabled]="importing" (click)="importPendingFiles()" class="inline-flex items-center rounded-md border border-blue-600 bg-white px-4 py-2 text-sm font-medium text-blue-600 hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-50">{{ importing ? 'Importing XML...' : 'Import XML Files' }}</button></div>
<app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500 shadow-sm">Loading report...</div> } @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> } @else if (report) {
<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
  <div class="rounded-xl bg-white p-5 shadow-sm"><div class="text-2xl font-semibold text-blue-600">{{ money(report.totalSales ?? report.sales) }}</div><div class="mt-1 text-sm text-slate-500">Total Sales</div><div class="mt-2 text-xs text-slate-500">{{ money(report.cardSales) }} Card · {{ money(report.cashSales) }} Cash</div></div>
  <div class="rounded-xl bg-white p-5 shadow-sm"><div class="text-2xl font-semibold text-blue-600">{{ money(report.grossProfit) }}</div><div class="mt-1 text-sm text-slate-500">Gross Profit</div><div class="mt-2 text-xs text-slate-500">{{ report.grossMarginPercent | number:'1.1-1' }}% margin</div></div>
  <div class="rounded-xl bg-emerald-50 p-5 shadow-sm"><div class="text-2xl font-semibold text-emerald-700">{{ money(report.netProfit) }}</div><div class="mt-1 text-sm text-emerald-800">Net Profit</div><div class="mt-2 text-xs text-emerald-700">{{ report.netMarginPercent | number:'1.1-1' }}% margin</div></div>
  <div class="rounded-xl bg-white p-5 shadow-sm"><div class="text-2xl font-semibold text-blue-600">{{ report.transactions }}</div><div class="mt-1 text-sm text-slate-500">Transactions</div><div class="mt-2 text-xs text-slate-500">{{ money(report.averageSale) }} average sale</div></div>
</div>
<div class="mt-5 grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
  <div class="rounded-lg bg-slate-50 p-4"><div class="text-lg font-semibold text-slate-700">{{ money(report.cardSales) }}</div><div class="text-xs text-slate-500">Card Sales</div></div>
  <div class="rounded-lg bg-slate-50 p-4"><div class="text-lg font-semibold text-slate-700">{{ money(report.cashSales) }}</div><div class="text-xs text-slate-500">Cash Sales</div></div>
  <div class="rounded-lg bg-slate-50 p-4"><div class="text-lg font-semibold text-slate-700">{{ money(report.costOfGoodsSold ?? 0) }}</div><div class="text-xs text-slate-500">Cost of Goods Sold</div></div>
  <div class="rounded-lg bg-slate-50 p-4"><div class="text-lg font-semibold text-slate-700">{{ money(report.siteCommission) }}</div><div class="text-xs text-slate-500">Site Commission</div></div>
  <div class="rounded-lg bg-slate-50 p-4"><div class="text-lg font-semibold text-slate-700">{{ money(report.nayaxFeesIncludingGst ?? report.nayaxFeesExGst) }}</div><div class="text-xs text-slate-500">Nayax Fees</div></div>
</div>
<section class="mt-5 rounded-xl bg-white p-5 shadow-sm"><div class="mb-4 flex items-center justify-between"><h2 class="text-lg font-semibold text-slate-800">Nayax Reconciliation</h2><span class="rounded px-2 py-1 text-sm font-semibold" [class]="statusClass(report.reconciliationStatus)">{{ report.reconciliationStatus }} {{ report.isReconciled ? '✓' : '' }}</span></div><div class="max-w-xl space-y-3 text-sm"><div class="flex justify-between"><span>Card Sales</span><strong>{{ money(report.cardSales) }}</strong></div><div class="flex justify-between"><span>Nayax Fees</span><strong>-{{ money(report.nayaxFeesIncludingGst ?? report.nayaxFeesExGst) }}</strong></div><div class="flex justify-between border-t border-slate-200 pt-3"><span class="font-semibold">Expected Reimbursement</span><strong>{{ money(report.expectedReimbursement) }}</strong></div><div class="flex justify-between"><span>Actual Reimbursement</span><strong>{{ report.reconciliationStatus === 'Pending' ? 'Pending' : money(report.actualReimbursement) }}</strong></div><div class="flex justify-between"><span>Difference</span><strong>{{ report.reconciliationStatus === 'Pending' ? '—' : money(report.reimbursementDifference) }}</strong></div></div><p class="mt-3 text-xs text-slate-500">Reconciled when the absolute difference is at most {{ money(report.reconciliationTolerance ?? 0.01) }}. Reimbursements are matched using their covered transaction period.</p></section>
}
` })
export class DashboardReportComponent extends ReportPageBase<DashboardReport> {
  importing = false;
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService, private receiptService: ReceiptService, private toast: ToastService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<DashboardReport> { return this.reports.dashboard(filter); }
  statusClass(status?: string): string {
    return status === 'Reconciled' ? 'text-emerald-700 bg-emerald-50' : 'text-amber-700 bg-amber-50';
  }
  importPendingFiles(): void {
    if (this.importing) return;
    this.importing = true;
    this.receiptService.importPendingFiles().subscribe({
      next: (result) => {
        this.importing = false;
        this.toast.success(
          `Imported ${result.importedFiles} file(s) and ${result.importedReimbursements} reimbursement(s). ` +
          `Skipped ${result.skippedFiles}; failed ${result.failedFiles}.`,
          'XML import complete'
        );
        this.load();
      },
      error: (err) => {
        this.importing = false;
        this.toast.error(err.error ?? 'The XML import could not be started.');
      }
    });
  }
}
