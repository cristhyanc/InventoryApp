import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ProductService } from '../../services/product.service';
import { CategoryService } from '../../services/category.service';
import { Category, GstClassification } from '../../models/models';
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
   * The GST rule chosen for a product being created (issue #829), reported by the deferred
   * `app-product-gst-rule` panel. `Unknown` is `GstRules.None` - no rule - which is also what a
   * created product carries, so it is the honest starting value here.
   *
   * It is unused on the edit page: there the panel reads and saves the stored rule on its own
   * resource, as it has since issue #430.
   */
  gstRule: GstClassification = GstClassification.Unknown;

  /**
   * The id of the product this page created while its GST rule is still outstanding (issue #829).
   *
   * Creating a product with a GST rule is two writes - `POST api/products`, then
   * `PUT api/products/{id}/gst-rule` on the id it answered - because the rule is its own resource
   * and the product payload is a pinned API contract (docs/architecture.md § Product and supplier
   * GST rules). When the second one fails the product exists, so this page reports exactly that,
   * keeps the id, and retries the rule alone: a retry must never create a second product, and a
   * create whose rule was not stored must never be reported as finished.
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
    if (this.createdProductId !== null) {
      return 'Save GST Rule';
    }

    return this.productId ? 'Update Product' : 'Create Product';
  }

  /** Whether the page's one submit control is unavailable. */
  get submitDisabled(): boolean {
    if (this.saving || this.sessionActive) {
      return true;
    }

    // Only the outstanding GST rule is left to write (issue #829). The product is already stored
    // and these fields are no longer being sent, so their validity must not trap the rule here.
    if (this.createdProductId !== null) {
      return false;
    }

    return this.form.restockTo < this.form.lowStockThreshold;
  }

  save(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
    // The product exists and only its GST rule is outstanding (issue #829): retry that one write,
    // with whatever rule is on screen now. Creating the product again is exactly the duplicate
    // this branch exists to prevent, so no create validation runs and no create is issued.
    if (this.createdProductId !== null) {
      this.saveGstRuleOfCreatedProduct(this.createdProductId);
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
          // A created product already carries `GstRules.None`, so "no rule" is stored by the
          // create itself and needs no second write - and must not be sent as anything else.
          if (this.gstRule === GstClassification.Unknown) {
            this.router.navigate(['/products']);
            return;
          }

          this.createdProductId = created.id;
          this.saveGstRuleOfCreatedProduct(created.id);
        },
        error: (error: unknown) => {
          this.error = apiErrorMessage(error, 'Failed to create product.');
          this.saving = false;
        }
      });
    }
  }

  /**
   * Writes the chosen GST rule to the product that has just been created (issue #829). The product
   * is already stored, so a failure here is a partial outcome and is reported as one: the page stays
   * open, says the product exists with its rule unsaved, and offers the retry of this write alone.
   */
  private saveGstRuleOfCreatedProduct(id: number): void {
    this.saving = true;
    this.error = '';
    this.productService.setGstRule(id, this.gstRule).subscribe({
      next: () => {
        this.createdProductId = null;
        this.router.navigate(['/products']);
      },
      error: () => {
        this.saving = false;
        this.error =
          'The product was created, but its GST rule was not saved. Press Save GST Rule to retry; ' +
          'it saves the rule only and never creates a second product. Other changes on this form ' +
          'are not saved again - edit the product from the Products list instead.';
      }
    });
  }
}
