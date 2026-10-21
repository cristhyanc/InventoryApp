import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { SupplierService } from '../../services/supplier.service';
import { Category, Supplier } from '../../models/models';
import { ProductPriceHistoryComponent } from './product-price-history/product-price-history.component';
import { ProductGstRuleComponent } from './product-gst-rule/product-gst-rule.component';
import { SupportSessionService } from '../../support-session/support-session.service';
import { apiErrorMessage } from '../shared/api-error-message';

@Component({
  selector: 'app-product-form',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, ProductPriceHistoryComponent, ProductGstRuleComponent],
  templateUrl: './product-form.component.html'
})
export class ProductFormComponent implements OnInit, OnDestroy {
  productId: number | null = null;
  categories: Category[] = [];
  suppliers: Supplier[] = [];
  saving = false;
  error = '';

  /**
   * Whether a support session is active (issue #554): Save is disabled while it is, because a
   * session grants `InventoryOperate` - the capability this page's write would otherwise be gated
   * on - and the backend refuses the write itself only by its read-only method rule, never by
   * withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  /**
   * The fields this business's sales provider manages on the loaded product (issue #737):
   * `["name", "categoryId"]` for a product the provider named, empty for a new product or one this
   * business maintains itself. See `Inventory.Domain.Providers.ProviderManagedCatalogueFields`.
   */
  providerManagedFields: string[] = [];

  private readonly sessionActiveSubscription: Subscription;

  form = {
    name: '',
    sku: '',
    description: '',
    unitPrice: 0,
    quantityInStock: 0,
    initialUnitCost: null as number | null,
    lowStockThreshold: 5,
    restockTo: 5,
    unit: 'unit',
    categoryId: '' as number | '',
    supplierId: '' as number | '',
    isActive: true
  };

  constructor(
    private readonly productService: ProductService,
    private readonly categoryService: CategoryService,
    private readonly supplierService: SupplierService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  ngOnInit(): void {
    this.categoryService.getAll().subscribe((c) => (this.categories = c));
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));

    const idParam = this.route.snapshot.paramMap.get('id');
    if (idParam) {
      this.productId = Number(idParam);
      this.productService.get(this.productId).subscribe((p) => {
        this.providerManagedFields = p.providerManagedFields ?? [];
        this.form = {
          name: p.name,
          sku: p.sku ?? '',
          description: p.description ?? '',
          unitPrice: p.unitPrice,
          quantityInStock: p.quantityInStock,
          initialUnitCost: null,
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

  /** Whether the provider names this product, so its name may not be edited here (issue #737). */
  get nameLocked(): boolean {
    return this.providerManagedFields.includes('name');
  }

  /** Whether the provider names this product's category, so it may not be changed here (issue #737). */
  get categoryLocked(): boolean {
    return this.providerManagedFields.includes('categoryId');
  }

  /** The note explaining which fields are locked, or `null` while nothing here is provider-managed. */
  get providerManagedNote(): string | null {
    if (this.nameLocked && this.categoryLocked) {
      return 'Name and category are managed by Nayax and cannot be edited here.';
    }
    if (this.nameLocked) {
      return 'Name is managed by Nayax and cannot be edited here.';
    }
    if (this.categoryLocked) {
      return 'Category is managed by Nayax and cannot be edited here.';
    }
    return null;
  }

  save(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
    if (!this.form.name.trim()) {
      this.error = 'Product name is required.';
      return;
    }
    if (this.form.unitPrice < 0) {
      this.error = 'Unit price cannot be negative.';
      return;
    }
    if (!this.productId && this.form.initialUnitCost !== null && this.form.initialUnitCost < 0) {
      this.error = 'Initial unit cost cannot be negative.';
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
        error: (error: unknown) => {
          this.error = apiErrorMessage(error, 'Failed to update product.');
          this.saving = false;
        }
      });
    } else {
      this.productService.create({
        ...base,
        quantityInStock: this.form.quantityInStock,
        initialUnitCost: this.form.initialUnitCost
      }).subscribe({
        next: () => this.router.navigate(['/products']),
        error: (error: unknown) => {
          this.error = apiErrorMessage(error, 'Failed to create product.');
          this.saving = false;
        }
      });
    }
  }
}
