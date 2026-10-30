import { CommonModule, DatePipe } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  BARCODE_SCOPE_LABELS,
  BarcodeScope,
  LISTING_MATCH_STATUS_LABELS,
  MASTER_PRODUCT_SIZE_UNIT_LABELS,
  MasterProductSizeUnit,
  ReviewSupplierListing,
  SupplierListingReviewOutcome,
  SuperAdminPriceCatalogueReviewService
} from '../../../services/super-admin-price-catalogue-review.service';
import { isSafeSupplierUrl } from '../../suppliers/supplier-url';
import { extractReviewErrorMessage, isStaleReviewState } from './review-error';

/**
 * One supplier listing awaiting a decision, with the evidence it was submitted with and the two
 * decisions a Super Admin may make about it (issue #928): confirm it - at the pack quantity, barcode
 * scope and identity as reviewed, which is how a correction is made - or refuse it.
 *
 * It is a dedicated feature component rather than markup in the page, per
 * docs/architecture.md § Page composition boundary: it owns the form the identity is corrected in,
 * its submission state and its error, and the routed page composes it through `[listing]` and reacts
 * only to `(decided)`.
 *
 * **Nothing here decides whether the pending reading becomes a price.** Whether it is fresh enough,
 * whether it describes the pack that was confirmed and whether it is inside the band are the
 * server's answers; this component shows them (`isFresh`, `needsPriceException`, and the reason the
 * decision came back with) and offers the one override the issue allows, which the server audits
 * separately.
 *
 * **Supplier text is plain text.** The title, the variant and the supplier's own product id are
 * rendered as text and nothing else; the listing's URL is the only link offered, and only when the
 * one supplier-website helper (issue #847) says it is a safe http(s) address.
 */
@Component({
  selector: 'app-supplier-listing-review',
  standalone: true,
  imports: [CommonModule, DatePipe, FormsModule],
  templateUrl: './supplier-listing-review.component.html'
})
export class SupplierListingReviewComponent implements OnChanges {
  @Input({ required: true }) listing!: ReviewSupplierListing;

  /** Raised after a decision the server accepted, so the page can re-read the queue. */
  @Output() readonly decided = new EventEmitter<string>();

  /** Raised when a refusal says the listing has been decided elsewhere since this page was read. */
  @Output() readonly staleState = new EventEmitter<void>();

  submitting = false;
  error: string | null = null;

  variant: string | null = null;
  sizeValue: number | null = null;
  sizeUnit: MasterProductSizeUnit | null = null;
  unitsPerPack: number | null = null;
  barcode: string | null = null;
  barcodeScope: BarcodeScope | null = null;
  approvePriceException = false;

  readonly sizeUnits = Object.entries(MASTER_PRODUCT_SIZE_UNIT_LABELS).map(([value, label]) => ({
    value: Number(value) as MasterProductSizeUnit,
    label
  }));

  readonly barcodeScopes = Object.entries(BARCODE_SCOPE_LABELS).map(([value, label]) => ({
    value: Number(value) as BarcodeScope,
    label
  }));

  constructor(private readonly reviewService: SuperAdminPriceCatalogueReviewService) {}

  /**
   * The form starts from what is <strong>stored</strong>, never from what was last typed: the page
   * re-reads the queue after every decision, and a correction carried onto another listing would be
   * a pack quantity nobody reviewed.
   */
  ngOnChanges(): void {
    this.error = null;
    this.submitting = false;
    this.approvePriceException = false;
    this.variant = this.listing.variant;
    this.sizeValue = this.listing.sizeValue;
    this.sizeUnit = this.listing.sizeUnit;
    this.unitsPerPack = this.listing.unitsPerPack;
    this.barcode = this.listing.barcode;
    this.barcodeScope = this.listing.barcodeScope;
  }

  get statusLabel(): string {
    return LISTING_MATCH_STATUS_LABELS[this.listing.matchStatus];
  }

  /** The master product this listing hangs from, in words, or `null` while it hangs from a search. */
  get productLabel(): string | null {
    const master = this.listing.masterProduct;

    return master === null
      ? null
      : `${master.brand} ${master.name} ${master.sizeValue}${MASTER_PRODUCT_SIZE_UNIT_LABELS[master.sizeUnit]}`;
  }

  /** What the confirmed listing a barcode match points at is called, or `null` when there is none. */
  get barcodeMatchLabel(): string | null {
    const match = this.listing.strongBarcodeMatch;

    if (match === null) {
      return null;
    }

    const master = match.masterProduct;

    return master === null
      ? `${match.supplierName} (listing ${match.supplierListingId})`
      : `${match.supplierName} · ${master.brand} ${master.name} ${master.sizeValue}${MASTER_PRODUCT_SIZE_UNIT_LABELS[master.sizeUnit]}`;
  }

  /** Whether the stored listing URL may be rendered as a link, asked of the one helper there is. */
  get canLinkToListing(): boolean {
    return isSafeSupplierUrl(this.listing.url);
  }

  /** What the size as read says, or `null` when the supplier published none. */
  get sizeAsRead(): string | null {
    return this.listing.sizeValue === null || this.listing.sizeUnit === null
      ? null
      : `${this.listing.sizeValue}${MASTER_PRODUCT_SIZE_UNIT_LABELS[this.listing.sizeUnit]}`;
  }

  /** Whether confirming would need the price exception, which is the server's own answer. */
  get needsPriceException(): boolean {
    return this.listing.pendingObservation?.needsPriceException === true;
  }

  confirm(): void {
    this.submit(SupplierListingReviewOutcome.Confirmed);
  }

  reject(): void {
    this.submit(SupplierListingReviewOutcome.Rejected);
  }

  private submit(outcome: SupplierListingReviewOutcome): void {
    this.submitting = true;
    this.error = null;

    this.reviewService
      .decideListing(this.listing.id, {
        outcome,
        unitsPerPack: this.unitsPerPack,
        barcode: this.barcode,
        barcodeScope: this.barcodeScope,
        variant: this.variant,
        sizeValue: this.sizeValue,
        sizeUnit: this.sizeUnit,
        // Read by the server only when the band is what would otherwise refuse the reading, and
        // audited separately when it is used.
        approvePriceException: this.approvePriceException
      })
      .subscribe({
        next: (result) => {
          this.submitting = false;

          if (result.outcome === SupplierListingReviewOutcome.Rejected) {
            this.decided.emit(`${this.listing.supplierName} ${this.listing.supplierProductId} was refused.`);
            return;
          }

          this.decided.emit(
            result.priceAccepted
              ? `${this.listing.supplierName} ${this.listing.supplierProductId} is confirmed, and its pending price was accepted.`
              : `${this.listing.supplierName} ${this.listing.supplierProductId} is confirmed with no current price: ${result.priceReason}`
          );
        },
        error: (err: unknown) => {
          this.submitting = false;
          this.error = extractReviewErrorMessage(err, 'Unable to record this decision.');

          if (isStaleReviewState(err)) {
            this.staleState.emit();
          }
        }
      });
  }
}
