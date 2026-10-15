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
import { ListLoadState } from '../shared/list-load-state';
import { createFilterTrigger$ } from '../shared/filter-request-trigger';
import { SortDirection, SortValue, compareSortValues, nextSortState, sortAriaState, sortColumnLabel } from '../shared/sortable-column';
import { ToastService } from '../../services/toast.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { SupportSessionService } from '../../support-session/support-session.service';

type NeedsOrderingSortColumn =
  | 'name'
  | 'category'
  | 'supplier'
  | 'price'
  | 'stock'
  | 'machineNeed'
  | 'lowStockThreshold'
  | 'restockTo'
  | 'onOrder'
  | 'needToOrder'
  | 'status';

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
  supplierId: number | '' = '';
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

  private readonly search$ = new Subject<string>();
  private readonly immediateFilter$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private supplierService: SupplierService,
    private supplierOrderService: SupplierOrderService,
    private toastService: ToastService,
    private router: Router,
    supportSession: SupportSessionService
  ) {
    supportSession.active$.pipe(takeUntil(this.destroy$)).subscribe((active) => (this.sessionActive = active));
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
  }

  private fetchProducts() {
    const filters = {
      search: this.search || undefined,
      categoryId: this.categoryId === '' ? undefined : this.categoryId,
      supplierId: this.supplierId === '' ? undefined : this.supplierId
    };

    const token = this.loadState.start();
    return this.productService.getLowStock(filters).pipe(
      tap((products) => {
        if (this.loadState.isCurrent(token)) {
          this.products = products;
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
    const supplierIds = [...new Set(selectedProducts.map((product) => product.supplierId).filter((id): id is number => id != null))];
    this.orderForm.supplierId = supplierIds.length === 1 ? supplierIds[0] : null;
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
   * (the name link, checkbox, Stock, Status) or is a modified/non-primary click the browser should handle.
   * The name cell's routerLink anchor is the real link: it gives keyboard focus, open-in-new-tab and copy-link.
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
      case 'supplier':
        return product.supplier?.name ?? null;
      case 'price':
        return product.unitPrice;
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
