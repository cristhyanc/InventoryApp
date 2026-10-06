import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { PurchaseService } from '../../services/purchase.service';
import { SupplierService } from '../../services/supplier.service';
import {
  GstClassification,
  Product,
  Purchase,
  PurchaseGstSummary,
  Supplier,
  PurchaseValidation
} from '../../models/models';
import { ProductService } from '../../services/product.service';
import { PurchaseItemPayload } from '../../services/purchase.service';
import { ObjectUrlCache } from '../shared/object-url-cache';
import { GST_CLASSIFICATION_OPTIONS, gstClassificationLabel, isChargePresent } from './gst-classification-options';

/**
 * One line of the purchase edit form (issue #431).
 *
 * `id` is the stored line's own stable identity and is what keeps each line's classification on the
 * right line when a purchase holds several lines for one product; it is `undefined` only for a line
 * added during the edit. `storedGstClassification` is the classification the purchase was read with,
 * which is how the form knows whether the person actually changed it: an unchanged classification is
 * left out of the request so the server keeps it and its provenance.
 */
interface EditPurchaseItem {
  id?: number;
  productId: number;
  quantity: number;
  unitCost: number;
  gstClassification: GstClassification;
  storedGstClassification: GstClassification | null;
}

@Component({
  selector: 'app-purchase-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './purchase-list.component.html'
})
export class PurchaseListComponent implements OnInit, OnDestroy {
  purchases: Purchase[] = [];
  suppliers: Supplier[] = [];
  products: Product[] = [];
  editItems: EditPurchaseItem[] = [];
  editingPurchaseId: number | null = null;

  /** Why the last save of the open edit form was refused, shown in the form itself. */
  editError = '';

  /** The GST picker's options, shared with the purchase entry form. */
  readonly gstOptions = GST_CLASSIFICATION_OPTIONS;

  editForm = {
    title: '',
    notes: '',
    totalAmount: null as number | null,
    deliveryCost: null as number | null,
    deliveryGstClassification: GstClassification.Unknown,
    packageCost: null as number | null,
    packageGstClassification: GstClassification.Unknown,
    purchaseDate: '',
    supplierId: '' as number | ''
  };

  /**
   * The charge classifications the edited purchase was read with, kept out of `editForm` because
   * they are never bound to a control: they exist only to tell a changed selection from an untouched
   * one.
   */
  private storedChargeGst = {
    delivery: GstClassification.Unknown,
    package: GstClassification.Unknown
  };

  // Purchase documents are protected by the API, so they are fetched through HttpClient (which
  // attaches the bearer token) and rendered from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();
  private thumbnailUrls = new Map<number, string>();

  constructor(
    private purchaseService: PurchaseService,
    private supplierService: SupplierService,
    private productService: ProductService
  ) {}

  ngOnInit(): void {
    this.load();
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
    this.productService.getAll().subscribe((p) => (this.products = p));
  }

  ngOnDestroy(): void {
    this.objectUrls.releaseAll();
  }

  load(): void {
    this.purchaseService.getAll().subscribe((r) => {
      this.purchases = r;
      this.loadThumbnails();
    });
  }

  getValidation(purchase: Purchase): PurchaseValidation | null {
    return this.purchaseService.getValidationFor(purchase.id);
  }

  /**
   * The purchase's input GST exactly as the API calculated it. Nothing here derives GST from an
   * amount: a `null` summary is shown as unavailable rather than as `$0`.
   */
  gstSummary(purchase: Purchase): PurchaseGstSummary | null {
    return this.purchaseService.getGstSummaryFor(purchase.id);
  }

  /**
   * Whether the figures on display describe a purchase that is currently being edited. The summary
   * is the saved one, so while the form is open it cannot be presented as covering unsaved changes.
   */
  gstSummaryExcludesUnsavedEdits(purchase: Purchase): boolean {
    return this.editingPurchaseId === purchase.id;
  }

  /** How one stored component's classification is named. */
  classificationLabel(value: GstClassification | null | undefined): string {
    return gstClassificationLabel(value);
  }

  /** Whether a delivery or package charge exists at all; an absent charge has no classification. */
  hasCharge(amount: number | null | undefined): boolean {
    return isChargePresent(amount);
  }

  thumbnailUrl(purchase: Purchase): string | null {
    return this.thumbnailUrls.get(purchase.id) ?? null;
  }

  openDocument(purchase: Purchase): void {
    this.purchaseService.getFile(purchase.id).subscribe({
      next: (blob) => window.open(this.objectUrls.create(blob), '_blank', 'noopener'),
      error: (err) => console.error('Failed to open purchase document', err)
    });
  }

  private loadThumbnails(): void {
    this.objectUrls.releaseAll();
    this.thumbnailUrls = new Map<number, string>();
    this.purchases
      .filter((purchase) => this.isImage(purchase))
      .forEach((purchase) =>
        this.purchaseService.getFile(purchase.id).subscribe({
          next: (blob) => this.thumbnailUrls.set(purchase.id, this.objectUrls.create(blob)),
          error: (err) => console.error('Failed to load purchase document', err)
        })
      );
  }

  isImage(purchase: Purchase): boolean {
    return purchase.contentType.startsWith('image/');
  }

