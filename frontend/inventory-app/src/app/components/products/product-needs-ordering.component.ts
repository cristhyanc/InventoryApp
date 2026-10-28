import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
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
import {
  CHEAPEST_SUPPLIERS_NOT_READ_YET,
  CheapestSuppliers,
  CheapestSuppliersComponent,
  cheapestSupplierFor
} from '../shared/cheapest-supplier/cheapest-suppliers.component';
import { CheapestSupplierCellComponent } from '../shared/cheapest-supplier/cheapest-supplier-cell.component';
import { CheapestSupplierProduct } from '../../models/cheapest-supplier';

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
  imports: [CommonModule, FormsModule, RouterLink, CheapestSuppliersComponent, CheapestSupplierCellComponent],
  templateUrl: './product-needs-ordering.component.html',
  styles: [
    `
      /*
       * The "Mark products as ordered" panel's own max-height (issue #906), set here rather than
       * with a Tailwind arbitrary-value max-height utility because the dvh-then-vh-fallback trick
       * needs both declarations to land on the same property in this exact order, which only a
       * literal CSS rule guarantees - a build tool is free to reorder utility classes. Matches
       * \`machine-restock-sync\`'s \`.sync-modal\` (issue #183).
       */
      .needs-order-dialog-panel {
        max-height: calc(100vh - 2rem);
        max-height: calc(100dvh - 2rem);
      }
    `
  ]
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
   * Products the Add product search put on the order being built that the Needs ordering list does
   * not hold at all (issue #907) - that is the whole point of the search. A match the page *does*
   * list joins the order through `selectedProductIds` instead, so the page's own checkbox for it is
   * ticked; see {@link addProduct}. See {@link orderLines}, which is what the dialog actually shows.
   */
  addedProducts: Product[] = [];

  /** The text currently in the dialog's Add product search box. */
  addProductQuery = '';

  /** The active search's own matches, before {@link addableProducts} removes what is already a line. */
  addProductResults: Product[] = [];

  /** Whether {@link addProductResults} is a real answer for the text currently in the box. */
  addProductSearchState: 'idle' | 'loading' | 'loaded' | 'failed' = 'idle';

  private readonly addProductQuery$ = new Subject<string>();

  /**
   * The immediate channel of the Add product search's {@link createFilterTrigger$} trigger: it
   * fires when the box is cleared other than by typing - a freshly opened dialog, and an add that
   * consumed the query - which is what makes the helper forget the term it last applied, so
   * searching the same text again still asks. See {@link resetAddProductSearch}.
   */
  private readonly addProductSearchReset$ = new Subject<void>();

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

  /**
   * The ids the displayed list is made of, which is what Retry asks about again - and what the
   * Cheapest supplier column's own read is given (issue #930). It is a fresh array on every load,
   * so that component's `ngOnChanges` sees a new product set rather than a mutated one.
   */
  listedProductIds: readonly number[] = [];

  /**
   * Where the Cheapest supplier column's one read stands, as
   * {@link CheapestSuppliersComponent} publishes it (issue #930). The page lays the answers out in
   * its own cells and decides nothing about them: the component owns the request, the batching,
   * the failure message and Retry.
   */
  cheapestSuppliers: CheapestSuppliers = CHEAPEST_SUPPLIERS_NOT_READ_YET;

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

    // The Add product search follows the same convention as the page's own search above: one
    // request per *settled* term, through the shared `createFilterTrigger$`/`SEARCH_DEBOUNCE_MS`
    // helper, rather than a second, differently-behaved search stream in the same component. So a
    // term still being typed spends no request, an unchanged term is not asked twice in a row, and
    // `switchMap` keeps a superseded answer unreachable the same way the purchase-cost request
    // above does - only the latest settled text's answer can land in `addProductResults`. The
    // trigger's emitted value is ignored for the same reason `fetchProducts` ignores it: the box's
    // current text is on the component, and the reset channel emits no term at all.
    createFilterTrigger$(this.addProductQuery$, this.addProductSearchReset$)
      .pipe(
        switchMap(() => this.searchAddableProducts(this.addProductQuery)),
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
   * This product's cheapest-supplier answer, or `undefined` while the read has not established one
   * (issue #930). The cell tells the states apart from the read's own state, never from this being
   * absent.
   */
  cheapestSupplier(product: Product): CheapestSupplierProduct | undefined {
    return cheapestSupplierFor(this.cheapestSuppliers, product.id);
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
    this.addedProducts = [];
    this.resetAddProductSearch();
    this.showOrderForm = true;
  }

  selectedProducts(): Product[] {
    return this.products.filter((product) => this.selectedProductIds.has(product.id));
  }

  /**
   * Every row the dialog shows (issue #907): the Needs ordering rows ticked when it was opened,
   * plus any product the Add product search added since. {@link selectedProducts} alone no longer
   * describes the dialog, because an added product is never required to be one of `this.products` -
   * that is the point of searching the wider catalogue for it - so it is left exactly as it reads
   * today and this is what the template and {@link saveOrder} use instead.
   */
  orderLines(): Product[] {
    return [...this.selectedProducts(), ...this.addedProducts];
  }

  /**
   * The Add product search's own matches, minus whatever is already a line in the order being
   * built - a product already on it is never offered a second time.
   */
  get addableProducts(): Product[] {
    const alreadyInOrder = new Set(this.orderLines().map((product) => product.id));
    return this.addProductResults.filter((product) => !alreadyInOrder.has(product.id));
  }

  onAddProductQueryChange(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.addProductQuery = value;
    this.addProductQuery$.next(value);
  }

  /**
   * Searches the catalogue for a product to add to the order being built (issue #907). `getAll`
   * without `includeRetired` already answers active products only; the same defence
   * {@link fetchProducts} applies against the reorder-alert listing is repeated here, so a response
   * that ever carried a retired product still cannot be offered.
   */
  private searchAddableProducts(term: string): Observable<unknown> {
    const trimmed = term.trim();
    if (!trimmed) {
      this.addProductResults = [];
      this.addProductSearchState = 'idle';
      return EMPTY;
    }

    this.addProductSearchState = 'loading';
    return this.productService.getAll({ search: trimmed }).pipe(
      tap((results) => {
        this.addProductResults = results.filter((product) => product.isActive !== false);
        this.addProductSearchState = 'loaded';
      }),
      catchError(() => {
        this.addProductResults = [];
        this.addProductSearchState = 'failed';
        return EMPTY;
      })
    );
  }

  /**
   * Adds a product the Add product search found to the order being built (issue #907), without
   * disturbing the supplier, dates, reference, notes or any quantity already entered for another
   * row. Starts at the product's own need-to-order when that is positive, otherwise at 1.
   *
   * **Which collection it joins is decided by whether the page already lists it.** A product that
   * is on the Needs ordering list - visible on the page behind the dialog, simply not ticked - is
   * added through `selectedProductIds`, the very set the page's checkboxes are bound to, so it is
   * ticked there as issue #907 requires and {@link removeProduct} unticks it again. Only a product
   * the page never listed becomes one of {@link addedProducts}, because there is no checkbox of it
   * to tick and the page's list must stay exactly as it was. The row is rendered from the listed
   * product's own object in either case, so its stock, on-order and need-to-order figures are the
   * ones the page calculated rather than a second copy from the catalogue search.
   *
   * A product already on the order is refused outright, here at the mutation boundary and not only
   * by {@link addableProducts} not offering it: a duplicate line would be rejected by
   * `CreateSupplierOrder` anyway, and resetting an already-entered quantity to a default is a
   * silent loss of what the user typed.
   *
   * Guarded the same way {@link removeProduct} is, and for the same reasons: refused during a
   * support session, and once {@link saveOrder} has sent its lines, so a line can never be added
   * after the request that would have had to carry it.
   */
  addProduct(product: Product): void {
    if (this.savingOrder || this.sessionActive) {
      return;
    }
    if (this.orderLines().some((line) => line.id === product.id)) {
      return;
    }

    const listed = this.products.find((candidate) => candidate.id === product.id);
    if (listed) {
      this.selectedProductIds.add(listed.id);
    } else {
      this.addedProducts = [...this.addedProducts, product];
    }

    const line = listed ?? product;
    this.orderQuantities[line.id] = line.needToOrder > 0 ? line.needToOrder : 1;
    this.resetAddProductSearch();
  }

  /**
   * Empties the Add product box and its results, and tells the search trigger that no term is
   * applied any more (issue #907). Used when the dialog opens and after an add consumed the query:
   * both clear the box without a keystroke, and without this the debounced, change-only trigger
   * would still remember the cleared term and ignore it if it were typed again.
   */
  private resetAddProductSearch(): void {
    this.addProductQuery = '';
    this.addProductResults = [];
    this.addProductSearchState = 'idle';
    this.addProductSearchReset$.next();
  }

  /**
   * Drops a product from the order being built, without cancelling the dialog (issue #906). A row
   * ticked on the Needs ordering list is dropped from `selectedProductIds` - the same set the
   * page's checkboxes are bound to, so this is also what unticks it there, including for a listed
   * product the Add product search ticked (issue #907) - and a row that search added from outside
   * the page's list is dropped from {@link addedProducts} instead; the page's own list is never
   * touched for one of those, since it was never on it. Either way every other row's
   * entered quantity and the order's own fields are left untouched. Refused below the last
   * remaining row, because an order needs at least one line.
   *
   * Refused as well while an order is in flight (`savingOrder`) or during a support session. Once
   * {@link saveOrder} has sent its lines, the order being created is fixed: dropping a row then
   * would leave the dialog and the page's ticks showing an order that disagrees with the one the
   * backend is creating, and - if the request fails - would have silently changed the draft the
   * user is about to retry. The session guard matches {@link openOrderForm} and {@link saveOrder}:
   * a session grants `InventoryOperate`, so the page refuses its writes itself (issue #661).
   */
  removeProduct(productId: number): void {
    if (this.savingOrder || this.sessionActive || this.orderLines().length <= 1) {
      return;
    }
    if (this.selectedProductIds.has(productId)) {
      this.selectedProductIds.delete(productId);
    } else {
      this.addedProducts = this.addedProducts.filter((product) => product.id !== productId);
    }
  }

  saveOrder(): void {
    if (this.sessionActive) {
      return;
    }
    if (!this.orderForm.supplierId) {
      this.toastService.error('Select a supplier for this order.');
      return;
    }

    const lines = this.orderLines().map((product) => ({
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
