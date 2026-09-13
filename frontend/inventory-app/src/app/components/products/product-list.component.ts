import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Category, Supplier } from '../../models/models';
import { ConfirmationDialogComponent } from '../shared/confirmation-dialog.component';
import { ToastService } from '../../services/toast.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import { SupplierOrder } from '../../models/models';

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, ConfirmationDialogComponent],
  templateUrl: './product-list.component.html'
})
export class ProductListComponent implements OnInit {
  products: Product[] = [];
  categories: Category[] = [];
  suppliers: Supplier[] = [];
  confirmingProduct: Product | null = null;
  search = '';
  categoryId: number | '' = '';
  supplierId: number | '' = '';
  lowStockOnly = false;
  reorderView: 'needs' | 'onOrder' | 'all' = 'all';
  orders: SupplierOrder[] = [];
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

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private supplierService: SupplierService,
    private supplierOrderService: SupplierOrderService,
    private route: ActivatedRoute,
    private router: Router,
    private toastService: ToastService
  ) {}

  ngOnInit(): void {
    this.categoryService.getAll().subscribe((c) => (this.categories = c));
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));

    this.route.queryParams.subscribe((params) => {
      // Handle reorderView parameter
      if (params['reorderView']) {
        this.reorderView = params['reorderView'] as 'needs' | 'onOrder' | 'all';
      } else if (params['lowStockOnly'] === 'true') {
        // Backward compatibility with lowStockOnly parameter
        this.reorderView = 'needs';
      } else {
        this.reorderView = 'all';
      }
      this.lowStockOnly = this.reorderView === 'needs';
      this.applyFilters();
    });
  }

  applyFilters(): void {
    const filters = {
      search: this.search || undefined,
      categoryId: this.categoryId === '' ? undefined : this.categoryId,
      supplierId: this.supplierId === '' ? undefined : this.supplierId
    };
    if (this.reorderView === 'onOrder') {
      this.supplierOrderService.getActive().subscribe({
        next: (orders) => (this.orders = orders),
        error: () => this.toastService.error('Failed to load supplier orders.')
      });
      return;
    }
    this.lowStockOnly = this.reorderView === 'needs';
    const request = this.lowStockOnly
      ? this.productService.getLowStock(filters)
      : this.productService.getAll(filters);
    request
      .subscribe((p) => {
        this.products = p;
      });
  }

  resetFilters(): void {
    this.search = '';
    this.categoryId = '';
    this.supplierId = '';
    this.lowStockOnly = false;
    this.reorderView = 'all';
    this.applyFilters();
  }

  setReorderView(view: 'needs' | 'onOrder' | 'all'): void {
    this.reorderView = view;
    this.selectedProductIds.clear();
    this.applyFilters();
  }

  toggleSelection(productId: number, checked: boolean): void {
    if (checked) this.selectedProductIds.add(productId);
    else this.selectedProductIds.delete(productId);
  }

  openOrderForm(): void {
    if (this.selectedProductIds.size === 0) return;
    const selectedProducts = this.products.filter((product) => this.selectedProductIds.has(product.id));
    const supplierIds = [...new Set(selectedProducts.map((product) => product.supplierId).filter((id): id is number => id != null))];
    this.orderForm.supplierId = supplierIds.length === 1 ? supplierIds[0] : null;
    this.orderQuantities = Object.fromEntries(selectedProducts.map((product) => [product.id, product.reorderShortfall]));
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
    if (lines.some((line) => line.quantityOrdered <= 0)) return;
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
        this.setReorderView('needs');
      },
      error: () => {
        this.savingOrder = false;
        this.toastService.error('Failed to create supplier order.');
      }
    });
  }

  cancelOrder(order: SupplierOrder): void {
    this.supplierOrderService.cancel(order.id).subscribe({
      next: () => {
        this.toastService.success('Supplier order cancelled.');
        this.applyFilters();
      },
      error: () => this.toastService.error('Unable to cancel this supplier order.')
    });
  }

  receiveOrder(order: SupplierOrder): void {
    this.router.navigate(['/receipts/new'], { queryParams: { supplierOrderId: order.id } });
  }

  orderStatus(status: number): string {
    return ['Ordered', 'Partially received', 'Received', 'Cancelled'][status] ?? 'Unknown';
  }

  confirmDelete(product: Product): void {
    this.confirmingProduct = product;
  }

  cancelDelete(): void {
    this.confirmingProduct = null;
  }

  deleteConfirmed(): void {
    if (!this.confirmingProduct) return;

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
