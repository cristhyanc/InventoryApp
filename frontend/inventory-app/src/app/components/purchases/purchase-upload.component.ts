import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
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
  SupplierGstDefaults,
  SupplierOrder,
  SupplierOrderCompletionPreview
} from '../../models/models';
import { PurchaseDuplicateMatch, PurchaseItemPayload } from '../../services/purchase.service';
import { generateIdempotencyKey } from '../pick-list/idempotency-key';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { isChargePresent } from './gst-classification-options';
import {
  DEFAULT_PRICE_ENTRY_MODE,
  PriceEntryMode,
  enterLineTotal,
  enterUnitCost,
  isPurchaseLinePriceComplete,
  lineTotalFromUnitCost,
  purchaseLinePriceErrorMessage,
  purchaseLinePriceProblem,
  syncPurchaseLinePrice,
  totalPriceFieldValue,
  unitPriceFieldValue
} from './purchase-line-price';
import { SupportSessionService } from '../../support-session/support-session.service';
import { SupplierOrderCompletionDialogComponent } from './supplier-order-completion-dialog/supplier-order-completion-dialog.component';
import {
  PurchaseFormLayoutComponent,
  PurchaseLineGstRuleSource,
  missingProductErrorMessage
} from './purchase-form/purchase-form-layout.component';
import { purchaseTitleSuggestion } from './purchase-form/purchase-title-suggestion';
import {
  PurchaseScannedLevy,
  PurchaseScannedLine,
  isScannedLineSkipped,
  purchaseScanFill
} from './purchase-form/purchase-scan-draft';
import {
  PurchaseReceiptScanComponent,
  PurchaseReceiptScanned
} from './purchase-receipt-scan/purchase-receipt-scan.component';

/**
 * The part of `AttachmentStorageQuotaPolicy.StorageFullMessage` (issue #968) that identifies the
 * refusal, which the create endpoint answers as a bare `400` string with no code of its own.
 *
 * Matching a sentence is not a contract, so this is written to depend on as little of it as
 * possible, and the cost of a miss is bounded: the refusal is still shown in full and nothing is
 * saved - only the "save it without the receipt" shortcut would be absent, and attaching the
 * document to the saved purchase later (issue #976) still works.
 */
const STORAGE_ALLOWANCE_REFUSAL = 'attachment storage allowance';

/** Whether a refused save was refused because the receipt does not fit the storage allowance. */
export function isAttachmentStorageRefusal(message: unknown): boolean {
  return typeof message === 'string' && message.includes(STORAGE_ALLOWANCE_REFUSAL);
}

/** What is shown when a save is refused and nothing more specific was returned. */
const SAVE_REFUSED_MESSAGE = 'Failed to add purchase.';

/**
 * The refusal to show for a failed save. The create endpoint answers its own refusals as a plain
 * `400` sentence, and the reused-key conflict of issue #1247 as a `409` ProblemDetails document
 * whose `detail` is the sentence written for the person - so both are shown verbatim, and anything
 * else falls back to the generic message rather than rendering an object.
 */
export function refusalMessage(error: unknown): string {
  const body = (error as { error?: unknown } | null)?.error;
  if (typeof body === 'string' && body.trim().length > 0) {
    return body;
  }

  const detail = (body as { detail?: unknown } | null)?.detail;
  return typeof detail === 'string' && detail.trim().length > 0 ? detail : SAVE_REFUSED_MESSAGE;
}

/**
 * The sentence shown when the server reports a purchase that already records this supplier, this
 * invoice number and this date (issue #1247). It names the purchase so the person can go and look,
 * and says nothing about what they should do: saving anyway is allowed and sometimes right.
 */
