import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink, ActivatedRoute } from '@angular/router';
import { Subscription, of } from 'rxjs';
import { switchMap, tap, map, catchError } from 'rxjs/operators';
import { PurchaseService } from '../../services/purchase.service';
import { SupplierService } from '../../services/supplier.service';
import { SupplierOrderService } from '../../services/supplier-order.service';
import {
  GstClassification,
  Product,
  Supplier,
  SupplierOrder,
  SupplierOrderCompletionPreview
} from '../../models/models';
import { PurchaseItemPayload } from '../../services/purchase.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { IconComponent } from '../shared/icon.component';
import { GST_CLASSIFICATION_OPTIONS, isChargePresent } from './gst-classification-options';
import {
  DEFAULT_PRICE_ENTRY_MODE,
  PriceEntryMode,
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
} from './purchase-line-price';
import { SupportSessionService } from '../../support-session/support-session.service';
import { SupplierOrderCompletionDialogComponent } from './supplier-order-completion-dialog/supplier-order-completion-dialog.component';

// Draft item representation where the quantity and either price may be null
interface DraftPurchaseItem {
  productId: number;
  quantity: number | null;
  /**
   * Which of this line's two prices the operator typed into last; the other is calculated from the
   * line's purchased unit quantity (issues #707 and #878). It is an entry convenience only and is
   * never submitted.
   */
  priceEntryMode: PriceEntryMode;
  /** The amount paid for the whole line: entered while `total` wins, calculated while `unit` does. */
  lineTotal: number | null;
  /** The per-unit cost the request carries: entered while `unit` wins, calculated while `total` does. */
  unitCost: number | null;
  /**
   * This line's GST classification (issue #431). A new line starts as `Unknown`/Not classified and
   * stays that way unless a person picks something: nothing here pre-fills a classification from the
   * product, the supplier or the amount (parent issue #62, decision D4).
   */
  gstClassification: GstClassification;
}

@Component({
  selector: 'app-purchase-upload',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, IconComponent, SupplierOrderCompletionDialogComponent],
  templateUrl: './purchase-upload.component.html'
})
export class PurchaseUploadComponent implements OnInit, OnDestroy {
  suppliers: Supplier[] = [];
  products: Product[] = [];
  items: DraftPurchaseItem[] = [];
  selectedFile: File | null = null;
  previewUrl: string | null = null;
  saving = false;
  error = '';

  /**
   * Whether a support session is active (issue #657, extending #554): Add Purchase is disabled
   * while it is, because a session grants `PurchasingOperate` - the capability this page's write
   * would otherwise be gated on - and the backend refuses the write itself only by its read-only
   * method rule, never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  supplierOrderId: number | null = null;
  supplierOrder: SupplierOrder | null = null;
  loadingSupplierOrder = false;
  noOutstandingItems = false;
  supplierOrderLoadFailed = false;

  /**
   * The server's projection of what the linked order would still be owed, while the operator is
   * being asked about it (issue #974). Non-null is exactly "the confirmation is open", and every
   * figure in it is the server's: this page formats them and calculates nothing.
   */
  completionChoice: SupplierOrderCompletionPreview | null = null;

  /**
   * The answer the operator gave, once. `null` means "keep the order open", which submits no
   * completion fields at all - the save that has always happened.
   */
  private completionDecision: { supplierOrderId: number; fingerprint: string } | null = null;

  /**
   * Whether the choice has been settled for the attempt in progress. A failed save clears it, so
   * the next attempt re-previews rather than resubmitting a fingerprint the server already refused
   * as stale.
   */
  private completionChoiceSettled = false;

  /** The GST picker's options, shared with the purchase edit form. */
  readonly gstOptions = GST_CLASSIFICATION_OPTIONS;

  form = {
    title: '',
    notes: '',
    totalAmount: null as number | null,
    deliveryCost: null as number | null,
    deliveryGstClassification: GstClassification.Unknown,
    packageCost: null as number | null,
    packageGstClassification: GstClassification.Unknown,
    purchaseDate: PurchaseUploadComponent.localDate(new Date()),
    supplierId: '' as number | ''
  };

