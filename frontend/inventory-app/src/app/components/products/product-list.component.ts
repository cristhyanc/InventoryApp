import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, Subject, Subscription, merge, of } from 'rxjs';
import { catchError, switchMap, take, takeUntil, tap } from 'rxjs/operators';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { Product, Category } from '../../models/models';
import { ConfirmationDialogComponent } from '../shared/confirmation-dialog.component';
import { ListLoadState } from '../shared/list-load-state';
import { createFilterTrigger$ } from '../shared/filter-request-trigger';
import {
  SortDirection,
  SortValue,
  compareSortValuesDirectionalNulls,
  nextSortState,
  sortAriaState,
  sortColumnLabel
} from '../shared/sortable-column';
import { ToastService } from '../../services/toast.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { apiErrorMessage } from '../shared/api-error-message';
import { AccessService } from '../../access/access.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { ProductPurchaseCostSummary } from '../../models/product-purchase-cost-summary';

// No 'supplier' column since issue #787 (A1 of #786): a product carries no supplier to sort by.
// No `price` column since issue #792 (A2 of #786): a product carries no selling price, so there is
// nothing to show or sort by. `averageCost`, `lastPurchase` and `cheapestPurchase` were added by
// issue #791 (E of #786); `stock` and `status` are unchanged.
type ProductSortColumn =
  | 'name'
  | 'category'
  | 'stock'
  | 'status'
  | 'averageCost'
  | 'lastPurchase'
  | 'cheapestPurchase';

/** `GET api/products`'s working-catalogue/whole-catalogue choice (issue #737), as a page filter. */
type ProductStatusFilter = 'active' | 'all';

/**
 * The state of the bulk purchase-cost read behind the Last/Cheapest purchase columns (issue #791,
 * E of #786): distinct from "no value", because an in-flight or failed read says nothing about
 * whether a product has purchase history, and only a successful read may answer `—`.
 */
type PurchaseCostsState = 'loading' | 'loaded' | 'failed';

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, ConfirmationDialogComponent],
  templateUrl: './product-list.component.html'
})
export class ProductListComponent implements OnInit, OnDestroy {
  products: Product[] = [];
  categories: Category[] = [];
  readonly loadState = new ListLoadState();
  confirmingProduct: Product | null = null;
  search = '';
  categoryId: number | '' = '';
  /**
   * The working-catalogue/whole-catalogue choice (issue #791, E of #786, replacing the "Show
   * retired" checkbox): "Only active" (the default) calls `GET /api/products` with no
   * `includeRetired`, which is what keeps the page's default view the working catalogue; "All"
   * sends `includeRetired=true`, which is how a just-deactivated product stays reachable to
   * Reactivate - the toggle flips `isActive` either way, but the row has to still be on screen to
   * click it.
   */
  statusFilter: ProductStatusFilter = 'active';
  sortColumn: ProductSortColumn | null = null;
  sortDirection: SortDirection = 'asc';

  /**
   * Whether a support session is active (issue #554): Delete is hidden and the Active/Inactive
   * toggle is disabled while it is, because a support session grants `InventoryOperate` - the
   * capability this page would otherwise gate those on - and refuses the write itself only by its
   * read-only method rule, never by withholding the capability. See
   * `SupportSessionService.active$`.
   */
  sessionActive = false;

  /**
   * Whether the signed-in member's role grants `Dashboard.ViewFinancials` (issue #791, E of #786):
   * the Last/Cheapest purchase columns and their request are hidden without it, on the same
   * fail-open default every other capability-gated control in this codebase starts from while the
   * capability has not been read yet. Average cost is unaffected and always shown.
   */
  canViewFinancials = true;

  /** The state of the current purchase-cost read; see {@link PurchaseCostsState}. */
  costsState: PurchaseCostsState = 'loading';

  /**
   * The current product set's purchase-cost summaries, keyed by product id. A product with no
   * entry has either not been asked about yet or has no recorded purchase history - which one is
   * told apart by {@link costsState}, never by this map alone.
   */
  purchaseCosts = new Map<number, ProductPurchaseCostSummary>();

