import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { GstClassification, Product, Purchase, Supplier } from '../../../models/models';
import { PurchaseItemPayload, PurchaseUpdatePayload } from '../../../services/purchase.service';
import { IconComponent } from '../../shared/icon.component';
import { GST_CLASSIFICATION_OPTIONS, isChargePresent } from '../gst-classification-options';
import {
  DEFAULT_PRICE_ENTRY_MODE,
  PriceEntryMode,
  STORED_LINE_PRICE_ENTRY_MODE,
  enterLineTotal,
  enterUnitCost,
  isPurchaseLinePriceComplete,
  lineTotalFromUnitCost,
  purchaseLinePriceErrorMessage,
  purchaseLinePriceProblem,
  savedLineAmount,
  syncPurchaseLinePrice,
  totalPriceFieldValue,
  unitPriceFieldValue
} from '../purchase-line-price';

/**
 * One line of the purchase edit form (issue #431).
 *
 * `id` is the stored line's own stable identity and is what keeps each line's classification on the
 * right line when a purchase holds several lines for one product; it is `undefined` only for a line
 * added during the edit. `storedGstClassification` is the classification the purchase was read with,
 * which is how the form knows whether the person actually changed it: an unchanged classification is
 * left out of the request so the server keeps it and its provenance.
 *
 * `priceEntryMode` and `lineTotal` are the per-line price entry of issues #707 and #878: both
 * amounts are editable fields, `priceEntryMode` records which of them the operator typed into last,
 * and neither it nor the line total is submitted - the request still carries the quantity and the
 * unit cost alone. The quantity and both amounts are nullable because a number input binds `null`
 * for a blank field, and because a figure that cannot be derived is cleared rather than left stale.
 */
export interface EditPurchaseItem {
  id?: number;
  productId: number;
  quantity: number | null;
  priceEntryMode: PriceEntryMode;
  lineTotal: number | null;
  unitCost: number | null;
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

  /**
   * Whether a support session is active (issue #657, extending #554): Save is disabled while it
   * is, because a session grants `PurchasingOperate` - the capability this page's write would
   * otherwise be gated on - and the backend refuses the write itself only by its read-only method
   * rule, never by withholding the capability. The page component owns the subscription to
   * `SupportSessionService.active$`; this presentational form only reads the flag it is given.
   */
  @Input() sessionActive = false;

  /** The edited values, for the page to send through the existing update API. */
  @Output() readonly saveRequested = new EventEmitter<PurchaseUpdatePayload>();

  /** The person left without saving; the page decides where that returns to. */
  @Output() readonly editCancelled = new EventEmitter<void>();

  editItems: EditPurchaseItem[] = [];

  /** The GST picker's options, shared with the purchase entry form. */
  readonly gstOptions = GST_CLASSIFICATION_OPTIONS;

  /**
   * Why this form refused to submit its own lines (issue #707), as opposed to {@link error}, which
   * is the API's refusal of a save the page did send. Cleared on the next submission attempt.
   */
  lineError = '';

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
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) return;
    if (this.saving) return;
    if (!this.editForm.title.trim()) return;

    // Every line's quantity and price, in whichever direction it was entered (issue #707): a whole
    // number of units above zero, a finite non-negative entered amount, and a finite non-negative
    // unit cost to submit. A line that fails this is refused here, so a blank price, a zero
    // quantity's division or a NaN never reaches the update request.
    const invalidLines = this.editItems.filter((item) => !isPurchaseLinePriceComplete(item));
    this.lineError = invalidLines.length > 0 ? purchaseLinePriceErrorMessage(invalidLines.length) : '';
    if (invalidLines.length > 0) return;

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
      priceEntryMode: DEFAULT_PRICE_ENTRY_MODE,
      lineTotal: 0,
      unitCost: 0,
      gstClassification: GstClassification.Unknown,
      storedGstClassification: null
    });
  }
  removeEditItem(index: number): void { this.editItems.splice(index, 1); }

  /**
   * The line's purchased unit quantity changed, so the calculated price follows the field that won
   * last (issue #707): the entered total divided over the new unit count, or the entered unit price
   * multiplied by it. A stored line opens with its unit price winning, which is what keeps a
   * quantity correction from re-costing it.
   */
  onLineQuantityChange(item: EditPurchaseItem, quantity: number | null): void {
    item.quantity = quantity;
    syncPurchaseLinePrice(item);
  }

  /**
   * The operator typed in this line's Total price field, so that amount wins from now on and the
   * unit cost follows from it (issue #878).
   */
  onLineTotalChange(item: EditPurchaseItem, amount: number | null): void {
    enterLineTotal(item, amount);
  }

  /**
   * The operator typed in this line's Unit price field, so that price wins from now on and the line
   * total follows from it (issue #878).
   */
  onLineUnitCostChange(item: EditPurchaseItem, amount: number | null): void {
    enterUnitCost(item, amount);
  }

  /** What this line's Unit price field shows: the entered price, or the calculated one to 4 dp. */
  unitPriceValue(item: EditPurchaseItem): number | string | null {
    return unitPriceFieldValue(item);
  }

  /** What this line's Total price field shows: the entered total, or the calculated one to the cent. */
  totalPriceValue(item: EditPurchaseItem): number | string | null {
    return totalPriceFieldValue(item);
  }

  /** What still has to be entered on this line, or null when it is ready to save. */
  lineProblem(item: EditPurchaseItem): string | null {
    return purchaseLinePriceProblem(item);
  }

  /** The amount this line will save: the server's own round(quantity * unitCost, 2). */
  savedLineAmount(item: EditPurchaseItem): number { return savedLineAmount(item); }
  get editItemsSubtotal(): number { return this.editItems.reduce((sum, item) => sum + this.savedLineAmount(item), 0); }

  private open(purchase: Purchase): void {
    this.source = purchase;
    this.lineError = '';
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
    // A stored line opens with the figure the purchase actually holds - its unit cost - as the
    // winning field, and its line amount round(quantity * unitCost, 2) calculated into the Total
    // price field beside it. Typing a box price into that Total price field hands the win over
    // (issues #707 and #878).
    //
    // Unit price rather than the Add form's default is deliberate: it is what keeps an edit of
    // something else from re-costing the line. Opening a stored line on its total would mean a
    // change of quantity divided that total over the new unit count, so correcting "10 received,
    // not 4" would silently change the unit cost the purchase was recorded at, and a stored cost
    // no cent amount divides evenly into could not be reproduced from the rounded total at all. A
    // line added during the edit has no stored cost and starts like a new Add Purchase line.
    this.editItems = (purchase.items ?? []).map(item => ({
      id: item.id,
      productId: item.productId,
      quantity: item.quantity,
      priceEntryMode: STORED_LINE_PRICE_ENTRY_MODE,
      lineTotal: lineTotalFromUnitCost(item.unitCost, item.quantity),
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
      quantity: item.quantity as number,  // Safe to cast: submit() refuses an unusable line
      unitCost: item.unitCost as number,  // Safe to cast: submit() refuses an unusable line
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