  constructor(
    private readonly purchaseService: PurchaseService,
    private readonly supplierService: SupplierService,
    private readonly supplierOrderService: SupplierOrderService,
    private readonly productService: ProductService,
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly toastService: ToastService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  ngOnInit(): void {
    this.supplierService.getAll().subscribe((s) => (this.suppliers = s));
    this.productService.getAll().subscribe((p) => (this.products = p));

    // switchMap cancels any in-flight supplier order request whenever the
    // id changes, so a stale response can never overwrite a newer one.
    this.route.queryParamMap
      .pipe(
        map((params) => {
          const raw = params.get('supplierOrderId');
          return raw ? Number(raw) : null;
        }),
        tap(() => this.resetPurchaseForm()),
        switchMap((id) => {
          if (id === null) {
            return of(null);
          }
          this.loadingSupplierOrder = true;
          return this.supplierOrderService.getById(id).pipe(
            map((order) => ({ id, order })),
            catchError(() => of({ id, order: null }))
          );
        })
      )
      .subscribe((result) => {
        if (result === null) {
          return;
        }

        this.loadingSupplierOrder = false;
        this.supplierOrderId = result.id;

        if (result.order) {
          this.supplierOrder = result.order;
          this.prefillFromSupplierOrder(result.order);
        } else {
          this.supplierOrderLoadFailed = true;
          this.supplierOrder = null;
        }
      });
  }

  // Restores a fresh, empty Create Purchase form (manual-purchase defaults).
  private resetPurchaseForm(): void {
    this.items = [];
    this.selectedFile = null;
    this.previewUrl = null;
    this.error = '';
    this.saving = false;

    this.form = {
      title: '',
      notes: '',
      totalAmount: null,
      deliveryCost: null,
      deliveryGstClassification: GstClassification.Unknown,
      packageCost: null,
      packageGstClassification: GstClassification.Unknown,
      purchaseDate: PurchaseUploadComponent.localDate(new Date()),
      supplierId: ''
    };

    this.supplierOrderId = null;
    this.supplierOrder = null;
    this.loadingSupplierOrder = false;
    this.noOutstandingItems = false;
    this.supplierOrderLoadFailed = false;
    this.completionChoice = null;
    this.completionDecision = null;
    this.completionChoiceSettled = false;
  }

  private prefillFromSupplierOrder(order: SupplierOrder): void {
    // Prefill supplier
    if (order.supplierId) {
      this.form.supplierId = order.supplierId;
    }

    // Set purchase date to today
    this.form.purchaseDate = PurchaseUploadComponent.localDate(new Date());

    // Prefill reference/notes with order information
    if (order.reference) {
      this.form.notes = `Supplier Order #${order.id}: ${order.reference}`;
    } else {
      this.form.notes = `Supplier Order #${order.id}`;
    }

    // Prefill items from outstanding quantities only
    const outstandingLines = order.lines.filter((line) => line.outstandingQuantity > 0);

    if (outstandingLines.length === 0) {
      this.noOutstandingItems = true;
      return;
    }

    // Use draft items with nullable unitCost - do NOT convert null to 0. The GST classification
    // stays Not classified: a supplier order says nothing about a line's GST status.
    //
    // The line opens with Total price as the winning field, on the total that prefilled unit price
    // already implies, and the unit cost itself is left exactly as the order recorded it: receiving
    // an order without typing a price submits the same unit cost as before issue #707.
    this.items = outstandingLines.map((line) => ({
      productId: line.productId,
      quantity: line.outstandingQuantity,
      priceEntryMode: DEFAULT_PRICE_ENTRY_MODE,
      lineTotal: lineTotalFromUnitCost(line.unitPrice ?? null, line.outstandingQuantity),
      unitCost: line.unitPrice ?? null,
      gstClassification: GstClassification.Unknown
    }));
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.selectedFile = input.files[0];
      if (!this.form.title) this.form.title = this.selectedFile.name;

      if (this.selectedFile.type.startsWith('image/')) {
        const reader = new FileReader();
        reader.onload = () => (this.previewUrl = reader.result as string);
        reader.readAsDataURL(this.selectedFile);
      } else {
        this.previewUrl = null;
      }
    }
  }

