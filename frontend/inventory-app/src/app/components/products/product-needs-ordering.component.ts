import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, Observable, Subject } from 'rxjs';
import { catchError, switchMap, take, takeUntil, tap } from 'rxjs/operators';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Category, ReorderMode, Supplier } from '../../models/models';
// `SupplierService` stays: this page's order dialog still needs the supplier list for its required
// supplier selector (issue #787, A1 of #786). What left is the product-supplier filter and column.
import { NayaxUnavailability, storageOnlyReorderLabel } from '../../provider/nayax-availability';
import { ListLoadState } from '../shared/list-load-state';
import { createFilterTrigger$ } from '../shared/filter-request-trigger';
import { SortDirection, SortValue, compareSortValues, nextSortState, sortAriaState, sortColumnLabel } from '../shared/sortable-column';
import { ToastService } from '../../services/toast.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { AccessService } from '../../access/access.service';
import { ProductPurchaseCostService } from '../../services/product-purchase-cost.service';
import { ProductPurchaseCostSummary } from '../../models/product-purchase-cost-summary';

// No `price` column since issue #792 (A2 of #786): a product carries no selling price, and no
// `status` column since issue #790 (D of #786): the list holds only active products, so a column
// of "Active" badges and a toggle for the one state that is listed say nothing. The reorder
// arithmetic - machine need, thresholds, on-order and need-to-order - is unchanged.
type NeedsOrderingSortColumn =
  | 'name'
  | 'category'
  | 'stock'
  | 'machineNeed'
  | 'lowStockThreshold'
  | 'restockTo'
  | 'onOrder'
  | 'needToOrder'
  | 'lastPurchaseCost'
  | 'cheapestPurchaseCost';

/**
 * What the two purchase-cost columns know (issue #790, D of #786), and the reason they are four
 * states rather than "a cost or nothing":
 *
 * - `idle` - nothing is being asked: before the first list arrives, and once a load was refused
 *   because the member's role does not grant `Dashboard.ViewFinancials`, in which case the columns
 *   are not rendered at all;
 * - `loading` - a request for the listed products is outstanding;
 * - `loaded` - a successful response established the costs, and a product **absent** from it has
 *   no recorded purchase history. This is the only state in which `—` may be shown;
 * - `unavailable` - the request failed. The columns say so and offer Retry, because "this product
 *   has never been bought" and "nobody could answer" are different facts and showing the second as
 *   the first is the mistake AGENTS.md § Frontend rules forbids.
 */
type PurchaseCostState = 'idle' | 'loading' | 'loaded' | 'unavailable';

@Component({
  selector: 'app-product-needs-ordering',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './product-needs-ordering.component.html'
})
export class ProductNeedsOrderingComponent implements OnInit, OnDestroy {
  products: Product[] = [];
  categories: Category[] = [];
  suppliers: Supplier[] = [];
  readonly loadState = new ListLoadState();
  search = '';
  categoryId: number | '' = '';
  selectedProductIds = new Set<number>();
  sortColumn: NeedsOrderingSortColumn | null = null;
  sortDirection: SortDirection = 'asc';
  orderQuantities: Record<number, number> = {};
  showOrderForm = false;
  savingOrder = false;
  orderForm = {
    supplierId: null as number | null,
    orderDate: new Date().toISOString().slice(0, 10),
    expectedDate: '',
    reference: '',
    notes: ''
  };

  /**
   * Whether a support session is active (issue #661): Mark as ordered is hidden and the status
   * toggle disabled while it is, because a session grants `InventoryOperate` - the capability this
   * page's two writes would otherwise be shown for - and the backend refuses them only by its
   * read-only method rule, never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  /**
   * Which facts the listed alerts were calculated from, and - in storage-only mode - why the
   * machines are missing from them, as `GET /api/products/alerts/low-stock` published them beside
   * the rows (issue #726, presented by issue #727). Nayax is an optional add-on, so a business with
   * no usable connection gets this page in full, calculated from its own storage data, and labelled
   * so a storage-only figure is never read as a machine-aware one.
   */
  reorderMode: ReorderMode | null = null;
  machineNeedUnavailableReason: NayaxUnavailability | null = null;

