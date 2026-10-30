import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { GstClassification, Product, PurchaseGstSummary, Supplier } from '../../../models/models';
import { IconComponent } from '../../shared/icon.component';
import { HelpToggletipComponent } from '../../shared/help-toggletip/help-toggletip.component';
import { GST_CLASSIFICATION_OPTIONS, isChargePresent } from '../gst-classification-options';
import {
  PurchaseLinePrice,
  enterLineTotal,
  enterUnitCost,
  purchaseLinePriceProblem,
  savedLineAmount,
  syncPurchaseLinePrice,
  totalPriceFieldValue,
  unitPriceFieldValue
} from '../purchase-line-price';
import { PurchaseProductPickerComponent } from './purchase-product-picker.component';
import { PurchaseReceiptFieldComponent, StoredReceipt } from './purchase-receipt-field.component';
import { ReceiptTotalComparison, compareReceiptTotal, purchaseTotal } from './purchase-receipt-comparison';

/** The purchase-level fields both forms edit, under the names both already used. */
export interface PurchaseFormFields {
  title: string;
  notes: string;
  totalAmount: number | null;
  deliveryCost: number | null;
  deliveryGstClassification: GstClassification;
  packageCost: number | null;
  packageGstClassification: GstClassification;
  purchaseDate: string;
  supplierId: number | '';
}

/** One line as both forms hold it; `id` is set only on a line the saved purchase already has. */
export interface PurchaseFormLine extends PurchaseLinePrice {
  id?: number;
  productId: number;
  gstClassification: GstClassification;
}

/** The message a line shows while it has no product chosen. */
export const MISSING_PRODUCT_MESSAGE = 'Choose a product.';

/** The message a form shows above itself when some of its lines have no product chosen. */
export function missingProductErrorMessage(lineCount: number): string {
  return `Choose a product for every item. ${lineCount} item(s) have no product.`;
}

/**
 * The one Add/Edit purchase layout (issue #1029): Purchase details, Purchased items, the optional
 * Additional costs and Notes, the Purchase summary and the sticky Cancel / Save purchase bar.
 *
 * It is presentational. Add Purchase (`PurchaseUploadComponent`) and the Edit purchase form
 * (`PurchaseEditFormComponent`) each own their own form object and lines, pass them in, and keep
 * every rule that differs between creating and editing: what a save submits, which GST
 * classifications are sent, how the purchase date becomes an instant, and what happens to a receipt.
 * This component edits the objects it is given in place, through the same shared line-price module
 * both forms already used, and reports only what the pages act on.
 *
 * It performs no GST calculation. The purchase total it shows is the items subtotal the forms have
 * always shown plus the entered charges, and its receipt comparison mirrors the server's own
 * `PurchaseTotalValidationPolicy`; neither changes what is saved.
 */
@Component({
  selector: 'app-purchase-form-layout',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    IconComponent,
    HelpToggletipComponent,
    PurchaseProductPickerComponent,
    PurchaseReceiptFieldComponent
  ],
  templateUrl: './purchase-form-layout.component.html'
})
export class PurchaseFormLayoutComponent implements OnChanges {
  @Input() mode: 'add' | 'edit' = 'add';
  @Input({ required: true }) form!: PurchaseFormFields;
  @Input({ required: true }) items: PurchaseFormLine[] = [];
  @Input() suppliers: Supplier[] = [];
  @Input() products: Product[] = [];

  /** Prefixes every field id (`editTitle` rather than `title`), so two forms never share an id. */
  @Input() idPrefix = '';

  /** Prefixes every `data-testid` (`edit-purchase-item` rather than `purchase-item`). */
  @Input() testIdPrefix = '';

  @Input() saving = false;
  @Input() sessionActive = false;

  /** Why the last save was refused, or why the page refused to send it. */
  @Input() error = '';

  /** Why the lines could not be submitted, shown above them. */
  @Input() lineError = '';

  @Input() pendingFile: File | null = null;
  @Input() storedReceipt: StoredReceipt | null = null;

  /**
   * The saved purchase's input GST as the API last calculated it, on Edit. `null` on Add, and
   * whenever the API sent none: GST is then shown as calculated on save, never as $0.
   */
  @Input() gstSummary: PurchaseGstSummary | null = null;

  @Output() readonly submitted = new EventEmitter<void>();
  @Output() readonly cancelled = new EventEmitter<void>();
  @Output() readonly addLine = new EventEmitter<void>();
  @Output() readonly removeLine = new EventEmitter<number>();
  @Output() readonly pendingFileChange = new EventEmitter<File | null>();
  @Output() readonly previewStoredReceipt = new EventEmitter<void>();

  /** The supplier or the purchase date changed, which is what a suggested title is made from. */
  @Output() readonly detailsChange = new EventEmitter<void>();

  /** The person typed in the title, so a suggested one must never replace it again. */
  @Output() readonly titleEdited = new EventEmitter<void>();

  readonly gstOptions = GST_CLASSIFICATION_OPTIONS;
  readonly missingProductMessage = MISSING_PRODUCT_MESSAGE;

  /** Save was pressed at least once, so every problem may be shown, not only touched ones. */
  submitAttempted = false;
  titleTouched = false;

