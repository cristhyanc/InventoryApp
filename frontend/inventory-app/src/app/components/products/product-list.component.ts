import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, Subject, Subscription, combineLatest } from 'rxjs';
import { catchError, switchMap, takeUntil, tap } from 'rxjs/operators';
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
import { ProviderAvailability, ProviderAvailabilityService, providerFeatureOffered } from '../../provider/provider-availability.service';
import { providerToolsOffered } from '../../provider/provider-lock';
import {
  PURCHASE_COSTS_NOT_READ_YET,
  ProductPurchaseCostColumn,
  ProductPurchaseCosts,
  ProductPurchaseCostsComponent,
  purchaseCostDateDisplay,
  purchaseCostDisplay,
  purchaseCostSortValue,
  purchaseCostSupplierDisplay
} from './product-purchase-costs/product-purchase-costs.component';
import {
  CHEAPEST_SUPPLIERS_NOT_READ_YET,
  CheapestSuppliers,
  CheapestSuppliersComponent,
  cheapestSupplierFor
} from '../shared/cheapest-supplier/cheapest-suppliers.component';
import { CheapestSupplierSummaryComponent } from '../shared/cheapest-supplier/cheapest-supplier-summary.component';
import { HelpToggletipComponent } from '../shared/help-toggletip/help-toggletip.component';
import { ActionMenuComponent } from '../shared/action-menu/action-menu.component';
import { ListPaginationComponent } from '../shared/list-pagination/list-pagination.component';
import { clampPageIndex, pageSlice } from '../shared/list-pagination/paging';
import { IconComponent } from '../shared/icon.component';
import { CheapestSupplierProduct } from '../../models/cheapest-supplier';
import { ProductGstRuleBulkComponent } from './product-gst-rule-bulk/product-gst-rule-bulk.component';

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

/** The page sizes the Products pager offers (issue #1093), the Dashboard's own set. */
export const PRODUCT_PAGE_SIZE_OPTIONS = [5, 10, 20, 50, 100] as const;

/** The page size every visit starts at (issue #1093's approved design). Not persisted. */
export const PRODUCT_DEFAULT_PAGE_SIZE = 10;

/**
 * What the Cheapest supplier heading's help says once for every row (issue #1093): what the prices
 * are, and that a supplier's stock may differ - the note each cell used to repeat.
 */
export const PRODUCTS_CHEAPEST_SUPPLIER_HELP =
  'Cheapest supplier is the lowest price your own suppliers currently list per unit, on the GST basis shown '
  + 'beside it - observed prices for purchasing guidance, not costs you have paid. These are online prices and may differ in store, and supplier stock may differ. '
  + 'Compare lists every supplier with its pack, prices and when it was last checked.';

