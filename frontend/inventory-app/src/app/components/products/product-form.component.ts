import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, ActivatedRoute, RouterLink } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { Category, Supplier } from '../../models/models';

@Component({
  selector: 'app-product-form',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './product-form.component.html'
})
export class ProductFormComponent implements OnInit {
  productId: number | null = null;
  categories: Category[] = [];
  suppliers: Supplier[] = [];
  saving = false;
  error = '';

  form = {
    name: '',
    sku: '',
    description: '',
    unitPrice: 0,
    lowStockThreshold: 5,
    restockTo: 5,
    unit: 'unit',
    categoryId: '' as number | '',
    supplierId: '' as number | '',
    isActive: true
  };

  constructor(
    private productService: ProductService,
    private categoryService: CategoryService,
    private supplierService: SupplierService,
    private router: Router,
    private route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    this.categoryService.getAll().subscribe((c) => (this.categories = c));
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));

    const idParam = this.route.snapshot.paramMap.get('id');
    if (idParam) {
      this.productId = Number(idParam);
      this.productService.get(this.productId).subscribe((p) => {
        this.form = {
          name: p.name,
          sku: p.sku ?? '',
          description: p.description ?? '',
          unitPrice: p.unitPrice,
          lowStockThreshold: p.lowStockThreshold,
          restockTo: p.restockTo,
          unit: p.unit ?? 'unit',
          categoryId: p.categoryId ?? '',
          supplierId: p.supplierId ?? '',
          isActive: p.isActive
        };
      });
    }
  }

  save(): void {
    if (!this.form.name.trim()) {
      this.error = 'Name is required.';
      return;
    }
    if (this.form.lowStockThreshold < 0) {
      this.error = 'Low Stock Threshold cannot be negative.';
      return;
    }
    if (this.form.restockTo < 0) {
      this.error = 'Restock To cannot be negative.';
      return;
    }
    if (this.form.restockTo < this.form.lowStockThreshold) {
      this.error = 'Restock To must be greater than or equal to the Low Stock Threshold.';
      return;
    }
    this.error = '';
    this.saving = true;

    const base = {
      name: this.form.name.trim(),
      sku: this.form.sku || null,
      description: this.form.description || null,
      unitPrice: this.form.unitPrice,
      lowStockThreshold: this.form.lowStockThreshold,
      restockTo: this.form.restockTo,
      unit: this.form.unit || null,
      categoryId: this.form.categoryId === '' ? null : this.form.categoryId,
      supplierId: this.form.supplierId === '' ? null : this.form.supplierId,
      isActive: this.form.isActive
    };

    if (this.productId) {
      this.productService.update(this.productId, base).subscribe({
        next: () => this.router.navigate(['/products']),
        error: () => { this.error = 'Failed to update product.'; this.saving = false; }
      });
    }
  }
}