  startEdit(purchase: Purchase): void {
    this.editingPurchaseId = purchase.id;
    this.editError = '';
    const delivery = purchase.deliveryGstClassification ?? GstClassification.Unknown;
    const packageCharge = purchase.packageGstClassification ?? GstClassification.Unknown;
    this.editForm = {
      title: purchase.title,
      notes: purchase.notes ?? '',
      totalAmount: purchase.totalAmount ?? null,
      deliveryCost: purchase.deliveryCost ?? null,
      deliveryGstClassification: delivery,
      packageCost: purchase.packageCost ?? null,
      packageGstClassification: packageCharge,
      purchaseDate: purchase.purchaseDate ? this.localDate(new Date(purchase.purchaseDate)) : '',
      supplierId: purchase.supplierId ?? ''
    };
    this.storedChargeGst = { delivery, package: packageCharge };
    this.editItems = (purchase.items ?? []).map(item => ({
      id: item.id,
      productId: item.productId,
      quantity: item.quantity,
      unitCost: item.unitCost,
      gstClassification: item.gstClassification ?? GstClassification.Unknown,
      storedGstClassification: item.gstClassification ?? GstClassification.Unknown
    }));
  }

  cancelEdit(): void {
    this.editingPurchaseId = null;
    this.editError = '';
  }

  saveEdit(purchase: Purchase): void {
    if (!this.editForm.title.trim()) return;
    this.editError = '';

    this.purchaseService
      .update(purchase.id, {
        title: this.editForm.title.trim(),
        notes: this.editForm.notes || null,
        totalAmount: this.editForm.totalAmount,
        deliveryCost: this.editForm.deliveryCost,
        deliveryGstClassification: this.chargeClassificationToSubmit(
          this.editForm.deliveryCost, this.editForm.deliveryGstClassification, this.storedChargeGst.delivery),
        packageCost: this.editForm.packageCost,
        packageGstClassification: this.chargeClassificationToSubmit(
          this.editForm.packageCost, this.editForm.packageGstClassification, this.storedChargeGst.package),
        purchaseDate: this.editPurchaseTimestamp(purchase),
        supplierId: this.editForm.supplierId === '' ? null : this.editForm.supplierId
        , items: this.editItems.map(item => this.toItemPayload(item))
      })
      .subscribe({
        next: () => {
          this.editingPurchaseId = null;
          this.load();
        },
        error: (err) => {
          // The edit stays open with the person's selections intact, and says why the save failed.
          // A rejected GST classification must never look like a saved one.
          this.editError = typeof err?.error === 'string' && err.error
            ? err.error
            : 'Failed to save the purchase.';
          console.error('Failed to update purchase', err);
        }
      });
  }

  addEditItem(): void {
    this.editItems.push({
      productId: this.products[0]?.id ?? 0,
      quantity: 1,
      unitCost: 0,
      gstClassification: GstClassification.Unknown,
      storedGstClassification: null
    });
  }
  removeEditItem(index: number): void { this.editItems.splice(index, 1); }
  editLineTotal(item: EditPurchaseItem): number { return Number(item.quantity || 0) * Number(item.unitCost || 0); }
  get editItemsSubtotal(): number { return this.editItems.reduce((sum, item) => sum + this.editLineTotal(item), 0); }

  remove(purchase: Purchase): void {
    if (!confirm(`Delete purchase "${purchase.title}"?`)) return;
    this.purchaseService.delete(purchase.id).subscribe(() => this.load());
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  /**
   * One edited line as the request carries it. The stored line's `id` travels with it, so a
   * reordered or partly removed set of duplicate-product lines still names each line by its own
   * identity rather than by its product or its position, and a line added during the edit has no id
   * at all.
   */
  private toItemPayload(item: EditPurchaseItem): PurchaseItemPayload {
    return {
      id: item.id,
      productId: item.productId,
      quantity: item.quantity,
      unitCost: item.unitCost,
      gstClassification: PurchaseListComponent.changedClassification(
        item.gstClassification, item.storedGstClassification)
    };
  }

  /**
   * The classification to submit for a delivery or package charge: nothing for a charge that no
   * longer has a value - the server clears an absent charge's classification itself - and otherwise
   * only a changed selection.
   */
  private chargeClassificationToSubmit(
    amount: number | null,
    selected: GstClassification,
    stored: GstClassification): GstClassification | undefined {
    return isChargePresent(amount)
      ? PurchaseListComponent.changedClassification(selected, stored)
      : undefined;
  }

  /**
   * What an edit submits for one component: the selection when the person changed it, and
   * `undefined` - omitted from the request entirely - when they did not.
   *
   * Omission is what preserves provenance: a classification a product rule or a supplier default
   * established keeps that provenance through an edit of a quantity, a cost or a date, because the
   * server is never told about it again. An explicit move back to Not classified is a change like any
   * other and is submitted, so a person can withdraw a classification deliberately.
   */
  private static changedClassification(
    selected: GstClassification, stored: GstClassification | null): GstClassification | undefined {
    if (stored === null) {
      return selected === GstClassification.Unknown ? undefined : selected;
    }
    return selected === stored ? undefined : selected;
  }

  private editPurchaseTimestamp(purchase: Purchase): string | null {
    if (!this.editForm.purchaseDate) return null;
    const original = new Date(purchase.purchaseDate);
    if (this.editForm.purchaseDate === this.localDate(original))
      return original.toISOString();
    const selected = new Date(`${this.editForm.purchaseDate}T00:00:00`);
    const now = new Date();
    if (this.editForm.purchaseDate === this.localDate(now))
      return now.toISOString();
    return selected.toISOString();
  }

  private localDate(value: Date): string {
    const year = value.getFullYear();
    const month = String(value.getMonth() + 1).padStart(2, '0');
    const day = String(value.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