export function possibleDuplicateMessage(match: PurchaseDuplicateMatch): string {
  return `A purchase with this supplier, invoice number and date is already recorded: "${match.title}" (#${match.purchaseId}).`;
}

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
   * This line's GST classification (issue #431). Until a person picks one (`gstChosen`), it is
   * pre-filled from the product's GST rule, else the supplier's product-line default (issue #1348,
   * which reverses parent issue #62's decision D4 for new purchases), and left out of the request so
   * the server applies the same rule. Nothing is ever inferred from an amount.
   */
  gstClassification: GstClassification;
  gstChosen?: boolean;
  gstRuleSource?: PurchaseLineGstRuleSource;

  /**
   * What a scanned receipt said about this line (issue #1166), and absent on a line somebody typed.
   * A line it marks as skipped is shown, counted towards the receipt total, and never submitted.
   */
  scan?: PurchaseScannedLine;
}

@Component({
  selector: 'app-purchase-upload',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    PurchaseFormLayoutComponent,
    PurchaseReceiptScanComponent,
    SupplierOrderCompletionDialogComponent
  ],
  templateUrl: './purchase-upload.component.html'
})
export class PurchaseUploadComponent implements OnInit, OnDestroy {
  suppliers: Supplier[] = [];
  products: Product[] = [];
  items: DraftPurchaseItem[] = [];
  /** The receipt chosen for this purchase, sent with the create only when the purchase is saved. */
  selectedFile: File | null = null;
  saving = false;
  error = '';

  /**
   * The receipt a scan read (issue #1166), shown beside the lines it produced. It is a record of
   * what the lines came from rather than of what will be uploaded - removing the chosen document
   * leaves it on screen, because the lines it explains are still there.
   */
  scannedReceipt: File | null = null;

  /** Levies a scanned receipt's own totals could not place on any product; never saved as lines. */
  scannedLevies: PurchaseScannedLevy[] = [];

  /**
   * Whether the last save was refused because the receipt does not fit the business's attachment
   * storage allowance (issue #968). The filled form is kept and the one way forward is offered -
   * saving the purchase without the receipt - and until that is chosen, nothing is saved.
   */
  storageRefusedReceipt = false;

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

  /**
   * Whether the person has typed in the title (issue #1029). Until they do, the title follows the
   * supplier and the purchase date; once they have, nothing replaces what they typed.
   */
  private titleEdited = false;

  /**
   * The key this form's save is claimed under (issue #1247). It is created when the form opens and
   * kept through every failed or timed-out attempt, which is what makes a retry safe: if the first
   * attempt did save the purchase, the server answers with it and nothing is recorded twice; if it
   * did not, the retry records it. A new key is created only after a save actually succeeds and
   * whenever the form is reset, because by then the next save is a different purchase.
   */
  private idempotencyKey = generateIdempotencyKey();

  /**
   * The "this looks like a purchase you already recorded" sentence (issue #1247), or `null`. It is
   * the server's answer, formatted: this page decides nothing about what counts as a duplicate and
   * never blocks the save.
   */
  duplicateWarning: string | null = null;

  /** The in-flight duplicate lookup, cancelled whenever a newer one starts or the form is reset. */
  private duplicateLookup: Subscription | null = null;

  /**
   * Product GST rules already read for this page (issue #1348), by product id. A product whose
   * rule could not be read is recorded as having none: the line then shows Not classified, and the
   * server still applies the real rule when the untouched line is saved.
   */
  private readonly productGstRules = new Map<number, GstClassification>();
  private readonly productGstRuleRequests = new Set<number>();

  /** The chosen supplier's GST defaults (issue #1348), or `null` while none is chosen or known. */
  private supplierGstDefaults: SupplierGstDefaults | null = null;
  private supplierGstDefaultsFor: number | null = null;
  private supplierGstDefaultsLookup: Subscription | null = null;

  form = {
    title: '',
    notes: '',
    invoiceNumber: '',
    totalAmount: null as number | null,
    deliveryCost: null as number | null,
    deliveryGstClassification: GstClassification.Unknown,
    packageCost: null as number | null,
    packageGstClassification: GstClassification.Unknown,
    deliveryGstChosen: false,
    packageGstChosen: false,
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
    this.duplicateLookup?.unsubscribe();
    this.supplierGstDefaultsLookup?.unsubscribe();
  }

