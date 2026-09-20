import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { Product, Category, Supplier } from '../../models/models';
import { ConfirmationDialogComponent } from '../shared/confirmation-dialog.component';
import { ToastService } from '../../services/toast.service';

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
  loaded = false;
  confirmingProduct: Product | null = null;
  search = '';
  categoryId: number | '' = '';
  supplierId: number | '' = '';

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private supplierService: SupplierService,
    private toastService: ToastService
  ) {}

  ngOnInit(): void {
    this.categoryService.getAll().subscribe((categories) => (this.categories = categories));
    this.supplierService.getAll().subscribe((suppliers) => (this.suppliers = suppliers));
    this.applyFilters();
  }

  applyFilters(): void {
    const filters = {
      search: this.search || undefined,
      categoryId: this.categoryId === '' ? undefined : this.categoryId,
      supplierId: this.supplierId === '' ? undefined : this.supplierId
    };

    this.loaded = false;
    this.productService.getAll(filters).subscribe({
      next: (products) => {
        this.products = products;
        this.loaded = true;
      },
      error: () => {
        this.loaded = true;
        this.toastService.error('Failed to load products.');
      }
    });
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
