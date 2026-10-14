import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, Subject, Subscription } from 'rxjs';
import { catchError, switchMap, takeUntil, tap } from 'rxjs/operators';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Category, Supplier } from '../../models/models';
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

type ProductSortColumn = 'name' | 'category' | 'supplier' | 'price' | 'stock' | 'status';

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, ConfirmationDialogComponent],
  templateUrl: './product-list.component.html'
})
export class ProductListComponent implements OnInit, OnDestroy {
  products: Product[] = [];
  categories: Category[] = [];
  suppliers: Supplier[] = [];
  readonly loadState = new ListLoadState();
  confirmingProduct: Product | null = null;
  search = '';
  categoryId: number | '' = '';
  supplierId: number | '' = '';
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
    private supplierService: SupplierService,
    private toastService: ToastService,
    private router: Router,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnInit(): void {
    this.categoryService.getAll().subscribe((categories) => (this.categories = categories));
    this.supplierService.getAll().subscribe((suppliers) => (this.suppliers = suppliers));

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
      supplierId: this.supplierId === '' ? undefined : this.supplierId
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

  /** Triggers an immediate (non-debounced) refresh, e.g. for category/supplier changes. */
  applyFilters(): void {
    this.immediateFilter$.next();
  }

  resetFilters(): void {
    this.search = '';
    this.categoryId = '';
    this.supplierId = '';
    this.applyFilters();
  }

  confirmDelete(product: Product): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive) {
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
    this.productService.delete(product.id).subscribe(() => this.applyFilters());
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
      case 'supplier':
        return product.supplier?.name ?? null;
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
      supplierId: product.supplierId ?? null,
      isActive: !product.isActive
    }).subscribe({
      next: () => this.applyFilters(),
      error: () => this.toastService.error('Failed to update product status.')
    });
  }
}