  ngOnInit(): void {
    this.supplierService.getAll().subscribe((s) => {
      this.suppliers = s;
      // The supplier's name may only now be known, so the suggestion can name it.
      this.refreshSuggestedTitle();
    });
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
    this.scannedReceipt = null;
    this.scannedLevies = [];
    this.storageRefusedReceipt = false;
    this.error = '';
    this.saving = false;
    // A reset form is a different purchase, so it is saved under a key of its own (issue #1247),
    // and nothing is yet known about whether it looks like one already recorded.
    this.idempotencyKey = generateIdempotencyKey();
    this.duplicateLookup?.unsubscribe();
    this.duplicateLookup = null;
    this.duplicateWarning = null;

    this.form = {
      title: '',
      notes: '',
      invoiceNumber: '',
      totalAmount: null,
      deliveryCost: null,
      deliveryGstClassification: GstClassification.Unknown,
      packageCost: null,
      packageGstClassification: GstClassification.Unknown,
      deliveryGstChosen: false,
      packageGstChosen: false,
      purchaseDate: PurchaseUploadComponent.localDate(new Date()),
      supplierId: ''
    };
    this.supplierGstDefaultsLookup?.unsubscribe();
    this.supplierGstDefaultsLookup = null;
    this.supplierGstDefaults = null;
    this.supplierGstDefaultsFor = null;

    this.supplierOrderId = null;
    this.supplierOrder = null;
    this.loadingSupplierOrder = false;
    this.noOutstandingItems = false;
    this.supplierOrderLoadFailed = false;
    this.completionChoice = null;
    this.completionDecision = null;
    this.completionChoiceSettled = false;
    this.titleEdited = false;
    this.refreshSuggestedTitle();
  }

  /**
   * The supplier or the purchase date changed. Both feed a title the person has not typed, and both
   * are part of what makes this purchase look like one already recorded (issue #1247).
   */
  onDetailsChange(): void {
    this.refreshSuggestedTitle();
    this.checkForPossibleDuplicate();
    this.loadSupplierGstDefaults();
  }

  /** A line's product changed, so its GST follows that product's rule until a person picks one. */
  onLineProductChange(): void {
    this.loadGstRules();
  }

  /**
   * Reads whatever product rules and supplier defaults the current lines and supplier need, then
   * pre-fills every GST picker nobody has picked (issue #1348).
   */
  private loadGstRules(): void {
    this.loadSupplierGstDefaults();
    for (const productId of new Set(this.items.map((item) => Number(item.productId)).filter((id) => id > 0))) {
      if (this.productGstRules.has(productId) || this.productGstRuleRequests.has(productId)) continue;
      this.productGstRuleRequests.add(productId);
      this.productService.getGstRule(productId).subscribe({
        next: (rule) => this.recordProductGstRule(productId, rule.gstRule),
        error: () => this.recordProductGstRule(productId, GstClassification.Unknown)
      });
    }
    this.applyGstRules();
  }

  private recordProductGstRule(productId: number, rule: GstClassification): void {
    this.productGstRuleRequests.delete(productId);
    this.productGstRules.set(productId, rule);
    this.applyGstRules();
  }

  /** Reads the chosen supplier's GST defaults, once per supplier, and drops a stale answer. */
  private loadSupplierGstDefaults(): void {
    const supplierId = this.form.supplierId === '' ? null : Number(this.form.supplierId);
    if (supplierId === this.supplierGstDefaultsFor) return;

    this.supplierGstDefaultsLookup?.unsubscribe();
    this.supplierGstDefaultsLookup = null;
    this.supplierGstDefaultsFor = supplierId;
    this.supplierGstDefaults = null;
    this.applyGstRules();
    if (supplierId === null) return;

    this.supplierGstDefaultsLookup = this.supplierService.getGstDefaults(supplierId).subscribe({
      next: (defaults) => {
        if (this.supplierGstDefaultsFor !== supplierId) return;
        this.supplierGstDefaults = defaults;
        this.applyGstRules();
      },
      // Unknown defaults pre-fill nothing; the server still applies the real ones on save.
      error: () => undefined
    });
  }

