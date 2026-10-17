import { Directive, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { Machine } from '../../models/models';
import { MachineService } from '../../services/machine.service';
import { ReportAvailability, ReportProviderAvailability } from '../../features/reports/shared/models/reporting.models';
import { ReportingFilter, ReportingService } from '../../services/reporting.service';
import { availabilityBadge, countOrState, money, moneyOrState, moneyOrUnavailable, percentOrState, percentOrUnavailable } from './report-formatting';

/**
 * What a report response may say about its provider data (issues #730 and #731): the two-family
 * block, for a report that shows settlement-sourced figures, or the single sales state, for one that
 * shows none. Both are optional, because an older response carries neither.
 */
interface ReportedAvailability {
  providerAvailability?: ReportProviderAvailability;
  salesAvailability?: ReportAvailability;
}

@Directive()
export abstract class ReportPageBase<T> implements OnInit {
  loading = false;
  error = '';
  from = this.isoDate(new Date(new Date().getFullYear(), new Date().getMonth(), 1));
  to = this.isoDate(new Date());
  machineId: number | null = null;
  machines: Machine[] = [];
  period = 'thisMonth';
  report: T | null = null;

  protected constructor(
    protected route: ActivatedRoute,
    protected reports: ReportingService,
    protected machineService: MachineService) {}

  ngOnInit(): void {
    this.machineService.getAll().subscribe({ next: machines => this.machines = machines ?? [], error: () => this.machines = [] });
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

  /** A provider count or quantity, or its availability state in place of a zero nobody measured. */
  countOrState(
    value: number | null | undefined,
    availability?: ReportAvailability | null,
    unavailableLabel?: string
  ): string {
    return countOrState(value, availability, unavailableLabel);
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

  /**
   * The state the API decided for every figure derived from stored provider sales (issue #730 for
   * the GST and bookkeeping reports, issue #731 for the rest).
   *
   * It reads both contract shapes on purpose: a report that shows settlement-sourced figures too
   * reports the two families together as `providerAvailability`, while one that shows none - the
   * product profitability and transaction sales reports - reports the single `salesAvailability`,
   * because labelling a figure with evidence that does not feed it is the thing this model exists to
   * prevent. Undefined when the API reported no state at all, which an older response may not.
   */
  salesState(): ReportAvailability | undefined {
    const reported = this.reportedAvailability;
    return reported.providerAvailability?.sales ?? reported.salesAvailability;
  }

  /** The same, for every figure derived from imported settlement data. */
  feeState(): ReportAvailability | undefined {
    return this.reportedAvailability.providerAvailability?.fees;
  }

  /**
   * The state for a figure that depends on both families. The API decides it with the Domain
   * least-complete rule, so this never combines two states here (AGENTS.md § Architecture rules).
   * Falling back to the sales state keeps a response that reported only the two families from
   * reading as complete.
   */
  combinedState(): ReportAvailability | undefined {
    return this.reportedAvailability.providerAvailability?.combined ?? this.salesState();
  }

  /**
   * The two availability shapes a report may carry, read off whichever one it actually has. The
   * cast is the whole point of this accessor: the generic `T` is one of seven report contracts and
   * these optional fields are what they have in common, so narrowing happens once here instead of
   * in each helper above.
   */
  private get reportedAvailability(): ReportedAvailability {
    return (this.report ?? {}) as ReportedAvailability;
  }

  /**
   * Whether any family the API reported is not complete, which is what decides whether the page
   * shows its "this is not a measured zero" banner. A report carrying no state at all is not called
   * incomplete: there is nothing to tell the reader.
   */
  providerIncomplete(): boolean {
    return [this.salesState(), this.feeState()]
      .some(availability => !!availability && availability.state !== 'Complete');
  }

  protected isoDate(date: Date): string {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