export const PRODUCTS_CHEAPEST_SUPPLIER_HELP_LABEL = 'About Cheapest supplier';

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    ConfirmationDialogComponent,
    ProductPurchaseCostsComponent,
    CheapestSuppliersComponent,
    CheapestSupplierSummaryComponent,
    HelpToggletipComponent,
    ActionMenuComponent,
    ListPaginationComponent,
    IconComponent,
    ProductGstRuleBulkComponent
  ],
  templateUrl: './product-list.component.html'
})
export class ProductListComponent implements OnInit, OnDestroy {
  products: Product[] = [];
  categories: Category[] = [];
  readonly loadState = new ListLoadState();
  confirmingProduct: Product | null = null;
  /**
   * The product whose deactivation is waiting to be confirmed (issue #825), or `null` while none
   * is. Deactivation is reversible and keeps everything, but it does take a product out of the
   * working catalogue, the product pickers and the reorder alerts, so it is confirmed rather than
   * being one unexplained click - whether it was started from the Deactivate button or from the
   * Status toggle. Reactivation stays one click: putting a product back is not a loss.
   */
  confirmingDeactivation: Product | null = null;
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
   * The page of the sorted list on screen (issue #1093). Paging is client-side over the one list
   * already read - the same strategy as the Dashboard's Reorder priorities - so a page or size
   * change re-requests nothing and the supplier and cost reads stay keyed to every listed product.
   */
  pageIndex = 0;
  pageSize: number = PRODUCT_DEFAULT_PAGE_SIZE;
  readonly pageSizeOptions: readonly number[] = PRODUCT_PAGE_SIZE_OPTIONS;

  readonly cheapestSupplierHelp = PRODUCTS_CHEAPEST_SUPPLIER_HELP;
  readonly cheapestSupplierHelpLabel = PRODUCTS_CHEAPEST_SUPPLIER_HELP_LABEL;

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
   * the Last/Cheapest purchase columns are hidden without it, on the same fail-open default every
   * other capability-gated control in this codebase starts from while the capability has not been
   * read yet. Average cost is unaffected and always shown. The *request* behind those two columns
   * is gated separately and independently, by `ProductPurchaseCostsComponent`'s own fresh read of
   * the same capability, so this page's fail-open window can never issue one.
   */
  canViewFinancials = true;

  /**
   * Whether Import products is offered beside New Product (issue #875): only while the member
   * holds `Imports.Run`, the shared provider-availability signal says the provider tools are
   * offered (`providerToolsOffered`), the `productCatalogue` feature is offered
   * (`providerFeatureOffered`), and no support session is active. It is recomputed whenever any of
   * those three answers changes, the same way `canViewFinancials` is, so the button appears and
   * disappears without a reload.
   */
  showImportProducts = false;

  /**
   * The ids of the products currently displayed, handed to `<app-product-purchase-costs>` as the
   * one piece of context that read needs. They change only once a new fetch actually succeeds (a
   * failed refetch leaves the previous rows on screen), which is the one moment the previous
   * purchase-cost results stop describing what is on screen.
   */
  productIds: readonly number[] = [];

  /**
   * What `<app-product-purchase-costs>` has published about {@link productIds}: the state of its
   * read and the summaries it found. This page only lays that answer out in its cost cells and
   * sorts on it; the read itself, its capability gate, its failure message and its Retry control
   * belong to that component (issue #191's page composition boundary).
   */
  purchaseCosts: ProductPurchaseCosts = PURCHASE_COSTS_NOT_READ_YET;

  /**
   * Where `<app-cheapest-suppliers>`'s one read for {@link productIds} stands, and the answers it
   * found (issue #940, reusing #930's shared component). This page only lays that answer out in the
   * Cheapest supplier column through {@link cheapestSupplier}; the request, its batching, its
   * failure message and its Retry control belong to that component (issue #191's page composition
   * boundary).
   */
  cheapestSuppliers: CheapestSuppliers = CHEAPEST_SUPPLIERS_NOT_READ_YET;

  /**
   * Whether the "Set GST rule in bulk" dialog is open (issue #1076). The page owns only that: the
   * eligible count, the choice, the confirmation, the write, its conflict and its failures all
   * belong to `<app-product-gst-rule-bulk>` (issue #191's page composition boundary).
   */
  settingGstRuleInBulk = false;

  private readonly search$ = new Subject<string>();
  private readonly immediateFilter$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();
  private readonly sessionActiveSubscription: Subscription;
  private readonly financialsAccessSubscription: Subscription;
  private readonly importProductsVisibilitySubscription: Subscription;

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private toastService: ToastService,
    private router: Router,
    supportSession: SupportSessionService,
    access: AccessService,
    provider: ProviderAvailabilityService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    this.financialsAccessSubscription = access.allows('Dashboard.ViewFinancials').subscribe((allowed) => {
      this.canViewFinancials = allowed;
      this.clearPurchaseCostSortWhenHidden();
    });
    this.importProductsVisibilitySubscription = combineLatest([
      access.allows('Imports.Run'),
      provider.watch(),
      supportSession.active$
    ]).subscribe(([importsRunAllowed, providerAvailability, sessionActive]) => {
      this.showImportProducts = ProductListComponent.importProductsOffered(importsRunAllowed, providerAvailability, sessionActive);
    });
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

    this.applyFilters();
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
    this.sessionActiveSubscription.unsubscribe();
    this.financialsAccessSubscription.unsubscribe();
    this.importProductsVisibilitySubscription.unsubscribe();
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
          this.productIds = products.map((product) => product.id);
          // A refresh can shorten the list (a filter, a deactivation): stay on the same page while it
          // still exists, and never land on an empty one past the end.
          this.pageIndex = clampPageIndex(this.pageIndex, products.length, this.pageSize);
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

  /** Records what `<app-product-purchase-costs>` has published, for the cost cells and their sort. */
  onPurchaseCostsChange(costs: ProductPurchaseCosts): void {
    this.purchaseCosts = costs;
  }

  /**
   * This product's cheapest-supplier answer, or `undefined` while the read has not established one
   * (issue #940). The cell tells the states apart from the read's own state, never from this being
   * absent.
   */
  cheapestSupplier(product: Product): CheapestSupplierProduct | undefined {
    return cheapestSupplierFor(this.cheapestSuppliers, product.id);
  }

  /**
   * Opens "Set GST rule in bulk" (issue #1076). Checked here as well as hidden in the template, so
   * the write is actually unavailable rather than merely invisible during a support session - the
   * endpoints refuse it either way, by the same central read-only method rule that refuses New
   * Product and Delete on this page.
   */
  openBulkGstRule(): void {
    if (this.sessionActive) {
      return;
    }

    this.settingGstRuleInBulk = true;
  }

  closeBulkGstRule(): void {
    this.settingGstRuleInBulk = false;
  }

  /**
   * What the dialog reported it configured. The count is the server's own answer from the committed
   * write, so the summary states what happened rather than what was asked for, and the list is
   * refetched because a product's rule is not on the catalogue payload - the refresh is what keeps
   * every other column on screen consistent with a catalogue that has just been written to.
   */
  bulkGstRuleApplied(changedCount: number): void {
    this.toastService.success(
      changedCount === 1
        ? 'GST rule set on 1 product that had no rule.'
        : `GST rule set on ${changedCount} products that had no rule.`
    );
    this.applyFilters();
  }

  onSearchChange(value: string): void {
    this.search = value;
    this.pageIndex = 0;
    this.search$.next(value);
  }

  /** The search box's own clear button: an immediate refresh rather than a debounced one. */
  clearSearch(): void {
    if (this.search === '') {
      return;
    }
    this.search = '';
    this.pageIndex = 0;
    this.applyFilters();
  }

  onCategoryChange(): void {
    this.pageIndex = 0;
    this.applyFilters();
  }

  /** Whether any filter differs from its default, which is the only time Clear filters is offered. */
  get filtersDiffer(): boolean {
    return this.search !== '' || this.categoryId !== '' || this.statusFilter !== 'active';
  }

  /** The products on the current page, in the current sort - one list both the table and the cards render. */
  get pagedProducts(): Product[] {
    return pageSlice(this.sortedProducts, this.pageIndex, this.pageSize);
  }

  onPageIndexChange(index: number): void {
    this.pageIndex = clampPageIndex(index, this.products.length, this.pageSize);
  }

  /** A new page size is a fresh view of the same list, so it returns to page 1. */
  onPageSizeChange(size: number): void {
    if (!this.pageSizeOptions.includes(size)) {
      return;
    }
    this.pageSize = size;
    this.pageIndex = 0;
  }

  /** Triggers an immediate (non-debounced) refresh, e.g. for a category change. */
  applyFilters(): void {
    this.immediateFilter$.next();
  }

  resetFilters(): void {
    this.search = '';
    this.categoryId = '';
    this.statusFilter = 'active';
    this.pageIndex = 0;
    this.clearStatusSortWhenHidden();
    this.applyFilters();
  }

  /**
   * The Status filter's own change handler (issue #828): switching to "Only active" hides the
   * Status column, so a sort by it would otherwise point at a column no longer on screen.
   */
  onStatusFilterChange(value: ProductStatusFilter): void {
    this.statusFilter = value;
    this.pageIndex = 0;
    this.clearStatusSortWhenHidden();
    this.applyFilters();
  }

  /** Clears a Status sort back to the default order once "Only active" hides the column it sorts. */
  private clearStatusSortWhenHidden(): void {
    if (this.statusFilter === 'active' && this.sortColumn === 'status') {
      this.sortColumn = null;
      this.sortDirection = 'asc';
    }
  }

  /**
   * Clears a Last/Cheapest purchase sort back to the default order once `canViewFinancials` turns
   * false and hides both columns (issue #1098): they are no longer optional, but the role's
   * capability can still change mid-visit, and a sort left pointing at a column no longer on
   * screen would otherwise keep ordering the list by it invisibly.
   */
  private clearPurchaseCostSortWhenHidden(): void {
    if (!this.canViewFinancials && (this.sortColumn === 'lastPurchase' || this.sortColumn === 'cheapestPurchase')) {
      this.sortColumn = null;
      this.sortDirection = 'asc';
    }
  }

  /**
   * Whether the API says this product may be deleted (issue #737). `canDelete` is the deletion
   * rule's own answer, published by the catalogue listing from the same
   * `Inventory.Domain.Products.ProductDeletionPolicy` that refuses the delete itself - a provider
   * link, stock movements, purchase lines, supplier order lines, sales, machine stock counts and
   * costing records all reach it. Delete is offered only on a plain `true`: an absent answer (a
   * response that never resolved the question) is not permission, and the row then offers
   * Deactivate instead, which is always available for an active product.
   *
   * The answer stays advisory - it is as fresh as the last fetch - and the server decides again
   * inside the delete's own transaction, which `deleteConfirmed` still surfaces.
   */
  canDelete(product: Product): boolean {
    return product.canDelete === true;
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
    // The supplier comparison dialog and the row's overflow menu are rendered inside the row, so a
    // click anywhere in them is theirs too, never a request to open the editor (issue #1093).
    const ownControl = (event.target as Element | null)?.closest('a, button, input, select, dialog, [popover]');
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

  /**
   * Whether Import products belongs beside New Product right now (issue #875). A provider answer
   * that has not settled yet (`undefined`) fails open, the same way `navigationFor`'s own
   * provider-tools check does: an unread status is not a refusal.
   */
  private static importProductsOffered(
    importsRunAllowed: boolean,
    provider: ProviderAvailability | undefined,
    sessionActive: boolean
  ): boolean {
    if (!importsRunAllowed || sessionActive) {
      return false;
    }
    if (provider === undefined) {
      return true;
    }
    return providerToolsOffered(provider.connection) && providerFeatureOffered(provider.features.productCatalogue);
  }

  /** A new sort is a new order of the whole list, so it starts again from page 1. */
  sortBy(column: ProductSortColumn): void {
    const next = nextSortState(this.sortColumn, this.sortDirection, column);
    this.sortColumn = next.column;
    this.sortDirection = next.direction;
    this.pageIndex = 0;
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
      case 'cheapestPurchase':
        return purchaseCostSortValue(this.purchaseCosts, product.id, column);
    }
  }

  /** `—` for an unestablished average cost (never `0`); a genuine `0` renders as `$0.00`. */
  averageCostDisplay(product: Product): string {
    const value = product.averageUnitCostForDisplay;
    return value == null ? '—' : this.money(value);
  }

  /** The Last/Cheapest purchase cost cell's text, as the feature component's own display rule states it. */
  purchaseCostDisplay(product: Product, column: ProductPurchaseCostColumn): string {
    return purchaseCostDisplay(this.purchaseCosts, product.id, column);
  }

  /** The recorded date of the purchase behind a found Last/Cheapest cost, or `null`. */
  purchaseCostDateDisplay(product: Product, column: ProductPurchaseCostColumn): string | null {
    return purchaseCostDateDisplay(this.purchaseCosts, product.id, column);
  }

  /** The supplier beside a found Last/Cheapest cost, or `null` while there is no cost to show one beside. */
  purchaseCostSupplierDisplay(product: Product, column: ProductPurchaseCostColumn): string | null {
    return purchaseCostSupplierDisplay(this.purchaseCosts, product.id, column);
  }

  private money(value: number): string {
    return `$${value.toFixed(2)}`;
  }

  /**
   * The activation write shared by the Status badge and, since issue #900, the Actions column's
   * Reactivate button. Deactivating goes through the same confirmation the Deactivate button opens
   * (issue #825), so the two controls cannot mean different things; reactivating writes straight
   * away, as it always has, and a refusal is shown the same way deactivation's is - the server's
   * own message through `apiErrorMessage`, falling back to this generic sentence when there is none.
   */
  toggleActive(product: Product): void {
    if (this.sessionActive) {
      return;
    }
    if (product.isActive) {
      this.confirmDeactivate(product);
      return;
    }

    this.writeActive(product, true).subscribe({
      next: () => this.applyFilters(),
      error: (error) => this.toastService.error(apiErrorMessage(error, 'Failed to update product status.'))
    });
  }

  confirmDeactivate(product: Product): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way. An already-inactive product has
    // nothing to deactivate.
    if (this.sessionActive || !product.isActive) {
      return;
    }
    this.confirmingDeactivation = product;
  }

