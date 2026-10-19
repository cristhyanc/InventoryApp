import { Component, Input, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Observable, Subject, Subscription, catchError, of, startWith, switchMap, tap } from 'rxjs';
import { MachineService } from '../../../services/machine.service';
import { AccessService } from '../../../access/access.service';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import { MachineLatestSale, MachineLatestSales } from '../../../models/models';

/**
 * The machine-details page's Sales panel (issue #768): the latest sales Nayax reports for one
 * machine, newest first, with its own Refresh action.
 *
 * <p>It owns the whole lifecycle of that one read - the request, the in-flight state, the failure
 * state, the provider-unavailable states and the honest empty state - so the machine page only has
 * to supply the machine identity and lay the panel out (`docs/architecture.md` § Page composition
 * boundary).</p>
 *
 * <p><strong>A rolling window, not a history.</strong> The endpoint behind it is Nayax's own "most
 * recent sales transactions for this machine", with no documented date range, row count or paging,
 * so this panel says exactly that and promises nothing more. It is also a pure read: refreshing
 * imports no sale, moves no stock and recosts nothing.</p>
 *
 * <p><strong>A different machine discards the previous one's answer.</strong> Changing
 * {@link machineId} switches the inner request stream, so an in-flight read for the machine the
 * operator has navigated away from is unsubscribed and its late answer ignored, and the rows on
 * screen are cleared at the same moment - the previous machine's sales can never be read under the
 * new machine's heading (the shape issue #669 established for a superseded business context).</p>
 *
 * <p><strong>Nothing financial is decided here.</strong> The API withholds each sale's amount and
 * the currency that labels it from a role without `Dashboard.ViewFinancials` (issue #698's
 * pattern), so an unprivileged caller never receives them. This panel only chooses the sentence to
 * show in that column, from the same `GET /api/me/access` capability every other hidden figure on
 * this page reads, and never converts a withheld or absent amount to `0`.</p>
 */
@Component({
  selector: 'app-machine-latest-sales',
  standalone: true,
  imports: [CommonModule, BusinessDateTimePipe],
  templateUrl: './machine-latest-sales.component.html'
})
export class MachineLatestSalesComponent implements OnDestroy {
  /**
   * The machine whose latest sales to show, or `null`/`undefined` before the page has resolved one.
   * Setting a different machine starts that machine's read and discards the previous machine's.
   */
  @Input()
  set machineId(value: number | null | undefined) {
    const machineId = typeof value === 'number' && !Number.isNaN(value) && value > 0 ? value : null;
    if (machineId === this.requestedMachineId) {
      return;
    }

    this.requestedMachineId = machineId;
    this.machineId$.next(machineId);
  }

  /** The latest answer for the current machine, or `null` while there is none to show. */
  latest: MachineLatestSales | null = null;

  /** A request for the current machine is in flight; the Refresh action is disabled while it is. */
  loading = false;

  /**
   * The read itself failed - a transport error, a cancellation or an upstream Nayax failure. It is
   * deliberately not the same state as an unavailable provider: a failed request says nothing about
   * whether this machine has sold anything, and the right next step is to try again.
   */
  error: string | null = null;

  /**
   * Whether the signed-in member's role grants `Dashboard.ViewFinancials`. Starts `true`, the
   * fail-open default every other capability-gated control in this codebase uses while the
   * capability has not been read yet; the API is what actually withholds the amounts.
   */
  canViewFinancials = true;

  private requestedMachineId: number | null = null;
  private readonly machineId$ = new Subject<number | null>();
  private readonly refresh$ = new Subject<void>();
  private readonly subscription: Subscription;
  private readonly financialsAccessSubscription: Subscription;

  constructor(private readonly machineService: MachineService, access: AccessService) {
    this.financialsAccessSubscription = access
      .allows('Dashboard.ViewFinancials')
      .subscribe((allowed) => (this.canViewFinancials = allowed));

    this.subscription = this.machineId$
      .pipe(
        switchMap((machineId) => {
          // The previous machine's rows leave the screen at the moment the machine changes, not
          // when the new answer arrives, so nothing stale is ever read under the new heading.
          this.latest = null;
          this.error = null;

          return this.refresh$.pipe(
            startWith(undefined),
            switchMap(() => this.request(machineId))
          );
        })
      )
      .subscribe((latest) => (this.latest = latest));
  }

  ngOnDestroy(): void {
    this.subscription.unsubscribe();
    this.financialsAccessSubscription.unsubscribe();
  }

  /** Re-reads the current machine's window. Ignored while a request is already in flight. */
  refresh(): void {
    if (this.loading || this.requestedMachineId === null) {
      return;
    }

    this.refresh$.next();
  }

  /** Whether there is a machine to read at all, which is what the Refresh action needs. */
  get hasMachine(): boolean {
    return this.requestedMachineId !== null;
  }

  /**
   * The honest empty state: the provider answered, and it reported no sale in its latest window.
   * Deliberately false for an unavailable answer, whose empty list means "nothing could be read".
   */
  get isEmpty(): boolean {
    return this.error === null
      && !this.loading
      && this.latest !== null
      && this.latest.isAvailable
      && this.latest.sales.length === 0;
  }

  /**
   * Whether an unavailable answer is a state of this business rather than something to repair. A
   * business with no sales provider (issue #759) runs inventory and purchasing perfectly well, so
   * that answer is presented as information, not as a fault.
   */
  isNoProvider(latest: MachineLatestSales): boolean {
    return !latest.isAvailable && latest.unavailableReason === 'NoSalesProvider';
  }

  /**
   * The amount cell's text. Three distinct answers, and none of them is `0`: the role may not see
   * the figure, the provider reported no figure, or this is the figure it reported.
   *
   * <p><strong>No currency denomination is invented.</strong> The documented `lastSales` contract
   * declares `CurrencyCode` as `string | null` and carries no currency symbol, so the figure is a
   * neutral number labelled with the code the provider reported - `9.50 EUR` for a euro sale - and
   * an unreported currency says so instead of being rendered as dollars. A `$` here would be this
   * application's guess at the denomination of somebody else's money, and the API deliberately
   * preserves a null `CurrencyCode` rather than assuming one.</p>
   */
  amountLabel(sale: MachineLatestSale): string {
    if (!this.canViewFinancials) {
      return 'Not available for your role';
    }

    if (sale.saleAmount === null || sale.saleAmount === undefined) {
      return 'Unavailable';
    }

    const amount = sale.saleAmount.toFixed(2);
    return sale.currencyCode ? `${amount} ${sale.currencyCode}` : `${amount} (currency not reported)`;
  }

  private request(machineId: number | null): Observable<MachineLatestSales | null> {
    if (machineId === null) {
      this.loading = false;
      this.error = null;
      return of(null);
    }

    this.loading = true;
    this.error = null;

    return this.machineService.getLatestSales(machineId).pipe(
      tap(() => (this.loading = false)),
      catchError(() => {
        this.loading = false;
        this.error =
          'The latest sales for this machine could not be loaded. This does not mean the machine '
          + 'has no sales - try Refresh.';
        return of(null);
      })
    );
  }
}
