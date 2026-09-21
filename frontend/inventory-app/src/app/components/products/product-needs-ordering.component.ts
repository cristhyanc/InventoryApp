import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
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
import { SupplierOrderService } from '../../services/supplier-order.service';

@Component({
  selector: 'app-product-needs-ordering',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, ConfirmationDialogComponent],
  templateUrl: './product-needs-ordering.component.html'
})
export class ProductNeedsOrderingComponent implements OnInit, OnDestroy {
  products: Product[] = [];
  categories: Category[] = [];
  suppliers: Supplier[] = [];
  readonly loadState = new ListLoadState();
  confirmingProduct: Product | null = null;
  search = '';
  categoryId: number | '' = '';
  supplierId: number | '' = '';
  selectedProductIds = new Set<number>();
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

  private readonly search$ = new Subject<string>();
  private readonly immediateFilter$ = new Subject<void>();
  private readonly destroy$ = new Subject<void>();

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private supplierService: SupplierService,
    private supplierOrderService: SupplierOrderService,
    private toastService: ToastService
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
    if (this.selectedProductIds.size === 0) {
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