  /**
   * Pre-fills every GST picker a person has not picked, with the server's precedence: a line takes
   * the product's rule, then the supplier's product-line default; a charge takes only the supplier's
   * matching fee default. It is only what the picker shows: an unpicked component is left out of the
   * request, and the server classifies it from the rules as they stand when it saves.
   */
  private applyGstRules(): void {
    const defaults = this.supplierGstDefaults;
    for (const item of this.items) {
      if (item.gstChosen) continue;
      const productRule = this.productGstRules.get(Number(item.productId)) ?? GstClassification.Unknown;
      const supplierDefault = defaults?.productLineGstDefault ?? GstClassification.Unknown;
      if (item.productId && productRule !== GstClassification.Unknown) {
        item.gstClassification = productRule;
        item.gstRuleSource = 'product';
      } else if (item.productId && supplierDefault !== GstClassification.Unknown) {
        item.gstClassification = supplierDefault;
        item.gstRuleSource = 'supplier';
      } else {
        item.gstClassification = GstClassification.Unknown;
        item.gstRuleSource = undefined;
      }
    }
    if (!this.form.deliveryGstChosen) {
      this.form.deliveryGstClassification = defaults?.deliveryGstDefault ?? GstClassification.Unknown;
    }
    if (!this.form.packageGstChosen) {
      this.form.packageGstClassification = defaults?.packageGstDefault ?? GstClassification.Unknown;
    }
  }

  /** The supplier or the date changed, so a title the person has not typed follows them. */
  refreshSuggestedTitle(): void {
    if (this.titleEdited) return;
    const supplier = this.suppliers.find((s) => s.id === this.form.supplierId);
    this.form.title = purchaseTitleSuggestion(supplier?.name ?? null, this.form.purchaseDate);
  }

  /**
   * Asks the server whether a purchase with this supplier, this invoice number and this date is
   * already recorded (issue #1247), and shows what it answers.
   *
   * It is a warning and nothing else: it never blocks Save, it is never waited for, and a failed
   * lookup simply shows nothing - a purchase must be recordable when this question cannot be asked.
   * The server decides what counts as a match, including which day the date falls on in the
   * business's own calendar.
   */
  checkForPossibleDuplicate(): void {
    this.duplicateLookup?.unsubscribe();
    this.duplicateWarning = null;

    const supplierId = this.form.supplierId === '' ? null : this.form.supplierId;
    const invoiceNumber = this.form.invoiceNumber.trim() || null;
    const purchaseDate = this.purchaseTimestamp();
    if (supplierId === null || invoiceNumber === null || purchaseDate === null) {
      return;
    }

    this.duplicateLookup = this.purchaseService
      .possibleDuplicate(supplierId, invoiceNumber, purchaseDate)
      .subscribe({
        next: (match) => {
          this.duplicateWarning = match === null ? null : possibleDuplicateMessage(match);
        },
        error: () => (this.duplicateWarning = null)
      });
  }

  /** The person typed a title of their own, which no suggestion may replace from now on. */
  onTitleEdited(): void {
    this.titleEdited = true;
  }

  private prefillFromSupplierOrder(order: SupplierOrder): void {
    // Prefill supplier
    if (order.supplierId) {
      this.form.supplierId = order.supplierId;
    }

    // Set purchase date to today
    this.form.purchaseDate = PurchaseUploadComponent.localDate(new Date());
    this.refreshSuggestedTitle();

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

    // Use draft items with nullable unitCost - do NOT convert null to 0. A supplier order says
    // nothing about a line's GST status: each line's GST follows its product's rule or the
    // supplier's default (issue #1348), read below.
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
    this.loadGstRules();
  }

