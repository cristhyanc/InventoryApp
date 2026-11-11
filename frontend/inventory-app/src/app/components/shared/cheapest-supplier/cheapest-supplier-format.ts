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

/**
 * The note under every observed price (owner decision, 4 November 2026): every price in this column
 * was read from a supplier's website, and a website's price (an online-only special, another
 * state's online store) can differ from the shelf price in the store this business buys from. It
 * is a label and nothing more: an online price is still compared and can still be the cheapest.
 */
export const ONLINE_PRICE_NOTE = 'Online price, may differ in store';

/** The online-price note for one popup row: any row that shows a price, and none that shows no price. */
export function offerOnlinePriceNote(offer: CheapestSupplierOffer): string | null {
  return offer.unitPrice !== null || offer.packPrice !== null ? ONLINE_PRICE_NOTE : null;
}

/** What the popup says about a match the collector made that nobody has checked yet (#941). */
export const AUTOMATIC_MATCH_NOTE = 'Matched automatically';

/**
 * The notice above the comparison table (issue #1183's owner-approved wording), visible without
 * hovering or scrolling through the results: the comparison's scope and its limits, so the badge
 * below is never read as a guarantee across the whole market.
 */
export const SUPPLIER_COMPARISON_NOTICE =
  'Check before buying: Prices and availability may change and can vary by location, membership or '
  + 'promotion. Comparisons use recorded unit prices from supported suppliers and exclude delivery '
  + 'and additional fees. Confirm the product, pack size and final total on the supplier’s '
  + 'website before purchasing.';

/** The badge on the comparison's lowest-priced row or rows (issue #1183, replacing "Cheapest"). */
export const LOWEST_UNIT_PRICE_LABEL = 'Lowest unit price';

/**
 * What the {@link LOWEST_UNIT_PRICE_LABEL} badge means (issue #1183), said once beside the table
 * rather than repeated on every row that carries it.
 */
export const LOWEST_UNIT_PRICE_EXPLAINER =
  'Lowest recorded unit price among the eligible offers shown, excluding delivery and additional '
  + 'fees. Check the supplier’s final price before buying.';

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
