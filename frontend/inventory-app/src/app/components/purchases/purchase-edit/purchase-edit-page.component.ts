import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { Product, Purchase, Supplier } from '../../../models/models';
import { PurchaseService, PurchaseUpdatePayload } from '../../../services/purchase.service';
import { SupplierService } from '../../../services/supplier.service';
import { ProductService } from '../../../services/product.service';
import { PurchaseEditFormComponent } from './purchase-edit-form.component';

/**
 * The dedicated Edit purchase page (issue #475), reached at `/purchases/:id/edit`. Editing a
 * purchase used to expand a form inside the purchases table; that inline editor is gone, and this
 * routed page replaces it.
 *
 * It is the composition boundary described in `docs/architecture.md` § Page composition boundary: it
 * resolves the route id, loads the purchase through the existing authorized `PurchaseService.get`
 * (so a bookmarked URL and a refresh work without any list component instance or navigation-only
 * state), loads the suppliers and products the form offers, owns the loading / unavailable /
 * save-error states and the save request, and navigates back to the list. The form itself is
 * `PurchaseEditFormComponent`.
 *
 * Permissions and tenant isolation are the API's, unchanged: `GET`/`PUT /api/purchases/{id}` are
 * `[Authorize]`d and scoped by the `AppDbContext` tenant filters, so another business's id reaches
 * this page's unavailable state rather than its data, and nothing here sends an owner.
 */
@Component({
  selector: 'app-purchase-edit-page',
  standalone: true,
  imports: [CommonModule, RouterLink, PurchaseEditFormComponent],
  templateUrl: './purchase-edit-page.component.html'
})
export class PurchaseEditPageComponent implements OnInit, OnDestroy {
  purchase: Purchase | null = null;
  suppliers: Supplier[] = [];
  products: Product[] = [];

  loading = true;

  /** Why no form is shown: an unusable id, a purchase this business cannot see, or a failed read. */
  unavailable = '';

  saving = false;

  /** Why the last save was refused. The form keeps the person's entered values while it shows. */
  saveError = '';

  private purchaseId: number | null = null;
  private readonly subscriptions = new Subscription();

  constructor(
    private purchaseService: PurchaseService,
    private supplierService: SupplierService,
    private productService: ProductService,
    private router: Router,
    private route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    this.subscriptions.add(this.supplierService.getAll().subscribe((s) => (this.suppliers = s)));
    this.subscriptions.add(this.productService.getAll().subscribe((p) => (this.products = p)));

    // paramMap rather than a snapshot: the router reuses this component between two edit URLs, so
    // browser Back and Forward must reload the purchase the current URL names.
    this.subscriptions.add(this.route.paramMap.subscribe((params) => this.load(params.get('id'))));
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
  }

  onSave(payload: PurchaseUpdatePayload): void {
    if (this.saving || this.purchaseId === null) return;
    this.saving = true;
    this.saveError = '';

    this.purchaseService.update(this.purchaseId, payload).subscribe({
      next: () => {
        this.saving = false;
        this.returnToList();
      },
      error: (err) => {
        // The page stays open with the form's values intact and says why the save failed. A
        // rejected GST classification must never look like a saved one.
        this.saving = false;
        this.saveError = typeof err?.error === 'string' && err.error ? err.error : 'Failed to save the purchase.';
        console.error('Failed to update purchase', err);
      }
    });
  }

  onCancel(): void {
    this.returnToList();
  }

  private load(idParam: string | null): void {
    const id = Number(idParam);
    this.purchase = null;
    this.saveError = '';
    if (!idParam || !Number.isInteger(id) || id <= 0) {
      this.loading = false;
      this.purchaseId = null;
      this.unavailable = 'That purchase address is not valid.';
      return;
    }

    this.purchaseId = id;
    this.loading = true;
    this.unavailable = '';
    this.purchaseService.get(id).subscribe({
      next: (purchase) => {
        this.purchase = purchase;
        this.loading = false;
      },
      error: (err) => {
        this.loading = false;
        this.unavailable =
          err?.status === 404
            ? 'This purchase is no longer available.'
            : 'Failed to load the purchase. Please try again.';
        console.error('Failed to load purchase', err);
      }
    });
  }

  /**
   * Back to the purchases list. The list holds no filter, sort or paging state to preserve, so
   * there is no navigation state or query parameter to carry: `/purchases` is the one reliable
   * destination for a Save, a Cancel and a direct URL entry alike.
   */
  private returnToList(): void {
    this.router.navigate(['/purchases']);
  }
}