  /**
   * A receipt was attached, replaced (`file`) or removed (`null`) in the form. Nothing is uploaded
   * here: the file travels with the create request when the purchase is saved, so removing it before
   * then leaves nothing behind.
   */
  onReceiptChange(file: File | null): void {
    this.selectedFile = file;
    // A different document is no longer the one the storage allowance refused.
    this.storageRefusedReceipt = false;
  }

  /** Whether the form already holds items, which is what makes a scan ask before replacing them. */
  get hasLines(): boolean {
    return this.items.length > 0;
  }

  /**
   * A receipt was scanned (issue #1166). The draft fills this form for the operator to check and
   * **nothing is saved**: the purchase is still created by the ordinary create request when they
   * press Save, through the endpoint and the validation that already existed.
   *
   * Every figure is the draft's, carried through by `purchaseScanFill`; a value the receipt did not
   * carry leaves the form's own alone, which is what keeps a purchase date no receipt printed at
   * today. The scanned file becomes the purchase's document, so the save uploads the exact bytes
   * that were read, once.
   */
  onScanned(result: PurchaseReceiptScanned): void {
    const fill = purchaseScanFill(result.draft, { products: this.products, suppliers: this.suppliers });

    if (fill.fields.supplierId !== null) this.form.supplierId = fill.fields.supplierId;
    if (fill.fields.purchaseDate !== null) this.form.purchaseDate = fill.fields.purchaseDate;
    if (fill.fields.invoiceNumber !== null) this.form.invoiceNumber = fill.fields.invoiceNumber;
    if (fill.fields.receiptTotal !== null) this.form.totalAmount = fill.fields.receiptTotal;
    if (fill.fields.deliveryCost !== null) this.form.deliveryCost = fill.fields.deliveryCost;
    if (fill.fields.packageCost !== null) this.form.packageCost = fill.fields.packageCost;

    this.items = fill.lines;
    this.scannedLevies = fill.levies;
    // The receipt's GST marker is the receipt's claim, never a classification; a matched line's GST
    // follows its product's rule or the supplier's default (issue #1348).
    this.loadGstRules();
    this.selectedFile = result.file;
    this.scannedReceipt = result.file;
    this.error = '';
    this.storageRefusedReceipt = false;

    // The supplier and the date may both have moved, and a title nobody typed follows them.
    this.refreshSuggestedTitle();

    // The same receipt scanned twice is exactly what the duplicate warning is for (issue #1247),
    // and a scan fills the supplier, the invoice number and the date without anybody typing - so
    // the question is asked here rather than waiting for a field to be left.
    this.checkForPossibleDuplicate();

    // The lines are not the ones any completion projection was made against, so the question is
    // asked again rather than a stale fingerprint being resubmitted (issue #974).
    this.completionChoice = null;
    this.completionDecision = null;
    this.completionChoiceSettled = false;
  }

  /**
   * The operator chose to save the purchase without the receipt the storage allowance refused
   * (issue #968, and the optional document of #976). The filled form is untouched; only the
   * document is dropped, and this is the only thing that saves it.
   */
  saveWithoutReceipt(): void {
    if (!this.storageRefusedReceipt) {
      return;
    }

    this.selectedFile = null;
    this.storageRefusedReceipt = false;
    this.error = '';
    this.upload();
  }

  /** Back to where this purchase was started from, saving nothing. */
  cancel(): void {
    this.router.navigate([this.supplierOrderId ? '/purchases/orders' : '/purchases']);
  }

  upload(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }

    // Whatever this attempt is refused for, it is this attempt's reason: the previous refusal's
    // offer must not be left beside a different message.
    this.storageRefusedReceipt = false;

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

    // No document at all is the ordinary case since issue #976: a business with no receipt to
    // attach, or no storage left to put one in, still has to be able to record what it bought, and
    // the document can be added to the saved purchase later.
    if (!this.form.title.trim()) {
      this.error = 'Please enter a purchase title.';
      return;
    }

    // A scanned line the receipt said is not stock, and that nobody has chosen a product for, is
    // not a purchase line at all (issue #1166): it is shown, it is counted towards the receipt
    // total, and it is neither validated nor submitted. Everything else is a line.
    const savedItems = this.items.filter(item => !isScannedLineSkipped(item));

