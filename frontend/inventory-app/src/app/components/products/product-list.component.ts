import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink, ActivatedRoute } from '@angular/router';
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
  confirmingProduct: Product | null = null;
  confirmingImport: boolean = false;
  search = '';
  categoryId: number | '' = '';
  supplierId: number | '' = '';
  lowStockOnly = false;

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private supplierService: SupplierService,
    private route: ActivatedRoute,
    private toastService: ToastService
  ) {}

  ngOnInit(): void {
    this.categoryService.getAll().subscribe((c) => (this.categories = c));
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));

    this.route.queryParams.subscribe((params) => {
      if (params['lowStockOnly'] === 'true') this.lowStockOnly = true;
      this.applyFilters();
    });
  }

  applyFilters(): void {
    this.productService
      .getAll({
        search: this.search || undefined,
        categoryId: this.categoryId === '' ? undefined : this.categoryId,
        supplierId: this.supplierId === '' ? undefined : this.supplierId,
        lowStockOnly: this.lowStockOnly || undefined
      })
      .subscribe((p) => (this.products = p));
  }

  resetFilters(): void {
    this.search = '';
    this.categoryId = '';
    this.supplierId = '';
    this.lowStockOnly = false;
    this.applyFilters();
  }

  confirmImportProducts(): void {
    this.confirmingImport = true;
  }

  importProducts(): void {
    this.productService.importProducts().subscribe({
        next: () => this.toastService.success('Products imported successfully.'),
        error: () => { this.toastService.error('Failed to import products.'); }
      });


    this.confirmingImport = true;
  }

  cancelImportProducts(): void {
    this.confirmingImport = false;
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
}
