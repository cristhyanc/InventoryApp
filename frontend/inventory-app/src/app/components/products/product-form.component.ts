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
  isEdit = false;
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
    quantityInStock: 0,
    lowStockThreshold: 5,
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
      this.isEdit = true;
      this.productId = Number(idParam);
      this.productService.get(this.productId).subscribe((p) => {
        this.form = {
          name: p.name,
          sku: p.sku ?? '',
          description: p.description ?? '',
          unitPrice: p.unitPrice,
          quantityInStock: p.quantityInStock,
          lowStockThreshold: p.lowStockThreshold,
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
    this.error = '';
    this.saving = true;

    const base = {
      name: this.form.name.trim(),
      sku: this.form.sku || null,
      description: this.form.description || null,
      unitPrice: this.form.unitPrice,
      lowStockThreshold: this.form.lowStockThreshold,
      unit: this.form.unit || null,
      categoryId: this.form.categoryId === '' ? null : this.form.categoryId,
      supplierId: this.form.supplierId === '' ? null : this.form.supplierId,
      isActive: this.form.isActive
    };

    if (this.isEdit && this.productId) {
      this.productService.update(this.productId, base).subscribe({
        next: () => this.router.navigate(['/products']),
        error: () => { this.error = 'Failed to update product.'; this.saving = false; }
      });
    } else {
      this.productService
        .create({ ...base, quantityInStock: this.form.quantityInStock })
        .subscribe({
          next: () => this.router.navigate(['/products']),
          error: () => { this.error = 'Failed to create product.'; this.saving = false; }
        });
    }
  }
}
