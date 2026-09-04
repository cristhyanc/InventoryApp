import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReportingService, SaleCostingBackfillResult } from '../../services/reporting.service';

@Component({
  selector: 'app-sale-costing-backfill',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './sale-costing-backfill.component.html'
})
export class SaleCostingBackfillComponent {
  loading = false;
  error = '';
  result: SaleCostingBackfillResult | null = null;

  constructor(private reports: ReportingService) {}

  runDryRun(): void {
    this.run(true, false);
  }

  execute(): void {
    if (!window.confirm('This will write historical sale costs to the database. Continue?')) return;
    this.run(false, true);
  }

  private run(dryRun: boolean, force: boolean): void {
    this.loading = true;
    this.error = '';
    this.reports.backfillSaleCosts(dryRun, force).subscribe({
      next: result => {
        this.result = result;
        this.loading = false;
      },
      error: err => {
        this.error = typeof err?.error === 'string' ? err.error : 'Unable to run the sale-cost backfill.';
        this.loading = false;
      }
    });
  }
}
