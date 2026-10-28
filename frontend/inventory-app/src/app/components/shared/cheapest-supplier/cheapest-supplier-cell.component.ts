import { CommonModule } from '@angular/common';
import { Component, ElementRef, HostListener, Input, ViewChild } from '@angular/core';
import {
  CheapestSupplierOffer,
  CheapestSupplierProduct,
  SupplierOfferIneligibility,
  SupplierPriceBasis,
  SupplierStockStatus
} from '../../../models/cheapest-supplier';
import { ProductPriceTrackingStatus } from '../../../models/models';
import { CheapestSuppliersState } from './cheapest-suppliers.component';

/** The note every popup carries: these are prices somebody observed, not costs this business paid. */
export const OBSERVED_PRICES_NOTE =
  'Observed supplier prices, for purchasing guidance. They are not recorded costs and never change '
  + 'what this business has paid.';

/** What the popup says about a match the collector made that nobody has checked yet (#941). */
export const AUTOMATIC_MATCH_NOTE = 'Matched automatically';

/** `$0.95`, the one money format in this column. Rounding happens here, where it is displayed. */
export function money(value: number): string {
  return `$${value.toFixed(2)}`;
}

/** `ex GST` / `inc GST`, so every figure in this column says which basis it is on. */
export function basisLabel(basis: SupplierPriceBasis | null): string {
  if (basis === SupplierPriceBasis.ExclusiveOfGst) {
    return 'ex GST';
  }
  return basis === SupplierPriceBasis.InclusiveOfGst ? 'inc GST' : '';
}

/** What the supplier said about availability, in words. `Unknown` is said, never read as "in stock". */
export function stockLabel(status: SupplierStockStatus | null): string | null {
  switch (status) {
    case SupplierStockStatus.InStock:
      return 'In stock';
    case SupplierStockStatus.OutOfStock:
      return 'Out of stock';
    case SupplierStockStatus.Unknown:
      return 'Stock unknown';
    default:
      return null;
  }
}

/**
 * Why an offer was left out of the comparison, in the owner's words rather than the enum's. The
 * offer is still listed: "BIG W is out of stock" is the answer somebody needs, and leaving the row
 * off would read as "BIG W does not sell it".
 */
export function ineligibilityLabel(reason: SupplierOfferIneligibility | null): string | null {
  switch (reason) {
    case SupplierOfferIneligibility.PackSizeUnknown:
      return 'Pack size not published, so there is no unit price';
    case SupplierOfferIneligibility.ListingNotConfirmed:
      return 'Match not confirmed, so this price is not compared';
    case SupplierOfferIneligibility.GstRateUnknown:
      return 'GST rate unknown, so this price is not compared';
    case SupplierOfferIneligibility.OutOfStock:
      return 'Out of stock';
    case SupplierOfferIneligibility.PromotionEnded:
      return 'The special has ended';
    case SupplierOfferIneligibility.Stale:
      return 'Last check is too old to compare';
    default:
      return null;
  }
}

/**
 * What one supplier's row says beside its name: its unit price on the business's basis, or the one
 * state that explains why there is none. One sentence per row, so a row is never silent.
 */
export function offerStatus(offer: CheapestSupplierOffer): string | null {
  if (!offer.hasListing) {
    return 'No match';
  }
  if (!offer.hasPrice) {
    return 'Not checked yet';
  }
  return ineligibilityLabel(offer.ineligibility);
}

/**
 * The Cheapest supplier cell's own text (issue #930's fourth criterion), and the order of the
 * checks is the point: only a successful read may say anything about a product, and "the business
 * does not track this", "nothing has matched it", "it was looked for and not found" and "no offer
 * could be compared" stay four different answers rather than one blank.
 */
export function cheapestSupplierDisplay(
  state: CheapestSuppliersState,
  product: CheapestSupplierProduct | undefined,
  displayBasis: SupplierPriceBasis | null
): string {
  if (state === 'loading') {
    return 'Loading';
  }
  if (state !== 'loaded') {
    return 'Unavailable';
  }
  if (product?.trackingStatus == null) {
    return 'Not tracked';
  }
  if (product.trackingStatus === ProductPriceTrackingStatus.NotFound) {
    return 'No match found';
  }
  if (
    product.trackingStatus === ProductPriceTrackingStatus.Requested
    || product.trackingStatus === ProductPriceTrackingStatus.AwaitingProductDetails
  ) {
    return 'Awaiting match';
  }

  const cheapest = product.suppliers.filter((offer) => offer.isCheapest);
  if (cheapest.length === 0 || product.cheapestUnitPrice === null) {
    return 'No eligible price';
  }

  const names = cheapest.map((offer) => offer.supplierName).join(' / ');
  const special = cheapest.some((offer) => offer.isSpecial) ? ' (special)' : '';

  return `${names} ${money(product.cheapestUnitPrice)}/unit ${basisLabel(displayBasis)}${special}`.trim();
}