  cancelDeactivate(): void {
    this.confirmingDeactivation = null;
  }

  deactivateConfirmed(): void {
    const product = this.confirmingDeactivation;
    if (!product) {
      return;
    }

    this.confirmingDeactivation = null;
    this.writeActive(product, false).subscribe({
      // The product leaves the default "Only active" view on success; "All" keeps it on screen to
      // reactivate.
      next: () => this.applyFilters(),
      error: (error) => this.toastService.error(apiErrorMessage(error, 'Failed to deactivate product.'))
    });
  }

  /** What the confirmation says deactivation does, and what it leaves alone. */
  deactivationMessage(product: Product): string {
    return (
      `Deactivate '${product.name}'? It will be hidden from the working catalogue and the reorder alerts, ` +
      'keeps its history, stock and costs, and can be reactivated at any time.'
    );
  }

  /**
   * The one activation write this page makes: the existing product update with `isActive` set and
   * every other field sent back as it was fetched, so flipping the flag changes nothing else.
   */
  private writeActive(product: Product, isActive: boolean) {
    return this.productService.update(product.id, {
      name: product.name,
      sku: product.sku ?? null,
      description: product.description ?? null,
      lowStockThreshold: product.lowStockThreshold,
      restockTo: product.restockTo,
      unit: product.unit ?? null,
      categoryId: product.categoryId ?? null,
      isActive
    });
  }
}