  /**
   * Whether the signed-in member's role grants `Dashboard.ViewFinancials`, which is what the two
   * purchase-cost columns and their one request need (issue #790).
   *
   * It starts `false` and not at the fail-open `true` other capability-gated figures use, because
   * here the capability decides whether a **request** is made at all: `GET
   * /api/products/purchase-cost-summary` answers `403` without it, and asking before the answer is
   * known would be asking for a refusal. `AccessService` already fails open where that matters - an
   * unreadable capability set answers `true` - so a member whose capabilities could not be read
   * still gets the columns.
   */
  canViewFinancials = false;

  /** Which of the four {@link PurchaseCostState} answers the cost columns currently hold. */
  purchaseCostState: PurchaseCostState = 'idle';

  private purchaseCosts = new Map<number, ProductPurchaseCostSummary>();

  /** The ids the displayed list is made of, which is what Retry asks about again. */
  private listedProductIds: readonly number[] = [];

  private readonly purchaseCostRequest$ = new Subject<readonly number[]>();
  private readonly search$ = new Subject<string>();
  private readonly immediateFilter$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private supplierService: SupplierService,
    private supplierOrderService: SupplierOrderService,
    private purchaseCostService: ProductPurchaseCostService,
    private toastService: ToastService,
    private router: Router,
    private access: AccessService,
    supportSession: SupportSessionService
  ) {
    supportSession.active$.pipe(takeUntil(this.destroy$)).subscribe((active) => (this.sessionActive = active));
    this.access
      .allows('Dashboard.ViewFinancials')
      .pipe(takeUntil(this.destroy$))
      .subscribe((allowed) => (this.canViewFinancials = allowed));
  }

  ngOnInit(): void {
    this.categoryService.getAll().subscribe((categories) => (this.categories = categories));
    this.supplierService.getAll().subscribe((suppliers) => (this.suppliers = suppliers));

    // `switchMap` is what makes a superseded cost answer unreachable: the request for the previous
    // product set is unsubscribed - and the HTTP request cancelled - the moment a newer one starts,
    // so neither its rows nor its failure can land on the list that replaced it.
    this.purchaseCostRequest$
      .pipe(
        switchMap((productIds) => this.requestPurchaseCosts(productIds)),
        takeUntil(this.destroy$)
      )
      .subscribe();

    createFilterTrigger$(this.search$, this.immediateFilter$)
      .pipe(
        switchMap(() => this.fetchProducts()),
        takeUntil(this.destroy$)
      )
      .subscribe();

    this.applyFilters();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  private fetchProducts() {
    const filters = {
      search: this.search || undefined,
      categoryId: this.categoryId === '' ? undefined : this.categoryId
    };

    const token = this.loadState.start();
    return this.productService.getLowStock(filters).pipe(
      tap((listing) => {
        if (this.loadState.isCurrent(token)) {
          // Active products only (issue #790). `GET /api/products/alerts/low-stock` already
          // excludes retired products server-side; this is the same defence the dashboard's
          // reorder-alert card applies, so a response that ever carried one cannot list it here.
          this.products = listing.products.filter((product) => product.isActive !== false);
          this.reorderMode = listing.mode;
          this.machineNeedUnavailableReason = listing.machineNeedUnavailableReason;
          this.beginPurchaseCostLoad(this.products.map((product) => product.id));
        }
        this.loadState.succeed(token);
      }),
      catchError(() => {
        this.loadState.fail(token);
        this.toastService.error('Failed to load products needing ordering.');
        return EMPTY;
      })
    );
  }

  /**
   * Starts the one bulk purchase-cost request for the products now on screen (issue #790).
   *
   * The previous list's costs are dropped **here**, synchronously with the product set they
   * belonged to, so no row of the new list can be rendered beside a cost paid for a different
   * product. No ids means no request: the endpoint reads "no ids" as "every product of the
   * business", which is not what an empty alert list is asking for - and an empty list has no row
   * left whose costs could be outstanding, so it settles as `loaded`.
   */
  private beginPurchaseCostLoad(productIds: readonly number[]): void {
    this.purchaseCosts = new Map();
    this.listedProductIds = productIds;
    this.purchaseCostState = productIds.length === 0 ? 'loaded' : 'loading';

    if (productIds.length > 0) {
      this.purchaseCostRequest$.next(productIds);
    }
  }

  /** Re-asks for the listed products' costs after a failure. */
  retryPurchaseCosts(): void {
    this.beginPurchaseCostLoad(this.listedProductIds);
  }

  private requestPurchaseCosts(productIds: readonly number[]): Observable<unknown> {
    return this.access.allows('Dashboard.ViewFinancials').pipe(
      take(1),
      switchMap((allowed) => {
        if (!allowed) {
          // No capability, no request: the endpoint refuses it, and the columns it would fill are
          // not rendered. The state goes back to `idle` so nothing claims to be loading.
          this.purchaseCostState = 'idle';
          return EMPTY;
        }

        return this.purchaseCostService.getSummaries(productIds).pipe(
          tap((summaries) => {
            this.purchaseCosts = new Map(summaries.map((summary) => [summary.productId, summary]));
            this.purchaseCostState = 'loaded';
          }),
          catchError(() => {
            this.purchaseCosts = new Map();
            this.purchaseCostState = 'unavailable';
            return EMPTY;
          })
        );
      })
    );
  }

  /**
   * The product's last and cheapest recorded purchase, or `undefined` when a successful response
   * held none for it - which is "never bought", and only ever read as that while the costs are
   * {@link PurchaseCostState} `loaded`.
   */
  purchaseCost(product: Product): ProductPurchaseCostSummary | undefined {
    return this.purchaseCostState === 'loaded' ? this.purchaseCosts.get(product.id) : undefined;
  }

  /**
   * Where the product name links: a Google search for it, opened in a new tab (issue #790). The
   * name is URL-encoded, so an `&`, a space or an accent in it searches for the name rather than
   * breaking the query.
   */
  googleSearchUrl(productName: string): string {
    return `https://www.google.com/search?q=${encodeURIComponent(productName)}`;
  }

  /**
   * The label a storage-only list carries, or `null` in machine-aware mode and for a listing that
   * published no mode at all (issue #727). It is shown for an empty list too: "nothing needs
   * ordering" means something different when the machines' current need was never read.
   */
  get storageOnlyNote(): string | null {
    return this.reorderMode === 'StorageOnly'
      ? storageOnlyReorderLabel(this.machineNeedUnavailableReason)
      : null;
  }

  /**
   * Whether the Machine need column may be shown. In storage-only mode it may not:
   * `machineReplenishmentNeed` is `0` there because nobody read the machines, and a column of
   * zeroes would state that the machines need nothing.
   */
  get isMachineNeedShown(): boolean {
    return this.reorderMode !== 'StorageOnly';
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
    this.applyFilters();
  }

  toggleSelection(productId: number, checked: boolean): void {
    if (checked) {
      this.selectedProductIds.add(productId);
    } else {
      this.selectedProductIds.delete(productId);
    }
  }

  openOrderForm(): void {
    // Checked here as well as hidden in the template, so the dialog is actually unavailable
    // rather than merely unreachable by mouse - the endpoint refuses the order either way.
    if (this.selectedProductIds.size === 0 || this.sessionActive) {
      return;
    }

    const selectedProducts = this.products.filter((product) => this.selectedProductIds.has(product.id));

    // The order starts with no supplier chosen (issue #787, A1 of #786). It used to be inferred
    // from the selected products' own supplier when they all agreed; a product carries none now,
    // and guessing was never the right default anyway - the supplier being ordered from is a
    // decision about this order. The selector stays required, so the order cannot be sent without
    // one.
    this.orderForm.supplierId = null;
    this.orderQuantities = Object.fromEntries(selectedProducts.map((product) => [product.id, product.needToOrder]));
    this.showOrderForm = true;
  }

  selectedProducts(): Product[] {
    return this.products.filter((product) => this.selectedProductIds.has(product.id));
  }

  saveOrder(): void {
    if (this.sessionActive) {
      return;
    }
    if (!this.orderForm.supplierId) {
      this.toastService.error('Select a supplier for this order.');
      return;
    }

    const lines = this.selectedProducts().map((product) => ({
      productId: product.id,
      quantityOrdered: this.orderQuantities[product.id]
    }));

    if (lines.some((line) => line.quantityOrdered <= 0)) {
      return;
    }

    this.savingOrder = true;
    this.supplierOrderService.create({
      supplierId: this.orderForm.supplierId,
      orderDate: this.orderForm.orderDate,
      expectedDate: this.orderForm.expectedDate || null,
      reference: this.orderForm.reference || null,
      notes: this.orderForm.notes || null,
      lines
    }).subscribe({
      next: () => {
        this.savingOrder = false;
        this.showOrderForm = false;
        this.selectedProductIds.clear();
        this.toastService.success('Supplier order created.');
        this.applyFilters();
      },
      error: () => {
        this.savingOrder = false;
        this.toastService.error('Failed to create supplier order.');
      }
    });
  }

  /**
   * Row click/Enter navigates to the product editor, unless it originated from a nested control
   * (the name's Google search link, the checkbox, Stock, Edit) or is a modified/non-primary click
   * the browser should handle. Since issue #790 the name anchor is a Google search for the product,
   * so deferring to it is what keeps one click from doing two things.
   *
   * This is a convenience over the row, never the only way to the editor: a keyboard cannot click a
   * row - the `<tr>` takes no focus and every focusable child is deferred to - so the row's own
   * `Edit` anchor is what reaches the editor from the keyboard.
   */
  onRowActivate(product: Product, rowEvent: Event): void {
    // Bound to (click) and (keydown.enter) only; Angular types both template $events as Event.
    const event = rowEvent as MouseEvent | KeyboardEvent;
    const ownControl = (event.target as Element | null)?.closest('a, button, input, select, textarea, label');
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
    return [...this.products].sort((left, right) => {
      const leftValue = this.sortValue(left, column);
      const rightValue = this.sortValue(right, column);
      // Missing values always sort last, regardless of direction; direction only
      // affects the order of the non-missing values and of ties among them.
      if (leftValue == null || rightValue == null) {
        if (leftValue == null && rightValue == null) {
          return left.id - right.id;
        }
        return leftValue == null ? 1 : -1;
      }

      const comparison = compareSortValues(leftValue, rightValue);
      // Ties keep a deterministic order (by id) instead of relying on sort stability alone.
      const resolved = comparison !== 0 ? comparison : left.id - right.id;
      return direction === 'desc' ? -resolved : resolved;
    });
  }

  sortBy(column: NeedsOrderingSortColumn): void {
    const next = nextSortState(this.sortColumn, this.sortDirection, column);
    this.sortColumn = next.column;
    this.sortDirection = next.direction;
  }

  ariaSort(column: NeedsOrderingSortColumn): 'ascending' | 'descending' | 'none' {
    return sortAriaState(this.sortColumn, this.sortDirection, column);
  }

  sortLabel(label: string, column: NeedsOrderingSortColumn): string {
    return sortColumnLabel(label, this.sortColumn, this.sortDirection, column);
  }

  private sortValue(product: Product, column: NeedsOrderingSortColumn): SortValue {
    switch (column) {
      case 'name':
        return product.name;
      case 'category':
        return product.category?.name ?? null;
      case 'stock':
        return product.quantityInStock;
      case 'machineNeed':
        return product.machineReplenishmentNeed;
      case 'lowStockThreshold':
        return product.lowStockThreshold;
      case 'restockTo':
        return product.restockTo;
      case 'onOrder':
        return product.onOrderQuantity;
      case 'needToOrder':
        return product.needToOrder;
      // A cost nobody has established yet - still loading, unavailable, or a product that has
      // never been bought - is missing, and the comparison above keeps a missing value last in
      // both directions. It is never sorted as a zero.
      case 'lastPurchaseCost':
        return this.purchaseCost(product)?.lastUnitCost ?? null;
      case 'cheapestPurchaseCost':
        return this.purchaseCost(product)?.lowestUnitCost ?? null;
    }
  }
}
