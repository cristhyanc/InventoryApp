import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';
import { EMPTY, Subject, merge, of } from 'rxjs';
import { catchError, switchMap, take, takeUntil, tap } from 'rxjs/operators';
import { AccessService } from '../../../access/access.service';
import { ProductPurchaseCostSummary } from '../../../models/product-purchase-cost-summary';
import { ProductPurchaseCostService } from '../../../services/product-purchase-cost.service';

/** The two purchase-cost figures a product screen shows side by side (issue #788, B of #786). */
export type ProductPurchaseCostColumn = 'lastPurchase' | 'cheapestPurchase';

/**
 * Where the bulk purchase-cost read for the current product set stands. Distinct from "no value",
 * because an in-flight or failed read says nothing about whether a product has purchase history,
 * and only a successful read may answer `—`.
 */
export type ProductPurchaseCostsState = 'loading' | 'loaded' | 'failed';

/**
 * What {@link ProductPurchaseCostsComponent} publishes about the product set it was given: the
 * state of its read, and the summaries that read found, keyed by product id. A product with no
 * entry has either not been asked about yet or has no recorded purchase history - which one is
 * told apart by `state`, never by the map alone.
 */
export interface ProductPurchaseCosts {
  readonly state: ProductPurchaseCostsState;
  readonly summaries: ReadonlyMap<number, ProductPurchaseCostSummary>;
}

/** What a page holds before the component has published anything: a read that has not answered yet. */
export const PURCHASE_COSTS_NOT_READ_YET: ProductPurchaseCosts = { state: 'loading', summaries: new Map() };

export const PURCHASE_COSTS_FAILED_MESSAGE = 'The Last and Cheapest purchase cost columns could not be loaded.';

function summaryOf(costs: ProductPurchaseCosts, productId: number): ProductPurchaseCostSummary | undefined {
  return costs.state === 'loaded' ? costs.summaries.get(productId) : undefined;
}

/**
 * A product's Last/Cheapest purchase cost cell text: `Loading` while the read for the current
 * product set is in flight, `Unavailable` once it has failed, `—` once it has succeeded and found
 * no recorded purchase for this product, and the cost itself - a genuine `0` included - once it has
 * found one. The order matters: only a successful response may answer `—`.
 */
export function purchaseCostDisplay(costs: ProductPurchaseCosts, productId: number, column: ProductPurchaseCostColumn): string {
  if (costs.state === 'loading') {
    return 'Loading';
  }
  if (costs.state === 'failed') {
    return 'Unavailable';
  }

  const summary = costs.summaries.get(productId);
  if (!summary) {
    return '—';
  }

  const cost = column === 'lastPurchase' ? summary.lastUnitCost : summary.lowestUnitCost;
  return `$${cost.toFixed(2)}`;
}

/** The supplier beside a found Last/Cheapest cost (`None` when the purchase recorded none), or `null` while there is no cost to show one beside. */
export function purchaseCostSupplierDisplay(
  costs: ProductPurchaseCosts,
  productId: number,
  column: ProductPurchaseCostColumn
): string | null {
  const summary = summaryOf(costs, productId);
  if (!summary) {
    return null;
  }

  const supplierName = column === 'lastPurchase' ? summary.lastSupplierName : summary.lowestSupplierName;
  return supplierName ?? 'None';
}

/** The cost a Last/Cheapest purchase column sorts on - never its supplier - or `null` when there is none to sort on. */
export function purchaseCostSortValue(
  costs: ProductPurchaseCosts,
  productId: number,
  column: ProductPurchaseCostColumn
): number | null {
  const summary = summaryOf(costs, productId);
  if (!summary) {
    return null;
  }

  return column === 'lastPurchase' ? summary.lastUnitCost : summary.lowestUnitCost;
}

/**
 * The bulk purchase-cost read behind the Last and Cheapest purchase columns of the product screens
 * (issue #791, E of #786), as a dedicated feature component rather than a second workflow grown
 * inside a routed page - see `docs/architecture.md` § Page composition boundary (issue #191). It
 * owns the whole lifecycle of that read: when it starts, the capability that permits it, its
 * loading/loaded/failed state, and its own failure message with the Retry control that re-enters
 * it. The page supplies only the product ids it is displaying (`productIds`) and lays the published
 * answer (`costsChange`) out in its own table cells through the display helpers in this file, so
 * the rule that "only a successful response may answer `—`" is stated once, here.
 *
 * **The capability is read fresh for every request**, independently of whatever continuous
 * `AccessService.allows('Dashboard.ViewFinancials')` subscription the page keeps for hiding the
 * columns: the endpoint answers `403` without it, so no request may be issued on a capability
 * answer that was read before this product set existed - including during the fail-open window
 * before a page's first capability read settles.
 */
@Component({
  selector: 'app-product-purchase-costs',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './product-purchase-costs.component.html'
})
export class ProductPurchaseCostsComponent implements OnChanges, OnDestroy {
  /**
   * The product ids currently displayed. A new set - a new search, category, status filter or Reset
   * on the page - drops the previous set's results immediately and starts a fresh read.
   */
  @Input() productIds: readonly number[] = [];

  /** The state and summaries of the current read, published on every change to either. */
  @Output() readonly costsChange = new EventEmitter<ProductPurchaseCosts>();

  readonly failedMessage = PURCHASE_COSTS_FAILED_MESSAGE;

  state: ProductPurchaseCostsState = PURCHASE_COSTS_NOT_READ_YET.state;

  private readonly productIds$ = new Subject<readonly number[]>();
  private readonly retry$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();

  constructor(
    private readonly purchaseCostService: ProductPurchaseCostService,
    private readonly access: AccessService
  ) {
    // Subscribed here rather than in ngOnInit because ngOnChanges delivers the first product set
    // before ngOnInit runs. The outer switchMap cancels an in-flight read - and any pending Retry
    // on it - the moment a newer product set arrives, so a slow or failed response can never land
    // under a set it no longer describes.
    this.productIds$
      .pipe(
        switchMap((productIds) => this.load(productIds)),
        takeUntil(this.destroy$)
      )
      .subscribe();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productIds']) {
      this.productIds$.next(this.productIds ?? []);
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  /** Re-enters the read for the product ids currently displayed, after a failure. */
  retry(): void {
    this.retry$.next();
  }

  private load(productIds: readonly number[]) {
    if (productIds.length === 0) {
      this.publish('loaded', new Map());
      return EMPTY;
    }

    this.publish('loading', new Map());
    return this.access.allows('Dashboard.ViewFinancials').pipe(
      take(1),
      switchMap((allowed) => {
        if (!allowed) {
          this.publish('loaded', new Map());
          return EMPTY;
        }

        return merge(of(undefined), this.retry$).pipe(switchMap(() => this.request(productIds)));
      })
    );
  }

  private request(productIds: readonly number[]) {
    this.publish('loading', new Map());
    return this.purchaseCostService.getSummaries(productIds).pipe(
      tap((summaries) => this.publish('loaded', new Map(summaries.map((summary) => [summary.productId, summary])))),
      catchError(() => {
        this.publish('failed', new Map());
        return EMPTY;
      })
    );
  }

  private publish(state: ProductPurchaseCostsState, summaries: ReadonlyMap<number, ProductPurchaseCostSummary>): void {
    this.state = state;
    this.costsChange.emit({ state, summaries });
  }
}