  upload(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
    if (this.loadingSupplierOrder) {
      this.error = 'Please wait while the supplier order is loading.';
      return;
    }

    if (this.supplierOrderLoadFailed) {
      this.error = 'Unable to load supplier order.';
      return;
    }

    if (this.noOutstandingItems) {
      this.error = 'This supplier order has no outstanding items to receive.';
      return;
    }

    if (!this.selectedFile) {
      this.error = 'Please select a receipt or invoice (PDF/image).';
      return;
    }
    if (!this.form.title.trim()) {
      this.error = 'Please enter a purchase title.';
      return;
    }

    // Validate every line's quantity and price, in whichever direction it was entered (issue
    // #707): a whole number of units above zero, a finite non-negative entered amount, and a
    // finite non-negative unit cost to submit - so a blank price, a zero quantity's division and a
    // NaN are all refused here rather than sent.
    const invalidItems = this.items.filter(item => !isPurchaseLinePriceComplete(item));
    if (invalidItems.length > 0) {
      this.error = purchaseLinePriceErrorMessage(invalidItems.length);
      return;
    }

    this.error = '';

    // Map draft items to PurchaseItemPayload only after validation
    const validItems: PurchaseItemPayload[] = this.items.map(item => ({
      productId: item.productId,
      quantity: item.quantity as number,  // Safe to cast after validation
      unitCost: item.unitCost as number,  // Safe to cast after validation
      gstClassification: PurchaseUploadComponent.submittedClassification(item.gstClassification)
    }));

    // Before the final save, when this purchase was started from a supplier order, ask the server
    // what the order would still be owed (issue #974). The projection, the allocation and the
    // eligibility are all its answers; nothing about the balance is worked out here.
    if (this.supplierOrderId !== null && !this.completionChoiceSettled) {
      this.askAboutSupplierOrderCompletion(this.supplierOrderId, validItems);
      return;
    }

    this.saving = true;
    this.purchaseService
      .upload({
        file: this.selectedFile,
        title: this.form.title.trim(),
        notes: this.form.notes || null,
        totalAmount: this.form.totalAmount,
        deliveryCost: this.form.deliveryCost,
        deliveryGstClassification: this.chargeClassification(
          this.form.deliveryCost, this.form.deliveryGstClassification),
        packageCost: this.form.packageCost,
        packageGstClassification: this.chargeClassification(
          this.form.packageCost, this.form.packageGstClassification),
        purchaseDate: this.purchaseTimestamp(),
        supplierId: this.form.supplierId === '' ? null : this.form.supplierId,
        items: validItems,
        completeSupplierOrderId: this.completionDecision?.supplierOrderId ?? null,
        completeSupplierOrderFingerprint: this.completionDecision?.fingerprint ?? null
      })
      .subscribe({
        next: () => {
          // Only now, after the server stored it, is a completion reported as having happened.
          if (this.completionDecision) {
            this.toastService.success('Purchase created and the supplier order completed.');
            this.router.navigate(['/purchases/orders']);
          } else if (this.supplierOrderId) {
            this.toastService.success('Purchase created from supplier order.');
            this.router.navigate(['/purchases/orders']);
          } else {
            this.toastService.success('Purchase created.');
            this.router.navigate(['/purchases']);
          }
        },
        error: (err) => {
          this.error = err?.error ?? 'Failed to add purchase.';
          this.saving = false;
          // The entered purchase stays on screen, and the choice is asked again: a refusal may be
          // exactly that the order changed, so the fingerprint it was made against is no good.
          this.completionDecision = null;
          this.completionChoiceSettled = false;
        }
      });
  }

  /**
   * Asks the server to project the linked order against the purchase as entered, and opens the
   * confirmation only when a positive balance really would be left undelivered. An order that is no
   * longer open, or one this purchase receives in full, has nothing to close and is saved straight
   * through - which is what keeps the existing fully received path from asking about a balance that
   * does not exist.
   */
  private askAboutSupplierOrderCompletion(supplierOrderId: number, items: PurchaseItemPayload[]): void {
    this.saving = true;
    this.supplierOrderService
      .completionPreview(supplierOrderId, {
        supplierId: this.form.supplierId === '' ? null : this.form.supplierId,
        purchaseDate: this.purchaseTimestamp(),
        lines: items.map((item) => ({ productId: item.productId, quantity: item.quantity }))
      })
      .subscribe({
        next: (preview) => {
          this.saving = false;
          if (preview.eligibleForCompletion && preview.hasOutstandingBalance) {
            this.completionChoice = preview;
            return;
          }

          this.completionChoiceSettled = true;
          this.completionDecision = null;
          this.upload();
        },
        error: () => {
          this.saving = false;
          this.error =
            'Unable to check what this supplier order is still owed, so nothing was saved. ' +
            'Try again, or reload the order.';
        }
      });
  }

  /**
   * "Keep order open" - the safe default. The purchase is saved exactly as it always was, the order
   * stays Partially received, and its remaining balance stays outstanding supply.
   */
  keepSupplierOrderOpen(): void {
    this.completionChoice = null;
    this.completionDecision = null;
    this.completionChoiceSettled = true;
    this.upload();
  }

