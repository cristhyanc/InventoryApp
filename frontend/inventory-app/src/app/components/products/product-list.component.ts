import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, Subject, Subscription } from 'rxjs';
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

// No 'supplier' column since issue #787 (A1 of #786): a product carries no supplier to sort by.
type ProductSortColumn = 'name' | 'category' | 'price' | 'stock' | 'status';

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
   * Whether to list a deactivated product too (issue #737): off by default, because `GET
   * /api/products` answers only the working catalogue unless asked otherwise. Checking it is what
   * keeps a just-deactivated product reachable to Reactivate - the toggle flips `isActive` either
   * way, but the row has to still be on screen to click it.
   */
  includeRetired = false;
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

  private readonly search$ = new Subject<string>();
  private readonly immediateFilter$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();
  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private toastService: ToastService,
    private router: Router,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
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
  }

  private fetchProducts() {
    const filters = {
      search: this.search || undefined,
      categoryId: this.categoryId === '' ? undefined : this.categoryId,
      includeRetired: this.includeRetired
    };

    const token = this.loadState.start();
    return this.productService.getAll(filters).pipe(
      tap((products) => {
        if (this.loadState.isCurrent(token)) {
          this.products = products;
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
    this.includeRetired = false;
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
    return [...this.products].sort((left, right) => {
      const comparison = compareSortValuesDirectionalNulls(this.sortValue(left, column), this.sortValue(right, column));
      // Ties keep a deterministic order (by id) instead of relying on sort stability alone.
      const resolved = comparison !== 0 ? comparison : left.id - right.id;
      return direction === 'desc' ? -resolved : resolved;
    });
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
      case 'price':
        return product.unitPrice;
      case 'stock':
        return product.quantityInStock;
      case 'status':
        return product.isActive ? 1 : 0;
    }
  }

  toggleActive(product: Product): void {
    if (this.sessionActive) {
      return;
    }
    this.productService.update(product.id, {
      name: product.name,
      sku: product.sku ?? null,
      description: product.description ?? null,
      unitPrice: product.unitPrice,
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
