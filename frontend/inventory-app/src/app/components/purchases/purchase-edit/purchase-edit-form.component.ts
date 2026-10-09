import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { GstClassification, Product, Purchase, Supplier } from '../../../models/models';
import { PurchaseItemPayload, PurchaseUpdatePayload } from '../../../services/purchase.service';
import { IconComponent } from '../../shared/icon.component';
import { GST_CLASSIFICATION_OPTIONS, isChargePresent } from '../gst-classification-options';

/**
 * One line of the purchase edit form (issue #431).
 *
 * `id` is the stored line's own stable identity and is what keeps each line's classification on the
 * right line when a purchase holds several lines for one product; it is `undefined` only for a line
 * added during the edit. `storedGstClassification` is the classification the purchase was read with,
 * which is how the form knows whether the person actually changed it: an unchanged classification is
 * left out of the request so the server keeps it and its provenance.
 */
export interface EditPurchaseItem {
  id?: number;
  productId: number;
  quantity: number;
  unitCost: number;
  gstClassification: GstClassification;
  storedGstClassification: GstClassification | null;
}

/**
 * The purchase edit form (issue #475), extracted unchanged from the row the purchases list used to
 * expand inline. It is a feature component, not a page: `PurchaseEditPageComponent` resolves the
 * route id, loads the purchase, suppliers and products, owns the save lifecycle and does the
 * navigation, which is what `docs/architecture.md` § Page composition boundary asks of a routed
 * page. This component owns only the form's own state, performs no GST arithmetic and makes no
 * request of its own - it emits the payload `PurchaseService.update` already accepted.
 */
@Component({
  selector: 'app-purchase-edit-form',
  standalone: true,
  imports: [CommonModule, FormsModule, IconComponent],
  templateUrl: './purchase-edit-form.component.html'
})
export class PurchaseEditFormComponent {
  /**
   * The purchase being edited. Setting it (re)opens the form on that purchase's stored values, so a
   * route id change loads the new purchase rather than leaving the previous one's values on screen.
   */
  @Input({ required: true })
  set purchase(value: Purchase) {
    this.open(value);
  }

  @Input() suppliers: Supplier[] = [];
  @Input() products: Product[] = [];

  /** True while the page's save request is in flight; disables Save so it cannot be pressed twice. */
  @Input() saving = false;

  /** Why the page's last save was refused. The form keeps the entered values beneath it. */
  @Input() error = '';

  /** The edited values, for the page to send through the existing update API. */
  @Output() readonly saveRequested = new EventEmitter<PurchaseUpdatePayload>();

  /** The person left without saving; the page decides where that returns to. */
  @Output() readonly editCancelled = new EventEmitter<void>();

  editItems: EditPurchaseItem[] = [];

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

  /** The purchase the form opened on, kept for its stored purchase instant and nothing else. */
  private source: Purchase | null = null;

  /**
   * The charge classifications the edited purchase was read with, kept out of `editForm` because
   * they are never bound to a control: they exist only to tell a changed selection from an untouched
   * one.
   */
  private storedChargeGst = {
    delivery: GstClassification.Unknown,
    package: GstClassification.Unknown
  };

  /** Whether a delivery or package charge exists at all; an absent charge has no classification. */
  hasCharge(amount: number | null | undefined): boolean {
    return isChargePresent(amount);
  }

  /**
   * Whether this edit line is one the purchase already holds, rather than one added in the form.
   *
   * A stored line is submitted with its own id, and the server refuses to re-point an identified
   * line at a different product (`PurchaseLineIdentityPolicy.ProductChangedMessage`): that would move
   * the line's GST classification, its provenance and its restock movement onto another product's
   * costing history. So the form does not offer to change it; replacing a product is removing the
   * line and adding the new product as a line of its own, which is a new line with no id and no
   * inherited classification.
   */
  isStoredLine(item: EditPurchaseItem): boolean {
    return item.id !== undefined && item.id !== null;
  }

  /** How a stored edit line names its product, which the form shows instead of a picker. */
  productName(productId: number): string {
    return this.products.find((p) => p.id === productId)?.name ?? `Product ${productId}`;
  }

  submit(): void {
    if (this.saving) return;
    if (!this.editForm.title.trim()) return;

    this.saveRequested.emit({
      title: this.editForm.title.trim(),
      notes: this.editForm.notes || null,
      totalAmount: this.editForm.totalAmount,
      deliveryCost: this.editForm.deliveryCost,
      deliveryGstClassification: this.chargeClassificationToSubmit(
        this.editForm.deliveryCost, this.editForm.deliveryGstClassification, this.storedChargeGst.delivery),
      packageCost: this.editForm.packageCost,
      packageGstClassification: this.chargeClassificationToSubmit(
        this.editForm.packageCost, this.editForm.packageGstClassification, this.storedChargeGst.package),
      purchaseDate: this.editPurchaseTimestamp(),
      supplierId: this.editForm.supplierId === '' ? null : this.editForm.supplierId,
      items: this.editItems.map(item => this.toItemPayload(item))
    });
  }

  cancelEdit(): void {
    this.editCancelled.emit();
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

  private open(purchase: Purchase): void {
    this.source = purchase;
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
      gstClassification: PurchaseEditFormComponent.changedClassification(
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
      ? PurchaseEditFormComponent.changedClassification(selected, stored)
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

  private editPurchaseTimestamp(): string | null {
    if (!this.editForm.purchaseDate || !this.source) return null;
    const original = new Date(this.source.purchaseDate);
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
