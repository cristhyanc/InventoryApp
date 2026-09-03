import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { ReportingService, ReportingFilter, DashboardReport, BookkeepingReport, DailyReport, ReconciliationReport, MachineReport, ProductReport, GstReport } from '../../services/reporting.service';
import { MachineService } from '../../services/machine.service';
import { Machine } from '../../models/models';

@Component({
  selector: 'app-reports',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './reports.component.html'
})
export class ReportsComponent implements OnInit {
  kind = 'dashboard';
  loading = false;
  error = '';
  from = this.isoDate(new Date(new Date().getFullYear(), new Date().getMonth(), 1));
  to = this.isoDate(new Date());
  machineId: number | null = null;
  machines: Machine[] = [];
  period = 'thisMonth';
  report: DashboardReport | BookkeepingReport | DailyReport | ReconciliationReport | MachineReport | ProductReport | GstReport | null = null;

  constructor(private route: ActivatedRoute, private reports: ReportingService, private machineService: MachineService) {}

  ngOnInit(): void {
    this.machineService.getAll().subscribe({ next: machines => this.machines = machines ?? [], error: () => this.machines = [] });
    this.route.data.subscribe(data => { this.kind = data['kind'] ?? 'dashboard'; this.load(); });
  }

  selectPeriod(period: string): void {
    this.period = period;
    const today = new Date();
    if (period === 'thisMonth') {
      this.from = this.isoDate(new Date(today.getFullYear(), today.getMonth(), 1));
      this.to = this.isoDate(today);
    } else if (period === 'lastMonth') {
      this.from = this.isoDate(new Date(today.getFullYear(), today.getMonth() - 1, 1));
      this.to = this.isoDate(new Date(today.getFullYear(), today.getMonth(), 0));
    } else if (period === 'currentFy') {
      const year = today.getMonth() >= 6 ? today.getFullYear() : today.getFullYear() - 1;
      this.from = this.isoDate(new Date(year, 6, 1));
      this.to = this.isoDate(today);
    } else if (period === 'previousFy') {
      const year = today.getMonth() >= 6 ? today.getFullYear() - 1 : today.getFullYear() - 2;
      this.from = this.isoDate(new Date(year, 6, 1));
      this.to = this.isoDate(new Date(year + 1, 5, 30));
    }
  }

  machineLabel(machine: Machine): string {
    return machine.machineName || machine.machineNumber || `Machine ${machine.machineID}`;
  }

  load(): void {
    this.loading = true; this.error = '';
    const filter: ReportingFilter = { from: this.from, to: this.to, machineId: this.machineId };
    const request: Observable<any> = this.kind === 'bookkeeping' ? this.reports.bookkeeping(filter)
      : this.kind === 'daily' ? this.reports.daily(filter)
      : this.kind === 'reconciliation' ? this.reports.reconciliation(filter)
      : this.kind === 'machines' ? this.reports.machines(filter)
      : this.kind === 'products' ? this.reports.products(filter)
      : this.kind === 'gst' ? this.reports.gst(filter)
      : this.reports.dashboard(filter);
    request.subscribe({ next: value => { this.report = value; this.loading = false; }, error: () => { this.error = 'Unable to load this report.'; this.loading = false; } });
  }

  export(format: 'csv' | 'xlsx'): void {
    const names: Record<string, string> = { bookkeeping: 'bookkeeping', daily: 'daily', reconciliation: 'reconciliation', machines: 'machine-profitability', products: 'product-profitability', gst: 'gst' };
    this.reports.export(names[this.kind] ?? 'bookkeeping', format, { from: this.from, to: this.to, machineId: this.machineId }).subscribe(blob => {
      const url = URL.createObjectURL(blob); const anchor = document.createElement('a');
      anchor.href = url; anchor.download = `${names[this.kind] ?? 'report'}.${format}`; anchor.click(); URL.revokeObjectURL(url);
    });
  }

  title(): string { return ({ dashboard: 'Reporting Dashboard', bookkeeping: 'Monthly Bookkeeping', daily: 'Daily Sales / Reimbursement', reconciliation: 'Nayax Reconciliation', machines: 'Machine Profitability', products: 'Product Profitability', gst: 'GST / BAS Accounting Aid' } as Record<string, string>)[this.kind]; }
  money(value: number | null | undefined): string { return new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' }).format(value ?? 0); }
  otherOperatingExpenses(report: BookkeepingReport): number {
    return Math.max(0, (report.otherOperatingExpenses ?? 0) - (report.deliveryCosts ?? 0) - (report.packageCosts ?? 0));
  }
  totalOperatingCosts(report: BookkeepingReport): number {
    return (report.nayaxFeesIncludingGst ?? 0) + (report.siteCommission ?? 0) + (report.otherOperatingExpenses ?? 0);
  }
  quality(): string[] { return this.report?.dataQuality?.notes ?? []; }
  private isoDate(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