  private readonly search$ = new Subject<string>();
  private readonly immediateFilter$ = new Subject<void>();
  private readonly productIdsChanged$ = new Subject<readonly number[]>();
  private readonly retryCosts$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();
  private readonly sessionActiveSubscription: Subscription;
  private readonly financialsAccessSubscription: Subscription;

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private purchaseCostService: ProductPurchaseCostService,
    private toastService: ToastService,
    private router: Router,
    supportSession: SupportSessionService,
    private access: AccessService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    this.financialsAccessSubscription = access
      .allows('Dashboard.ViewFinancials')
      .subscribe((allowed) => (this.canViewFinancials = allowed));
  }

  ngOnInit(): void {
    // No supplier list is loaded (issue #787, A1 of #786): this page has no supplier filter or
    // column left, so there is nothing for one to feed.
    this.categoryService.getAll().subscribe((categories) => (this.categories = categories));

    createFilterTrigger$(this.search$, this.immediateFilter$)
      .pipe(
        switchMap(() => this.fetchProducts()),
        takeUntil(this.destroy$)
      )
      .subscribe();

    // The displayed product set changes only once a new fetch actually succeeds (a failed refetch
    // leaves the previous rows on screen), which is the one moment stale purchase-cost results must
    // be dropped. The outer switchMap cancels an in-flight read - and any pending Retry on it - the
    // moment a newer product set arrives, so a slow or failed response can never land under a set
    // it no longer describes.
    this.productIdsChanged$
      .pipe(
        switchMap((productIds) => this.loadPurchaseCosts(productIds)),
        takeUntil(this.destroy$)
      )
      .subscribe();

    this.applyFilters();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
    this.sessionActiveSubscription.unsubscribe();
    this.financialsAccessSubscription.unsubscribe();
  }

  private fetchProducts() {
    const filters = {
      search: this.search || undefined,
      categoryId: this.categoryId === '' ? undefined : this.categoryId,
      includeRetired: this.statusFilter === 'all'
    };

    const token = this.loadState.start();
    return this.productService.getAll(filters).pipe(
      tap((products) => {
        if (this.loadState.isCurrent(token)) {
          this.products = products;
          this.productIdsChanged$.next(products.map((product) => product.id));
        }
        this.loadState.succeed(token);
      }),
      catchError(() => {
        this.loadState.fail(token);
        this.toastService.error('Failed to load products.');
        return EMPTY;
      })
    );
  }

  /**
   * Starts (or restarts) the purchase-cost read for the given product ids: the old results are
   * cleared immediately, because they describe a product set that is no longer on screen, and no
   * request is made at all without `Dashboard.ViewFinancials` - asked for fresh every time, so a
   * capability change since the last read is honoured rather than cached past it. Retry re-enters
   * the same read for the same ids.
   */
  private loadPurchaseCosts(productIds: readonly number[]) {
    this.purchaseCosts = new Map();

    if (productIds.length === 0) {
      this.costsState = 'loaded';
      return EMPTY;
    }

    this.costsState = 'loading';
    return this.access.allows('Dashboard.ViewFinancials').pipe(
      take(1),
      switchMap((allowed) => {
        if (!allowed) {
          this.costsState = 'loaded';
          return EMPTY;
        }

        return merge(of(undefined), this.retryCosts$).pipe(switchMap(() => this.requestPurchaseCosts(productIds)));
      })
    );
  }

  private requestPurchaseCosts(productIds: readonly number[]) {
    this.costsState = 'loading';
    return this.purchaseCostService.getSummaries(productIds).pipe(
      tap((summaries) => {
        this.purchaseCosts = new Map(summaries.map((summary) => [summary.productId, summary]));
        this.costsState = 'loaded';
      }),
      catchError(() => {
        this.costsState = 'failed';
        return EMPTY;
      })
    );
  }

  /** Re-enters the purchase-cost read for the currently displayed products, after a failure. */
  retryPurchaseCosts(): void {
    this.retryCosts$.next();
  }

  onSearchChange(value: string): void {
    this.search = value;
    this.search$.next(value);
  }

  /** Triggers an immediate (non-debounced) refresh, e.g. for a category change. */
  applyFilters(): void {
    this.immediateFilter$.next();
  }

  resetFilters(): void {
    this.search = '';
    this.categoryId = '';
    this.statusFilter = 'active';
    this.applyFilters();
  }

  /**
   * Whether the API says this product may be deleted (issue #737). `canDelete` is the deletion
   * rule's own answer, published by the catalogue listing from the same
   * `Inventory.Domain.Products.ProductDeletionPolicy` that refuses the delete itself - a provider
   * link, stock movements, purchase lines, supplier order lines, sales, machine stock counts and
   * costing records all reach it. Delete is offered only on a plain `true`: an absent answer (a
   * response that never resolved the question) is not permission, and the row then offers the
   * retirement path, which is always available.
   *
   * The answer stays advisory - it is as fresh as the last fetch - and the server decides again
   * inside the delete's own transaction, which `deleteConfirmed` still surfaces.
   */
  canDelete(product: Product): boolean {
    return product.canDelete === true;
  }

  /**
   * Why Delete is not offered, and which control retires the product instead: the server's own
   * refusal (so the page never words the rule a second time), followed by the name of the control
   * on this row that does what the refusal points at.
   */
  deleteRefusalNote(product: Product): string {
    const retirement = product.isActive
      ? 'Use the Status toggle to deactivate it.'
      : 'It is deactivated; use the Status toggle to reactivate it.';
    const refusal = product.deletionRefusal?.trim();
    return refusal ? `${refusal} ${retirement}` : `This product cannot be deleted. ${retirement}`;
  }

  confirmDelete(product: Product): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive || !this.canDelete(product)) {
      return;
    }
    this.confirmingProduct = product;
  }

  cancelDelete(): void {
    this.confirmingProduct = null;
  }

  deleteConfirmed(): void {
    if (!this.confirmingProduct) {
      return;
    }

    const product = this.confirmingProduct;
    this.confirmingProduct = null;
    this.productService.delete(product.id).subscribe({
      next: () => this.applyFilters(),
      // Delete is offered only where `canDelete` said the API allows it, but that answer was read
      // before this click: a product can gain a stock movement, a sale or a provider link in
      // between, and the server decides again inside the delete's own transaction. Its refusal is
      // shown verbatim - it names what is in the way and points at the Active/Inactive toggle
      // beside it (`ProductDeletionPolicy.Refusal`) - and the row stays as it was fetched, so the
      // toggle it points at is still there to use.
      error: (error) => this.toastService.error(apiErrorMessage(error, 'Failed to delete product.'))
    });
  }

  /**
   * Row click/Enter navigates to the product editor, unless it originated from a nested control
   * (the name link, Stock, Delete, Status) or is a modified/non-primary click the browser should handle.
   * The name cell's routerLink anchor is the real link: it gives keyboard focus, open-in-new-tab and copy-link.
   */
  onRowActivate(product: Product, rowEvent: Event): void {
    // Bound to (click) and (keydown.enter) only; Angular types both template $events as Event.
    const event = rowEvent as MouseEvent | KeyboardEvent;
    const ownControl = (event.target as Element | null)?.closest('a, button');
    const browserHandlesIt =
      event.defaultPrevented ||
      (event instanceof MouseEvent && event.button !== 0) ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey ||
      event.altKey;
    if (ownControl || browserHandlesIt) {
      return;
    }

    this.router.navigate(['/products', product.id, 'edit']);
  }

  get sortedProducts(): Product[] {
    const column = this.sortColumn;
    if (!column) {
      return this.products;
    }

    const direction = this.sortDirection;
    const missingAlwaysLast = ProductListComponent.sortsMissingValueLast(column);
    return [...this.products].sort((left, right) => {
      const leftValue = this.sortValue(left, column);
      const rightValue = this.sortValue(right, column);

      // The cost columns (issue #791, E of #786) sort a missing/unavailable value last whichever
      // way the column is toggled, unlike Category's directional null policy below: this decision
      // is made before - and is never multiplied by - the direction flip.
      if (missingAlwaysLast && (leftValue == null || rightValue == null)) {
        if (leftValue == null && rightValue == null) {
          return left.id - right.id;
        }
        return leftValue == null ? 1 : -1;
      }

      const comparison = compareSortValuesDirectionalNulls(leftValue, rightValue);
      // Ties keep a deterministic order (by id) instead of relying on sort stability alone.
      const resolved = comparison !== 0 ? comparison : left.id - right.id;
      return direction === 'desc' ? -resolved : resolved;
    });
  }

  private static sortsMissingValueLast(column: ProductSortColumn): boolean {
    return column === 'averageCost' || column === 'lastPurchase' || column === 'cheapestPurchase';
  }

  sortBy(column: ProductSortColumn): void {
    const next = nextSortState(this.sortColumn, this.sortDirection, column);
    this.sortColumn = next.column;
    this.sortDirection = next.direction;
  }

  ariaSort(column: ProductSortColumn): 'ascending' | 'descending' | 'none' {
    return sortAriaState(this.sortColumn, this.sortDirection, column);
  }

  sortLabel(label: string, column: ProductSortColumn): string {
    return sortColumnLabel(label, this.sortColumn, this.sortDirection, column);
  }

  private sortValue(product: Product, column: ProductSortColumn): SortValue {
    switch (column) {
      case 'name':
        return product.name;
      case 'category':
        return product.category?.name ?? null;
      case 'stock':
        return product.quantityInStock;
      case 'status':
        return product.isActive ? 1 : 0;
      case 'averageCost':
        return product.averageUnitCostForDisplay ?? null;
      case 'lastPurchase':
        return this.purchaseCosts.get(product.id)?.lastUnitCost ?? null;
      case 'cheapestPurchase':
        return this.purchaseCosts.get(product.id)?.lowestUnitCost ?? null;
    }
  }

  /** `—` for an unestablished average cost (never `0`); a genuine `0` renders as `$0.00`. */
  averageCostDisplay(product: Product): string {
    const value = product.averageUnitCostForDisplay;
    return value == null ? '—' : this.money(value);
  }

  /**
   * The Last/Cheapest purchase cost cell's text: `Loading` while the bulk read for the current
   * product set is in flight, `Unavailable` once it has failed, `—` once it has succeeded and
   * found no recorded purchase for this product, and the cost itself - a genuine `0` included -
   * once it has found one. The order matters: only a successful response may answer `—`.
   */
  purchaseCostDisplay(product: Product, column: 'lastPurchase' | 'cheapestPurchase'): string {
    if (this.costsState === 'loading') {
      return 'Loading';
    }
    if (this.costsState === 'failed') {
      return 'Unavailable';
    }

    const summary = this.purchaseCosts.get(product.id);
    if (!summary) {
      return '—';
    }

    return this.money(column === 'lastPurchase' ? summary.lastUnitCost : summary.lowestUnitCost);
  }

  /** The supplier beside a found Last/Cheapest cost, or `null` while there is no cost to show one beside. */
  purchaseCostSupplierDisplay(product: Product, column: 'lastPurchase' | 'cheapestPurchase'): string | null {
    const summary = this.purchaseCosts.get(product.id);
    if (this.costsState !== 'loaded' || !summary) {
      return null;
    }

    const supplierName = column === 'lastPurchase' ? summary.lastSupplierName : summary.lowestSupplierName;
    return supplierName ?? 'None';
  }

  private money(value: number): string {
    return `$${value.toFixed(2)}`;
  }

  toggleActive(product: Product): void {
    if (this.sessionActive) {
      return;
    }
    this.productService.update(product.id, {
      name: product.name,
      sku: product.sku ?? null,
      description: product.description ?? null,
      lowStockThreshold: product.lowStockThreshold,
      restockTo: product.restockTo,
      unit: product.unit ?? null,
      categoryId: product.categoryId ?? null,
      isActive: !product.isActive
    }).subscribe({
      next: () => this.applyFilters(),
      error: () => this.toastService.error('Failed to update product status.')
    });
  }
}