  /**
   * "Complete order" - no further delivery expected. The request carries the order id and the
   * fingerprint the server issued, and nothing else: the save re-reads the order, re-checks its
   * eligibility and recomputes that fingerprint inside its own transaction.
   */
  completeSupplierOrder(): void {
    const choice = this.completionChoice;
    if (choice === null) {
      return;
    }

    this.completionChoice = null;
    this.completionDecision = {
      supplierOrderId: choice.supplierOrderId,
      fingerprint: choice.decisionFingerprint
    };
    this.completionChoiceSettled = true;
    this.upload();
  }

  /**
   * Cancelling the confirmation returns to the purchase form with everything that was entered still
   * there, and saves nothing at all.
   */
  cancelCompletionChoice(): void {
    this.completionChoice = null;
    this.completionDecision = null;
    this.completionChoiceSettled = false;
    this.saving = false;
  }

  addItem(): void {
    this.items.push({
      productId: this.products[0]?.id ?? 0,
      quantity: 1,
      priceEntryMode: DEFAULT_PRICE_ENTRY_MODE,
      lineTotal: null,
      unitCost: null,
      gstClassification: GstClassification.Unknown
    });
  }
  removeItem(index: number): void { this.items.splice(index, 1); }

  /**
   * The line's purchased unit quantity changed, so the calculated price follows the field that won
   * last (issue #707): the entered total divided over the new unit count, or the entered unit price
   * multiplied by it.
   */
  onLineQuantityChange(item: DraftPurchaseItem, quantity: number | null): void {
    item.quantity = quantity;
    syncPurchaseLinePrice(item);
  }

  /**
   * The operator typed in this line's Total price field, so that amount wins from now on and the
   * unit cost follows from it (issue #878).
   */
  onLineTotalChange(item: DraftPurchaseItem, amount: number | null): void {
    enterLineTotal(item, amount);
  }

  /**
   * The operator typed in this line's Unit price field, so that price wins from now on and the line
   * total follows from it (issue #878).
   */
  onLineUnitCostChange(item: DraftPurchaseItem, amount: number | null): void {
    enterUnitCost(item, amount);
  }

  /** What this line's Unit price field shows: the entered price, or the calculated one to 4 dp. */
  unitPriceValue(item: DraftPurchaseItem): number | string | null {
    return unitPriceFieldValue(item);
  }

  /** What this line's Total price field shows: the entered total, or the calculated one to the cent. */
  totalPriceValue(item: DraftPurchaseItem): number | string | null {
    return totalPriceFieldValue(item);
  }

  /** What still has to be entered on this line, or null when it is ready to save. */
  lineProblem(item: DraftPurchaseItem): string | null {
    return purchaseLinePriceProblem(item);
  }

  /**
   * Whether a delivery or package charge exists, which is what decides whether its GST picker is
   * shown. An absent charge has no classification and is never unresolved, so offering a picker for
   * one would invite a choice the server would correctly discard.
   */
  hasCharge(amount: number | null): boolean { return isChargePresent(amount); }

  /**
   * The classification to submit for a delivery or package charge: nothing at all unless the charge
   * exists and a person actually picked a state for it.
   */
  private chargeClassification(
    amount: number | null, classification: GstClassification): GstClassification | undefined {
    return isChargePresent(amount) ? PurchaseUploadComponent.submittedClassification(classification) : undefined;
  }

  /**
   * What a new purchase submits for one component. `Unknown` is left out of the request rather than
   * sent: it is already the server's state for a component nobody classified, and omitting it keeps
   * the request free of choices the person did not make.
   */
  private static submittedClassification(classification: GstClassification): GstClassification | undefined {
    return classification === GstClassification.Unknown ? undefined : classification;
  }
  itemProduct(item: DraftPurchaseItem): Product | undefined { return this.products.find(p => p.id === Number(item.productId)); }

  /** The amount this line will save: the server's own round(quantity * unitCost, 2). */
  savedLineAmount(item: DraftPurchaseItem): number { return savedLineAmount(item); }
  get itemsSubtotal(): number { return this.items.reduce((sum, item) => sum + this.savedLineAmount(item), 0); }

  private purchaseTimestamp(): string | null {
    if (!this.form.purchaseDate) return null;
    const selected = new Date(`${this.form.purchaseDate}T00:00:00`);
    const now = new Date();
    if (selected.getFullYear() === now.getFullYear() &&
        selected.getMonth() === now.getMonth() &&
        selected.getDate() === now.getDate()) {
      return now.toISOString();
    }
    return selected.toISOString();
  }

  private static localDate(value: Date): string {
    const year = value.getFullYear();
    const month = String(value.getMonth() + 1).padStart(2, '0');
    const day = String(value.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
