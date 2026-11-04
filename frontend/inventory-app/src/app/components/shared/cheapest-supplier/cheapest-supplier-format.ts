import {
  CheapestSupplierOffer,
  SupplierOfferIneligibility,
  SupplierPriceBasis,
  SupplierStockStatus
} from '../../../models/cheapest-supplier';

/**
 * The shared wording and formatting for every Cheapest supplier surface (issue #930, split out of
 * the cell by issue #1093 so the cell, the Products summary and the supplier comparison dialog all
 * import one definition without importing each other).
 */

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
