import { CommonModule, DatePipe } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';
import { Subject, Subscription, catchError, map, of, switchMap, tap } from 'rxjs';
import {
  SuperAdminMasterProductDetail,
  SuperAdminMasterProductListing,
  SuperAdminMasterProductsService
} from '../../../services/super-admin-master-products.service';
import {
  LISTING_MATCH_STATUS_LABELS,
  MASTER_PRODUCT_SIZE_UNIT_LABELS,
  SupplierPriceBasis
} from '../../../services/super-admin-price-catalogue-review.service';
import { isSafeSupplierUrl } from '../../suppliers/supplier-url';

/** A read tagged with the product it was issued for; `detail` is null when that read failed. */
type MasterProductRead = { id: number; detail: SuperAdminMasterProductDetail | null };

/**
 * One master product's supplier listings (issue #1023): which suppliers publish it, the pack each one
 * describes, what the catalogue thinks of each listing, when it was last checked and what it currently
 * costs.
 *
 * It is a feature component of its own rather than markup inside the Master products page, per
 * docs/architecture.md § Page composition boundary: it owns a distinct read with its own loading and
 * error lifecycle, keyed on the product the page selected.
 *
 * **It displays and never calculates.** A pack price is published with the currency, the tax basis and
 * the instant it was observed, and all three are shown as they were stored: dividing a pack price by a
 * pack quantity, or converting between GST bases, is the cheapest-supplier comparison's work on its
 * own endpoint and its own rule, and a second implementation here would be a second answer.
 *
 * **It is read only**: there is no control here that confirms, corrects, refuses or deletes anything -
 * that is the Supplier Price Review's authority (issue #928).
 *
 * **Only the product it was last given may be described here.** The page reuses this one component for
 * whichever row is open, so viewing product A's listings and then product B's must never let A's
 * slower answer - or A's failure - describe B. The `switchMap` cancels the read still in flight and
 * the identity check discards an answer that arrives anyway, exactly as the product price-tracking
 * panel does it.
 */
@Component({
  selector: 'app-super-admin-master-product-detail',
  standalone: true,
  imports: [CommonModule, DatePipe],
  templateUrl: './master-product-detail.component.html'
})
export class MasterProductDetailComponent implements OnChanges, OnDestroy {
  /** The master product to show the listings of. */
  @Input({ required: true }) productId!: number;

  /** Asked for when the reader closes this panel. */
  @Output() readonly closed = new EventEmitter<void>();

  detail: SuperAdminMasterProductDetail | null = null;
  loading = false;
  loadError = false;

  private readonly requested = new Subject<number>();
  private readonly reads: Subscription;

  constructor(private readonly masterProducts: SuperAdminMasterProductsService) {
    this.reads = this.requested
      .pipe(
        tap(() => {
          this.loading = true;
          this.loadError = false;
          this.detail = null;
        }),
        switchMap((id) =>
          this.masterProducts.get(id).pipe(
            map((detail): MasterProductRead => ({ id, detail })),
            // A failed read travels as a result rather than as an error, so one failure cannot end
            // the stream and leave every later selection unanswered.
            catchError(() => of<MasterProductRead>({ id, detail: null }))
          )
        )
      )
      .subscribe(({ id, detail }) => {
        if (id !== this.productId) {
          return;
        }

        this.loading = false;

        if (detail === null) {
          this.loadError = true;
          return;
        }

        this.detail = detail;
      });
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productId']) {
      this.requested.next(this.productId);
    }
  }

  ngOnDestroy(): void {
    this.reads.unsubscribe();
    this.requested.complete();
  }

  /** Re-reads the product this panel is about, abandoning whatever read is still in flight. */
  load(): void {
    this.requested.next(this.productId);
  }

  /** What the catalogue thinks of one listing, in the server's own vocabulary. */
  statusLabel(listing: SuperAdminMasterProductListing): string {
    return LISTING_MATCH_STATUS_LABELS[listing.matchStatus];
  }

  /** The individual size as the supplier published it, or `null` when it published none. */
  sizeLabel(listing: SuperAdminMasterProductListing): string | null {
    if (listing.sizeValue === null || listing.sizeUnit === null) {
      return null;
    }

    const unit = MASTER_PRODUCT_SIZE_UNIT_LABELS[listing.sizeUnit];
    return unit === 'each' ? `${listing.sizeValue} each` : `${listing.sizeValue} ${unit}`;
  }

  /**
   * What a published pack price means, spelled out rather than shown as a flag: "incl. GST" and
   * "excl. GST" are different figures and neither is the absence of the other.
   */
  basisLabel(listing: SuperAdminMasterProductListing): string {
    return listing.latestPrice?.priceBasis === SupplierPriceBasis.InclusiveOfGst ? 'incl. GST' : 'excl. GST';
  }

  /**
   * Whether a stored listing URL may be rendered as a link, asked of the one supplier website helper
   * there is (issue #847). Anything else is shown as plain text, exactly as the Suppliers list does
   * it - a listing URL is a supplier's own value and is never trusted as markup.
   */
  canLinkTo(listing: SuperAdminMasterProductListing): boolean {
    return isSafeSupplierUrl(listing.url);
  }

  close(): void {
    this.closed.emit();
  }
}
