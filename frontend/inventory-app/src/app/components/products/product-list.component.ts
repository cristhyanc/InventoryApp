import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, Subject } from 'rxjs';
import { catchError, switchMap, takeUntil, tap } from 'rxjs/operators';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Category, Supplier } from '../../models/models';
import { ConfirmationDialogComponent } from '../shared/confirmation-dialog.component';
import { ListLoadState } from '../shared/list-load-state';
import { createFilterTrigger$ } from '../shared/filter-request-trigger';
import { ToastService } from '../../services/toast.service';

type ProductSortColumn = 'name' | 'category' | 'supplier' | 'price' | 'stock' | 'status';
type SortDirection = 'asc' | 'desc';
type SortValue = string | number | Date | null;

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

  private readonly search$ = new Subject<string>();
  private readonly immediateFilter$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private supplierService: SupplierService,
    private toastService: ToastService,
    private router: Router
  ) {}

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
      const comparison = this.compareValues(this.sortValue(left, column), this.sortValue(right, column));
      // Ties keep a deterministic order (by id) instead of relying on sort stability alone.
      const resolved = comparison !== 0 ? comparison : left.id - right.id;
      return direction === 'desc' ? -resolved : resolved;
    });
  }

  sortBy(column: ProductSortColumn): void {
    if (this.sortColumn === column) {
      this.sortDirection = this.sortDirection === 'asc' ? 'desc' : 'asc';
      return;
    }

    this.sortColumn = column;
    this.sortDirection = 'asc';
  }

  ariaSort(column: ProductSortColumn): 'ascending' | 'descending' | 'none' {
    if (this.sortColumn !== column) {
      return 'none';
    }

    return this.sortDirection === 'asc' ? 'ascending' : 'descending';
  }

  sortLabel(label: string, column: ProductSortColumn): string {
    if (this.sortColumn !== column) {
      return label;
    }

    return `${label} (${this.sortDirection === 'asc' ? 'ascending' : 'descending'})`;
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

  /** Missing values always sort last on ascending; numbers compare numerically, everything else as text. */
  private compareValues(left: SortValue, right: SortValue): number {
    if (left == null && right == null) {
      return 0;
    }
    if (left == null) {
      return 1;
    }
    if (right == null) {
      return -1;
    }

    if (left instanceof Date && right instanceof Date) {
      return left.getTime() - right.getTime();
    }
    if (typeof left === 'number' && typeof right === 'number') {
      return left - right;
    }

    return String(left).localeCompare(String(right), undefined, { sensitivity: 'base' });
  }

  toggleActive(product: Product): void {
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
