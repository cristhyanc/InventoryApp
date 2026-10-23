import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { Category } from '../../models/models';
import { ProductPriceHistoryComponent } from './product-price-history/product-price-history.component';
import { ProductGstRuleComponent } from './product-gst-rule/product-gst-rule.component';
import { ProductCreateGstRuleComponent } from './product-create-gst-rule/product-create-gst-rule.component';
import { SupportSessionService } from '../../support-session/support-session.service';
import { apiErrorMessage } from '../shared/api-error-message';

@Component({
  selector: 'app-product-form',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    ProductPriceHistoryComponent,
    ProductGstRuleComponent,
    ProductCreateGstRuleComponent
  ],
  templateUrl: './product-form.component.html'
})
export class ProductFormComponent implements OnInit, OnDestroy {
  productId: number | null = null;
  categories: Category[] = [];
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

  /**
   * Why this product may not be deleted, for the notice under the page header (issue #825), or
   * `null` when the loaded product may be deleted and on New Product.
   *
   * It is the server's own refusal, verbatim - `GET api/products/{id}`'s `deletionRefusal`, decided
   * by the one `Inventory.Domain.Products.ProductDeletionPolicy` (issue #737) - so this page never
   * words the deletion rule a second time. It is informational: it gates no field, no Save and no
   * other control, and it is shown for an active and an inactive product alike and during a
   * support session, because it explains the product rather than offering an action.
   */
  deletionNotice: string | null = null;

  /**
   * The id of the product this page created, while the rule chosen for it is still being stored
   * (issue #829), or `null` while no product has been created on this page.
   *
   * Creating a product with a GST rule is two writes - `POST api/products`, then
   * `PUT api/products/{id}/gst-rule` on the id it answered - because the rule is its own resource
   * and the product payload is a pinned API contract (docs/architecture.md § Product and supplier
   * GST rules). This page makes the first one and hands the id to
   * `app-product-create-gst-rule`, which owns the second one, its failure and its retry. The id
   * stays here because this page created the product: once it is set, the page offers no create
   * control at all, which is what makes a duplicate product impossible.
   */
  createdProductId: number | null = null;

  private readonly sessionActiveSubscription: Subscription;

  form = {
    name: '',
    sku: '',
    description: '',
    quantityInStock: 0,
    initialUnitCost: null as number | null,
    lowStockThreshold: 5,
    restockTo: 5,
    unit: 'unit',
    categoryId: '' as number | '',
    isActive: true
  };

  constructor(
    private readonly productService: ProductService,
    private readonly categoryService: CategoryService,
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
    // No supplier list is loaded (issue #787, A1 of #786): nothing on this page needs one now that
    // a product carries no supplier.
    this.categoryService.getAll().subscribe((c) => (this.categories = c));

    const idParam = this.route.snapshot.paramMap.get('id');
    if (idParam) {
      this.productId = Number(idParam);
      this.productService.get(this.productId).subscribe((p) => {
        this.providerManagedFields = p.providerManagedFields ?? [];
        // Anything but a plain `true` means a delete would be refused: `false` says so, and a
        // response that did not resolve the question is not permission either (issue #737).
        const refusal = p.deletionRefusal?.trim();
        if (p.canDelete === true) {
          this.deletionNotice = null;
        } else {
          this.deletionNotice = refusal || 'This product cannot be deleted.';
        }
        this.form = {
          name: p.name,
          sku: p.sku ?? '',
          description: p.description ?? '',
          quantityInStock: p.quantityInStock,
          initialUnitCost: null,
          lowStockThreshold: p.lowStockThreshold,
          restockTo: p.restockTo,
          unit: p.unit ?? 'unit',
          categoryId: p.categoryId ?? '',
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

  /** The label of the page's one submit control, which names the write it would actually make. */
  get submitLabel(): string {
    return this.productId ? 'Update Product' : 'Create Product';
  }

  /** Whether the page's one submit control is unavailable. */
  get submitDisabled(): boolean {
    if (this.saving || this.sessionActive) {
      return true;
    }

    return this.form.restockTo < this.form.lowStockThreshold;
  }

  save(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
    // This page has already created its product (issue #829), so it has no write left to make:
    // what is outstanding is the GST rule, which `app-product-create-gst-rule` owns and retries.
    // Creating the product a second time is exactly the duplicate this guard prevents.
    if (this.createdProductId !== null) {
      return;
    }
    if (!this.form.name.trim()) {
      this.error = 'Product name is required.';
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
      lowStockThreshold: this.form.lowStockThreshold,
      restockTo: this.form.restockTo,
      unit: this.form.unit || null,
      categoryId: this.form.categoryId === '' ? null : this.form.categoryId,
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
        next: (created) => {
          // The product exists from here on. Its GST rule is `app-product-create-gst-rule`'s to
          // store (issue #829), so the page stays in its saving state and finishes on that
          // component's report rather than treating the create alone as the whole outcome.
          this.createdProductId = created.id;
        },
        error: (error: unknown) => {
          this.error = apiErrorMessage(error, 'Failed to create product.');
          this.saving = false;
        }
      });
    }
  }

  /**
   * The created product's GST rule is stored, or needed no write because "no rule" is what a new
   * product already carries (issue #829). Only now is the create finished.
   */
  onGstRulePersisted(): void {
    this.saving = false;
    this.router.navigate(['/products']);
  }

  /**
   * The product was created and its GST rule was not stored (issue #829). The page stays open and
   * does not navigate - a create whose rule was not stored is not a finished create - and says
   * nothing further itself: the component that owns that write reports it and offers the retry.
   */
  onGstRuleFailed(): void {
    this.saving = false;
  }
}
