import { Component, ElementRef, Input, ViewChild } from '@angular/core';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import {
  CheapestSupplierOffer,
  CheapestSupplierProduct,
  SupplierOfferIneligibility,
  SupplierPriceBasis
} from '../../../models/cheapest-supplier';
import { ProductPriceTrackingStatus } from '../../../models/models';
import { CheapestSuppliersState } from './cheapest-suppliers.component';
import { basisLabel, money } from './cheapest-supplier-format';
import { SupplierComparisonDialogComponent } from './supplier-comparison-dialog.component';

/**
 * What the Products page's Cheapest supplier cell says about one product (issue #1093). Each state
 * stays its own answer, in the order issue #930 decided them: only a successful read may say
 * anything about a product, "not tracked", "nothing matched it", "still waiting", "only stale
 * prices" and "no offer could be compared" are different things, and none of them is a price.
 */
export type CheapestSupplierSummaryView =
  | { readonly kind: 'loading' }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'notTracked' }
  | { readonly kind: 'noMatch' }
  | { readonly kind: 'awaitingMatch' }
  | { readonly kind: 'noOffers' }
  | {
      readonly kind: 'price';
      readonly unitPrice: string;
      readonly basis: string;
      readonly supplierNames: readonly string[];
      readonly tied: boolean;
      readonly special: boolean;
      /**
       * Every offer behind {@link supplierNames}, in the same order, for the View link beside each
       * one (issue #1098): the server's own `isCheapest` offers, never a second opinion about which
       * is cheapest or an offer that fell out of the tie since the read.
       */
      readonly cheapestOffers: readonly CheapestSupplierOffer[];
    }
  | {
      readonly kind: 'stale';
      readonly unitPrice: string | null;
      readonly basis: string;
      readonly supplierName: string;
      readonly lastCheckedAtUtc: string | null;
    };

/** The words for each state that is a statement rather than a price. */
export const SUMMARY_STATE_LABELS = {
  loading: 'Loading',
  unavailable: 'Unavailable',
  notTracked: 'Not tracked',
  noMatch: 'No match found',
  awaitingMatch: 'Awaiting match',
  noOffers: 'No current offers'
} as const;

/**
 * The summary for one product. It formats the server's answer and decides nothing: which offers
 * are cheapest (every one in a tie carries `isCheapest`), the unit price and its basis, whether an
 * offer is a special and why one could not be compared are all the server's.
 *
 * "Stale" is shown only when nothing could be compared and the server said at least one offer was
 * left out because its last check is too old - and then it shows that offer's own observed price
 * and when it was checked, labelled as stale, never as the cheapest.
 */
export function cheapestSupplierSummaryView(
  state: CheapestSuppliersState,
  product: CheapestSupplierProduct | undefined,
  displayBasis: SupplierPriceBasis | null
): CheapestSupplierSummaryView {
  if (state === 'loading') {
    return { kind: 'loading' };
  }
  if (state !== 'loaded') {
    return { kind: 'unavailable' };
  }
  if (product?.trackingStatus == null) {
    return { kind: 'notTracked' };
  }
  if (product.trackingStatus === ProductPriceTrackingStatus.NotFound) {
    return { kind: 'noMatch' };
  }
  if (
    product.trackingStatus === ProductPriceTrackingStatus.Requested
    || product.trackingStatus === ProductPriceTrackingStatus.AwaitingProductDetails
  ) {
    return { kind: 'awaitingMatch' };
  }

  const cheapest = product.suppliers.filter((offer) => offer.isCheapest);
  if (cheapest.length > 0 && product.cheapestUnitPrice !== null) {
    return {
      kind: 'price',
      unitPrice: money(product.cheapestUnitPrice),
      basis: basisLabel(displayBasis),
      supplierNames: cheapest.map((offer) => offer.supplierName),
      tied: cheapest.length > 1,
      special: cheapest.some((offer) => offer.isSpecial),
      cheapestOffers: cheapest
    };
  }

  const stale = product.suppliers.find((offer) => offer.ineligibility === SupplierOfferIneligibility.Stale);
  if (stale !== undefined) {
    return {
      kind: 'stale',
      unitPrice: stale.unitPrice === null ? null : money(stale.unitPrice),
      basis: stale.unitPrice === null ? '' : basisLabel(displayBasis),
      supplierName: stale.supplierName,
      lastCheckedAtUtc: stale.lastCheckedAtUtc
    };
  }

  return { kind: 'noOffers' };
}

/**
 * The Products page's Cheapest supplier cell (issue #1093): the comparable unit price first, with
 * the GST basis the server put it on, the supplier (or tied suppliers) beneath it, a Special badge
 * when the server says so, and "Compare (N)" to open the shared supplier comparison. The same
 * component renders the desktop cell and the narrow-screen card's supplier line, so the two can
 * never disagree.
 *
 * Each cheapest offer with a `productUrl` also gets its own View link (issue #1098), opening that
 * supplier's product page directly in a new tab rather than the comparison popup - never a link
 * built from an arbitrary member of a tie, and never shown for a non-cheapest or stale offer.
 *
 * General guidance - that these are observed prices, and that a supplier's stock may differ - is
 * said once in the column heading's help, not repeated in every row.
 */
@Component({
  selector: 'app-cheapest-supplier-summary',
  standalone: true,
  imports: [BusinessDateTimePipe, SupplierComparisonDialogComponent],
  templateUrl: './cheapest-supplier-summary.component.html'
})
export class CheapestSupplierSummaryComponent {
  @Input() product?: CheapestSupplierProduct;
  @Input() state: CheapestSuppliersState = 'loading';
  @Input() displayBasis: SupplierPriceBasis | null = null;
  @Input() productName = '';
  @Input() productSku: string | null = null;
  @Input() categoryName: string | null = null;
  /** `cell` stacks the price over the supplier; `card` right-aligns both for the narrow card. */
  @Input() layout: 'cell' | 'card' = 'cell';

  readonly labels = SUMMARY_STATE_LABELS;

  open = false;

  @ViewChild('trigger') private readonly trigger?: ElementRef<HTMLButtonElement>;

  /** The words for a state that is a statement rather than a price. */
  stateLabel(kind: CheapestSupplierSummaryView['kind']): string {
    return (SUMMARY_STATE_LABELS as Record<string, string>)[kind] ?? '';
  }

  get view(): CheapestSupplierSummaryView {
    return cheapestSupplierSummaryView(this.state, this.product, this.displayBasis);
  }

  /** How many suppliers the comparison lists; there is a comparison only once the read has some. */
  get supplierCount(): number {
    return this.state === 'loaded' ? (this.product?.suppliers.length ?? 0) : 0;
  }

  get canOpen(): boolean {
    return this.supplierCount > 0;
  }

  /** Whether any of the cheapest offers has a URL to link to, so the View row renders only then. */
  hasAnyProductUrl(offers: readonly CheapestSupplierOffer[]): boolean {
    return offers.some((offer) => offer.productUrl !== null);
  }

  openComparison(): void {
    if (this.canOpen) {
      this.open = true;
    }
  }

  onComparisonClosed(): void {
    this.open = false;
    this.trigger?.nativeElement.focus();
  }
}
