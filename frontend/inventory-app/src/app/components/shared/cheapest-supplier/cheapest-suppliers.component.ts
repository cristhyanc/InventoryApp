import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';
import { EMPTY, Subject, merge, of } from 'rxjs';
import { catchError, switchMap, take, takeUntil, tap } from 'rxjs/operators';
import { planRequiredOfError } from '../../../access/plan-feature';
import {
  CheapestSupplierProduct,
  CheapestSupplierSummary,
  SupplierPriceBasis
} from '../../../models/cheapest-supplier';
import { CheapestSupplierService } from '../../../services/cheapest-supplier.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * Where the cheapest-supplier read for the current product set stands.
 *
 * - `loading` - a request for these products is outstanding, or none has been made yet;
 * - `loaded` - a successful answer established the offers, and a product **absent** from it is one
 *   the business does not track. Only this state may be read as a statement about a product;
 * - `failed` - the request failed. "Nobody could answer" is not "this product has no cheapest
 *   supplier", and showing the second as the first is what AGENTS.md § Frontend rules forbids;
 * - `unavailable` - the column cannot be read at all in this context. Today that is a read-only
 *   support session, which refuses every non-`GET` request (issue #551) and so would answer `403`
 *   to a Retry for ever;
 * - `planLocked` - the business's plan does not include observed supplier prices (issue #967's
 *   `PlanFeature.ObservedSupplierPrices`, read since issue #979). Like `unavailable` this offers no
 *   Retry: a `403 plan_required` is not a transient failure either, and retrying asks the same
 *   question the business is already told the answer to.
 */
export type CheapestSuppliersState = 'loading' | 'loaded' | 'failed' | 'unavailable' | 'planLocked';

/**
 * What {@link CheapestSuppliersComponent} publishes about the product set it was given: the state
 * of its read, the GST basis the server compared on, and the answers keyed by product id.
 */
export interface CheapestSuppliers {
  readonly state: CheapestSuppliersState;
  readonly displayBasis: SupplierPriceBasis | null;
  readonly byProduct: ReadonlyMap<number, CheapestSupplierProduct>;
}

/** What a page holds before the component has published anything: a read that has not answered yet. */
export const CHEAPEST_SUPPLIERS_NOT_READ_YET: CheapestSuppliers = {
  state: 'loading',
  displayBasis: null,
  byProduct: new Map()
};

export const CHEAPEST_SUPPLIERS_FAILED_MESSAGE = 'The Cheapest supplier column could not be loaded.';

export const CHEAPEST_SUPPLIERS_SUPPORT_SESSION_MESSAGE =
  'Supplier prices are not available while viewing a business in support view.';

/** The inline plan lock's own sentence (issue #979), naming the plan the refusal actually named. */
export function cheapestSuppliersPlanLockedMessage(requiredPlan: string): string {
  return `The Cheapest supplier column needs the ${requiredPlan} plan.`;
}

/** The answer for one product, or `undefined` while the read has not established one. */
export function cheapestSupplierFor(
  suppliers: CheapestSuppliers,
  productId: number
): CheapestSupplierProduct | undefined {
  return suppliers.state === 'loaded' ? suppliers.byProduct.get(productId) : undefined;
}

/**
 * The one cheapest-supplier read behind the Cheapest supplier column of the product screens (issue
 * #930, parent #817), as a dedicated feature component rather than a second workflow grown inside a
 * routed page - see `docs/architecture.md` § Page composition boundary (issue #191). It owns the
 * whole lifecycle of that read: when it starts, how a page's rows are split into requests, its
 * loading/loaded/failed state, and its own failure message with the Retry control that re-enters
 * it. The page supplies the product ids it is displaying and lays the published answer out in its
 * own table cells through {@link CheapestSupplierCellComponent}.
 *
 * **One request per page of rows**, split into batches of at most `CHEAPEST_SUPPLIER_MAX_IDS` by
 * the service, because that is the endpoint's own bound. A new product set - a new search, filter
 * or Reset - drops the previous answers immediately and starts a fresh read, so no row can be
 * rendered beside a price that belongs to a different product set.
 */
@Component({
  selector: 'app-cheapest-suppliers',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    @if (state === 'failed') {
      <div class="flex flex-wrap items-center gap-3 border-b border-md-gray-200 p-4" role="alert">
        <p class="text-md-body value-negative">{{ failedMessage }}</p>
        <button type="button" class="btn btn-sm btn-secondary" (click)="retry()">Retry</button>
      </div>
    } @else if (state === 'unavailable') {
      <p class="border-b border-md-gray-200 p-4 text-md-body value-muted">{{ supportSessionMessage }}</p>
    } @else if (state === 'planLocked') {
      <div class="flex flex-wrap items-center gap-3 border-b border-md-gray-200 p-4" role="status">
        <p class="text-md-body value-muted">{{ planLockedMessage }}</p>
        <a routerLink="/plan" class="btn-link">View the Plan page</a>
      </div>
    }
  `
})
export class CheapestSuppliersComponent implements OnChanges, OnDestroy {
  /** The product ids currently displayed. A new set drops the previous answers and re-reads. */
  @Input() productIds: readonly number[] = [];

  /** The state and answers of the current read, published on every change to either. */
  @Output() readonly suppliersChange = new EventEmitter<CheapestSuppliers>();

  readonly failedMessage = CHEAPEST_SUPPLIERS_FAILED_MESSAGE;
  readonly supportSessionMessage = CHEAPEST_SUPPLIERS_SUPPORT_SESSION_MESSAGE;

  state: CheapestSuppliersState = CHEAPEST_SUPPLIERS_NOT_READ_YET.state;

  /** The plan a `planLocked` state's own refusal named, for {@link planLockedMessage}. */
  requiredPlan: string | null = null;

  get planLockedMessage(): string {
    return cheapestSuppliersPlanLockedMessage(this.requiredPlan ?? 'Starter');
  }

  private readonly productIds$ = new Subject<readonly number[]>();
  private readonly retry$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();

  constructor(
    private readonly cheapestSuppliers: CheapestSupplierService,
    private readonly supportSession: SupportSessionService
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
      // No rows, no request - and settled rather than loading, because there is no cell left whose
      // answer could still be outstanding.
      this.publish('loaded', null, new Map());
      return EMPTY;
    }

    this.publish('loading', null, new Map());

    return this.supportSession.active$.pipe(
      take(1),
      switchMap((sessionActive) => {
        if (sessionActive) {
          // The endpoint is a POST and a support session refuses every non-GET request (issue
          // #551), so asking would spend a request on a refusal that can never succeed. The state
          // says the column is unavailable here, which is the truth, and offers no Retry.
          this.publish('unavailable', null, new Map());
          return EMPTY;
        }

        return merge(of(undefined), this.retry$).pipe(switchMap(() => this.request(productIds)));
      })
    );
  }

  private request(productIds: readonly number[]) {
    this.publish('loading', null, new Map());

    return this.cheapestSuppliers.getSummary(productIds).pipe(
      tap((summary) => this.publish('loaded', summary.displayBasis, index(summary))),
      catchError((error: unknown) => {
        // A `403 plan_required` is this business's own answer, not a transient failure, and
        // offering a Retry for it would ask the same question the response already answered
        // (issue #967's `ObservedSupplierPrices` feature, read since issue #979).
        const requiredPlan = planRequiredOfError(error);
        this.requiredPlan = requiredPlan;
        this.publish(requiredPlan === null ? 'failed' : 'planLocked', null, new Map());
        return EMPTY;
      })
    );
  }

  private publish(
    state: CheapestSuppliersState,
    displayBasis: SupplierPriceBasis | null,
    byProduct: ReadonlyMap<number, CheapestSupplierProduct>
  ): void {
    this.state = state;
    this.suppliersChange.emit({ state, displayBasis, byProduct });
  }
}

function index(summary: CheapestSupplierSummary): ReadonlyMap<number, CheapestSupplierProduct> {
  return new Map(summary.products.map((product) => [product.productId, product]));
}