  /**
   * Whether the person opened (`true`) or closed (`false`) an optional section. `null` follows its
   * content: an empty section starts collapsed and one holding a value starts expanded. Collapsing
   * only hides the fields; their values stay in the form and are still saved.
   */
  additionalCostsExpanded: boolean | null = null;
  notesExpanded: boolean | null = null;

  private touchedLines = new WeakSet<PurchaseFormLine>();

  ngOnChanges(changes: SimpleChanges): void {
    // A new form object is a newly opened purchase (a route change or a reset), so its own
    // validation and section state start again.
    if (changes['form'] && !changes['form'].firstChange) {
      this.submitAttempted = false;
      this.titleTouched = false;
      this.additionalCostsExpanded = null;
      this.notesExpanded = null;
      this.touchedLines = new WeakSet<PurchaseFormLine>();
    }
  }


  fieldId(name: string): string {
    return this.idPrefix ? `${this.idPrefix}${name.charAt(0).toUpperCase()}${name.slice(1)}` : name;
  }

  testId(name: string): string {
    return `${this.testIdPrefix}${name}`;
  }

  lineFieldId(index: number, field: string): string {
    return `${this.idPrefix || 'purchase'}-line-${index}-${field}`;
  }

  // ---- Details ------------------------------------------------------------------------------

  get titleProblem(): string | null {
    return this.form.title.trim() ? null : 'Enter a purchase title.';
  }

  get showTitleProblem(): boolean {
    return this.titleProblem !== null && (this.titleTouched || this.submitAttempted);
  }

  onSupplierChange(supplierId: number | ''): void {
    this.form.supplierId = supplierId;
    this.detailsChange.emit();
  }

  onDateChange(purchaseDate: string): void {
    this.form.purchaseDate = purchaseDate;
    this.detailsChange.emit();
  }

  onTitleChange(title: string): void {
    this.form.title = title;
    this.titleEdited.emit();
  }

  // ---- Lines --------------------------------------------------------------------------------

  /**
   * A line saved on the purchase keeps its product: the server refuses to re-point an identified
   * line (`PurchaseLineIdentityPolicy`), so the product is shown rather than offered for change.
   */
  isFixedProductLine(item: PurchaseFormLine): boolean {
    return this.mode === 'edit' && item.id !== undefined && item.id !== null;
  }

  productName(productId: number): string {
    return this.products.find((p) => p.id === Number(productId))?.name ?? `Product ${productId}`;
  }

  markLineTouched(item: PurchaseFormLine): void {
    this.touchedLines.add(item);
  }

  private showsProblems(item: PurchaseFormLine): boolean {
    return this.submitAttempted || this.touchedLines.has(item);
  }

  /** The missing product, shown once the line was touched or a save was attempted. */
  productProblem(item: PurchaseFormLine): string | null {
    return !item.productId && this.showsProblems(item) ? MISSING_PRODUCT_MESSAGE : null;
  }

  /** The line's quantity or price problem, shown once the line was touched or a save was attempted. */
  priceProblem(item: PurchaseFormLine): string | null {
    return this.showsProblems(item) ? purchaseLinePriceProblem(item) : null;
  }

  onProductChange(item: PurchaseFormLine, productId: number): void {
    item.productId = productId;
  }

  onQuantityChange(item: PurchaseFormLine, quantity: number | null): void {
    item.quantity = quantity;
    syncPurchaseLinePrice(item);
    this.markLineTouched(item);
  }

  onUnitPriceChange(item: PurchaseFormLine, amount: number | null): void {
    enterUnitCost(item, amount);
    this.markLineTouched(item);
  }

  onLineTotalChange(item: PurchaseFormLine, amount: number | null): void {
    enterLineTotal(item, amount);
    this.markLineTouched(item);
  }

  unitPriceValue(item: PurchaseFormLine): number | string | null {
    return unitPriceFieldValue(item);
  }

  totalPriceValue(item: PurchaseFormLine): number | string | null {
    return totalPriceFieldValue(item);
  }

  // ---- Optional sections --------------------------------------------------------------------

  hasCharge(amount: number | null | undefined): boolean {
    return isChargePresent(amount);
  }

  private hasValue(amount: number | null | undefined): boolean {
    return amount !== null && amount !== undefined && !Number.isNaN(amount);
  }

  get additionalCostsOpen(): boolean {
    return this.additionalCostsExpanded ?? (this.hasValue(this.form.deliveryCost) || this.hasValue(this.form.packageCost));
  }

  get notesOpen(): boolean {
    return this.notesExpanded ?? this.form.notes.trim().length > 0;
  }

  toggleAdditionalCosts(): void {
    this.additionalCostsExpanded = !this.additionalCostsOpen;
  }

  toggleNotes(): void {
    this.notesExpanded = !this.notesOpen;
  }

  // ---- Summary ------------------------------------------------------------------------------

  get itemsSubtotal(): number {
    return this.items.reduce((sum, item) => sum + savedLineAmount(item), 0);
  }

  get total(): number {
    return purchaseTotal(this.items, this.form.deliveryCost, this.form.packageCost);
  }

  get receiptComparison(): ReceiptTotalComparison {
    return compareReceiptTotal(this.form.totalAmount, this.items, this.form.deliveryCost, this.form.packageCost);
  }

  // ---- Actions ------------------------------------------------------------------------------

  submit(): void {
    this.submitAttempted = true;
    this.submitted.emit();
  }

  cancel(): void {
    this.cancelled.emit();
  }
}