    // Every line names a product (issue #1029): a new line starts with none chosen rather than
    // silently on the first product in the list.
    const linesWithoutProduct = savedItems.filter(item => !item.productId);
    if (linesWithoutProduct.length > 0) {
      this.error = missingProductErrorMessage(linesWithoutProduct.length);
      return;
    }

    // Validate every line's quantity and price, in whichever direction it was entered (issue
    // #707): a whole number of units above zero, a finite non-negative entered amount, and a
    // finite non-negative unit cost to submit - so a blank price, a zero quantity's division and a
    // NaN are all refused here rather than sent. A scanned line with no pack size has no usable
    // quantity, so this is also what blocks Save until the units are filled in (issue #1166).
    const invalidItems = savedItems.filter(item => !isPurchaseLinePriceComplete(item));
    if (invalidItems.length > 0) {
      this.error = purchaseLinePriceErrorMessage(invalidItems.length);
      return;
    }

    this.error = '';

    // Map draft items to PurchaseItemPayload only after validation
    const validItems: PurchaseItemPayload[] = savedItems.map(item => ({
      productId: item.productId,
      quantity: item.quantity as number,  // Safe to cast after validation
      unitCost: item.unitCost as number,  // Safe to cast after validation
      gstClassification: item.gstChosen ? item.gstClassification : undefined
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
        // Sent as typed (issue #1162): the API trims it and stores blank as none, so nothing here
        // has to agree with it about what "blank" or "trimmed" mean.
        invoiceNumber: this.form.invoiceNumber || null,
        totalAmount: this.form.totalAmount,
        deliveryCost: this.form.deliveryCost,
        deliveryGstClassification: this.chargeClassification(
          this.form.deliveryCost, this.form.deliveryGstClassification, this.form.deliveryGstChosen),
        packageCost: this.form.packageCost,
        packageGstClassification: this.chargeClassification(
          this.form.packageCost, this.form.packageGstClassification, this.form.packageGstChosen),
        purchaseDate: this.purchaseTimestamp(),
        supplierId: this.form.supplierId === '' ? null : this.form.supplierId,
        items: validItems,
        completeSupplierOrderId: this.completionDecision?.supplierOrderId ?? null,
        completeSupplierOrderFingerprint: this.completionDecision?.fingerprint ?? null,
        // The same key for every attempt at this purchase (issue #1247), so a retry after a
        // timeout is answered with the purchase the first attempt saved rather than saving a
        // second one.
        idempotencyKey: this.idempotencyKey
      })
      .subscribe({
        next: () => {
          // The purchase is saved, so the key is spent: the next save this form makes - after a
          // reset, or on a page it navigates back to - is a different purchase and gets its own.
          this.idempotencyKey = generateIdempotencyKey();

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
          // The endpoint answers most refusals as a plain sentence and the reused-key conflict as a
          // ProblemDetails document (issue #1247); both are shown as they were written. The key is
          // deliberately left alone: a refused save recorded nothing, so the same key is still the
          // right one for the next attempt at this purchase.
          this.error = refusalMessage(err);
          this.saving = false;
          // The receipt is what was refused, not the purchase (issue #968): everything entered stays
          // on screen and the purchase is offered without the document, which it is allowed to have
          // none of (issue #976). Nothing is saved until that is explicitly chosen.
          this.storageRefusedReceipt = this.selectedFile !== null && isAttachmentStorageRefusal(this.error);
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
      productId: 0,
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
   * exists and a person actually picked a state for it - including Not classified, which then stays
   * unresolved rather than taking the supplier's default (issue #1348).
   */
  private chargeClassification(
    amount: number | null, classification: GstClassification, chosen: boolean | undefined): GstClassification | undefined {
    return isChargePresent(amount) && chosen ? classification : undefined;
  }
  itemProduct(item: DraftPurchaseItem): Product | undefined { return this.products.find(p => p.id === Number(item.productId)); }

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