/**
 * The Cheapest supplier cell and its popup (issue #930, parent #817) - one standalone component the
 * Dashboard's Reorder Alerts (#939), Needs Ordering and Products (#940) all use, so the three
 * columns cannot say different things.
 *
 * **It formats and nothing else.** Which supplier is cheapest, every unit price on both GST bases,
 * which basis this business sees, why an offer could not be compared and which offers tie are the
 * server's answers from the one authoritative comparison (`CheapestUnitOfferPolicy`, issue #925).
 * There is no arithmetic in this file beyond `toFixed` for display, which is the only rounding this
 * column does.
 *
 * **The popup lists every one of the business's linked, not-hidden suppliers**, cheapest first,
 * with each one's pack size, pack price, unit price on both bases, special, stock, last check and a
 * link to the supplier's own page - and the suppliers with no match or no check yet, each saying
 * so. It is a modal dialog: labelled, focused on open, closed by Escape, the backdrop or its own
 * Close button, and it returns focus to the cell that opened it.
 */
@Component({
  selector: 'app-cheapest-supplier-cell',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './cheapest-supplier-cell.component.html'
})
export class CheapestSupplierCellComponent {
  private static instances = 0;

  /** The server's answer for this product, or `undefined` while the read has not established one. */
  @Input() product?: CheapestSupplierProduct;

  /** Where the page's one cheapest-supplier read stands. */
  @Input() state: CheapestSuppliersState = 'loading';

  /** The GST basis the figures are on, as the server decided it. */
  @Input() displayBasis: SupplierPriceBasis | null = null;

  /** The product's own name, for the popup's heading and the cell's accessible label. */
  @Input() productName = '';

  readonly observedPricesNote = OBSERVED_PRICES_NOTE;
  readonly automaticMatchNote = AUTOMATIC_MATCH_NOTE;

  /**
   * This cell's own heading id, so a table of them produces no duplicate ids and every popup is
   * labelled by its own heading rather than by the first one on the page.
   */
  readonly titleId = `cheapest-supplier-title-${++CheapestSupplierCellComponent.instances}`;

  open = false;

  @ViewChild('trigger') private readonly trigger?: ElementRef<HTMLButtonElement>;

  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLElement> | undefined) {
    panel?.nativeElement.focus();
  }

  /** The cell's own text: a cheapest supplier and its unit price, or the state that explains why not. */
  get display(): string {
    return cheapestSupplierDisplay(this.state, this.product, this.displayBasis);
  }

  /**
   * Whether there is a popup to open. Only a matched product with supplier rows has one; a product
   * nothing has matched has nothing to list, and a failed read has nothing to say.
   */
  get canOpen(): boolean {
    return this.state === 'loaded' && (this.product?.suppliers.length ?? 0) > 0;
  }

  /** Whether this product's match was made automatically and has not been checked (#941). */
  get isAutomaticMatch(): boolean {
    return this.product?.trackingStatus === ProductPriceTrackingStatus.AutoMatched;
  }

  get suppliers(): CheapestSupplierOffer[] {
    return this.product?.suppliers ?? [];
  }

  openPopup(): void {
    if (this.canOpen) {
      this.open = true;
    }
  }

  closePopup(): void {
    if (!this.open) {
      return;
    }

    this.open = false;
    this.trigger?.nativeElement.focus();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closePopup();
  }

  basisOf(basis: SupplierPriceBasis | null): string {
    return basisLabel(basis);
  }

  priceOf(value: number | null): string | null {
    return value === null ? null : money(value);
  }

  stockOf(offer: CheapestSupplierOffer): string | null {
    return stockLabel(offer.stockStatus);
  }

  statusOf(offer: CheapestSupplierOffer): string | null {
    return offerStatus(offer);
  }

  /** The popup's accessible name, which names the product it is about. */
  get dialogTitle(): string {
    return `Supplier prices for ${this.productName}`;
  }
}
