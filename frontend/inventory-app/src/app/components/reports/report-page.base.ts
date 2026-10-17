import { Directive, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { Machine } from '../../models/models';
import { MachineService } from '../../services/machine.service';
import { ReportAvailability } from '../../features/reports/shared/models/reporting.models';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { loadProviderList } from '../../provider/provider-list-load';
import { ReportingFilter, ReportingService } from '../../services/reporting.service';
import { availabilityBadge, money, moneyOrState, moneyOrUnavailable, percentOrState, percentOrUnavailable } from './report-formatting';

/** The one sentence every report page shows when its machine filter could not be read. */
export const REPORT_MACHINE_FILTER_ERROR =
  'The machine list could not be loaded, so this report cannot be narrowed to one machine.';

@Directive()
export abstract class ReportPageBase<T> implements OnInit {
  loading = false;
  error = '';
  from = this.isoDate(new Date(new Date().getFullYear(), new Date().getMonth(), 1));
  to = this.isoDate(new Date());
  machineId: number | null = null;
  machines: Machine[] = [];

  /**
   * Set when the machine list could not be read for a reason that is not an availability answer
   * (issue #728). The filter is replaced by this sentence rather than silently reporting on the
   * whole business, which a machine filter nobody could populate would otherwise do.
   */
  machinesError = '';

  period = 'thisMonth';
  report: T | null = null;

  protected constructor(
    protected route: ActivatedRoute,
    protected reports: ReportingService,
    protected machineService: MachineService,
    /** The shared provider-availability signal (issue #728). */
    protected provider: ProviderAvailabilityService) {}

  ngOnInit(): void {
    loadProviderList(this.provider, 'liveFleet', () => this.machineService.getAll()).subscribe(outcome => {
      this.machines = outcome.kind === 'listed' ? outcome.items : [];
      this.machinesError = outcome.kind === 'failed' ? REPORT_MACHINE_FILTER_ERROR : '';
    });
    this.load();
  }

  abstract request(filter: ReportingFilter): Observable<T>;

  load(): void {
    this.loading = true;
    this.error = '';
    this.request({ from: this.from, to: this.to, machineId: this.machineId }).subscribe({
      next: value => { this.report = value; this.loading = false; },
      error: () => { this.error = 'Unable to load this report.'; this.loading = false; }
    });
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

  export(format: 'csv' | 'xlsx', name: string): void {
    this.reports.export(name, format, { from: this.from, to: this.to, machineId: this.machineId }).subscribe(blob => {
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `${name}.${format}`;
      anchor.click();
      URL.revokeObjectURL(url);
    });
  }

  machineLabel(machine: Machine): string {
    return machine.machineName || machine.machineNumber || `Machine ${machine.machineID}`;
  }

  money(value: number | null | undefined): string {
    return money(value);
  }

  moneyOrUnavailable(value: number | null | undefined): string {
    return moneyOrUnavailable(value);
  }

  percentOrUnavailable(value: number | null | undefined, suffix = ''): string {
    return percentOrUnavailable(value, suffix);
  }

  /** The short "not complete" label for a provider figure family (issue #730); '' when complete. */
  availabilityBadge(availability?: ReportAvailability | null): string {
    return availabilityBadge(availability);
  }

  /** A provider amount, or its availability state in place of a zero that was never measured. */
  moneyOrState(
    value: number | null | undefined,
    availability?: ReportAvailability | null,
    unavailableLabel?: string
  ): string {
    return moneyOrState(value, availability, unavailableLabel);
  }

  /** A provider percentage, or its availability state in place of a margin nobody measured. */
  percentOrState(
    value: number | null | undefined,
    availability?: ReportAvailability | null,
    unavailableLabel?: string
  ): string {
    return percentOrState(value, availability, unavailableLabel);
  }

  quality(): string[] {
    return (this.report as any)?.dataQuality?.notes ?? [];
  }

  protected isoDate(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
