import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { Product, Purchase, PurchaseGstSummary, Supplier } from '../../../models/models';
import { PurchaseService, PurchaseUpdatePayload } from '../../../services/purchase.service';
import { SupplierService } from '../../../services/supplier.service';
import { ProductService } from '../../../services/product.service';
import { PurchaseEditFormComponent } from './purchase-edit-form.component';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { ToastService } from '../../../services/toast.service';
import { ObjectUrlCache } from '../../shared/object-url-cache';

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
 * A receipt chosen during the edit, for a purchase that has none, is attached through the existing
 * `POST /api/purchases/{id}/file` only after the edit itself has saved (issue #1029): the update and
 * the attach stay the two existing requests, and an attach the API refuses never undoes the saved
 * edit. A receipt the purchase already holds is never replaced or deleted from here.
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

  /**
   * Whether a support session is active (issue #657, extending #554), passed down to
   * `PurchaseEditFormComponent` so it can disable Save and guard its own submit handler. See
   * `SupportSessionService.active$`.
   */
  sessionActive = false;

  /** The saved purchase's input GST from the read, shown in the form's summary as last saved. */
  gstSummary: PurchaseGstSummary | null = null;

  /** A receipt chosen during this edit, attached once the edit has saved. */
  pendingReceipt: File | null = null;

  private purchaseId: number | null = null;
  private readonly subscriptions = new Subscription();
  private readonly objectUrls = new ObjectUrlCache();

  constructor(
    private readonly purchaseService: PurchaseService,
    private readonly supplierService: SupplierService,
    private readonly productService: ProductService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
    supportSession: SupportSessionService,
    private readonly toastService: ToastService
  ) {
    this.subscriptions.add(supportSession.active$.subscribe((active) => (this.sessionActive = active)));
  }

  ngOnInit(): void {
    this.subscriptions.add(this.supplierService.getAll().subscribe((s) => (this.suppliers = s)));
    this.subscriptions.add(this.productService.getAll().subscribe((p) => (this.products = p)));

    // paramMap rather than a snapshot: the router reuses this component between two edit URLs, so
    // browser Back and Forward must reload the purchase the current URL names.
    this.subscriptions.add(this.route.paramMap.subscribe((params) => this.load(params.get('id'))));
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
    this.objectUrls.releaseAll();
  }

  onSave(payload: PurchaseUpdatePayload): void {
    // Checked here as well as in the form it is passed to, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (this.saving || this.purchaseId === null) return;
    this.saving = true;
    this.saveError = '';

    const purchaseId = this.purchaseId;
    this.purchaseService.update(purchaseId, payload).subscribe({
      next: () => {
        if (this.pendingReceipt) {
          this.attachReceipt(purchaseId, this.pendingReceipt);
          return;
        }
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

  /** Opens the purchase's saved receipt, fetched through the authorized API as the list does. */
  previewStoredReceipt(): void {
    if (this.purchaseId === null) return;
    this.purchaseService.getFile(this.purchaseId).subscribe({
      next: (blob) => window.open(this.objectUrls.create(blob), '_blank', 'noopener'),
      error: (err) => {
        this.toastService.error('Could not open the receipt. Try again.');
        console.error('Failed to open purchase document', err);
      }
    });
  }

  /**
   * The edit is saved, so the chosen receipt goes through the existing attach endpoint, which keeps
   * every size, type and storage rule it already enforced. A refusal is reported, not hidden, and
   * the saved edit stands: the list offers the attach again for a purchase that still has none.
   */
  private attachReceipt(purchaseId: number, file: File): void {
    this.purchaseService.attachDocument(purchaseId, file).subscribe({
      next: () => {
        this.saving = false;
        this.pendingReceipt = null;
        this.returnToList();
      },
      error: (failure: HttpErrorResponse) => {
        this.saving = false;
        const reason = typeof failure?.error === 'string' && failure.error.trim() ? ` ${failure.error.trim()}` : '';
        this.toastService.error(
          `The purchase was saved, but the receipt was not attached.${reason} You can add it from the Purchases list.`);
        this.returnToList();
      }
    });
  }

  private load(idParam: string | null): void {
    const id = Number(idParam);
    this.purchase = null;
    this.saveError = '';
    this.gstSummary = null;
    this.pendingReceipt = null;
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
        this.gstSummary = this.purchaseService.getGstSummaryFor(id);
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
