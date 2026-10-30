import { CommonModule, DatePipe } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
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
 */
@Component({
  selector: 'app-super-admin-master-product-detail',
  standalone: true,
  imports: [CommonModule, DatePipe],
  templateUrl: './master-product-detail.component.html'
})
export class MasterProductDetailComponent implements OnChanges {
  /** The master product to show the listings of. */
  @Input({ required: true }) productId!: number;

  /** Asked for when the reader closes this panel. */
  @Output() readonly closed = new EventEmitter<void>();

  detail: SuperAdminMasterProductDetail | null = null;
  loading = false;
  loadError = false;

  constructor(private readonly masterProducts: SuperAdminMasterProductsService) {}

  ngOnChanges(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = false;
    this.detail = null;

    this.masterProducts.get(this.productId).subscribe({
      next: (detail) => {
        this.detail = detail;
        this.loading = false;
      },
      error: () => {
        this.loading = false;
        this.loadError = true;
      }
    });
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
