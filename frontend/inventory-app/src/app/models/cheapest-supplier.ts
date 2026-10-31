import { ProductPriceTrackingStatus } from './models';

/**
 * What an observed or calculated supplier price is a price *of* (backend
 * `Inventory.Domain.PriceCatalogue.SupplierPriceBasis`, issue #925): with GST, or without it.
 *
 * The summary publishes which basis its `unitPrice` figures are on, decided from the business's own
 * GST registration, so a page labels what it shows rather than choosing a basis itself.
 */
export enum SupplierPriceBasis {
  ExclusiveOfGst = 1,
  InclusiveOfGst = 2
}

/**
 * What the supplier said about availability when the price was read (backend
 * `SupplierStockStatus`, issue #925). `Unknown` is a real answer and is deliberately not `InStock`:
 * a supplier that publishes a price and says nothing has not said the product is there.
 */
export enum SupplierStockStatus {
  InStock = 1,
  OutOfStock = 2,
  Unknown = 3
}

/**
 * Why one supplier's offer could not take part in the cheapest-per-unit comparison (backend
 * `SupplierOfferIneligibility`, issue #925). The offer is still shown with its reason, because an
 * owner who cannot see that a supplier is out of stock will wonder why it is not on the list.
 */
export enum SupplierOfferIneligibility {
  PackSizeUnknown = 1,
  ListingNotConfirmed = 2,
  GstRateUnknown = 3,
  OutOfStock = 4,
  PromotionEnded = 5,
  Stale = 6
}

/**
 * One supplier's offer for one product, from `POST /api/cheapest-suppliers/summary` (issue #930).
 *
 * Every price here is the server's: the unit prices are `CheapestUnitOfferPolicy`'s own arithmetic
 * on both GST bases, `unitPrice` is already the one this business is shown and compared on, and
 * `isCheapest` is its answer - every supplier in a tie carries it. The frontend formats these
 * numbers and never divides, converts or ranks them (issue #930's second criterion).
 *
 * A price that is not knowable is `null` and never `0`, which is a price: `hasListing` false is
 * "this supplier does not list the product", `hasPrice` false is "nobody has checked it yet", and
 * `ineligibility` is why an observed price was left out of the comparison.
 */
export interface CheapestSupplierOffer {
  supplierId: number;
  supplierName: string;
  hasListing: boolean;
  hasPrice: boolean;
  packQuantity: number | null;
  packPrice: number | null;
  packPriceBasis: SupplierPriceBasis | null;
  unitPrice: number | null;
  unitPriceExGst: number | null;
  unitPriceIncGst: number | null;
  isSpecial: boolean;
  promotionEndsAtUtc: string | null;
  stockStatus: SupplierStockStatus | null;
  lastCheckedAtUtc: string | null;
  productUrl: string | null;
  ineligibility: SupplierOfferIneligibility | null;
  isCheapest: boolean;
}

/**
 * One product's cheapest-supplier answer (issue #930). `trackingStatus` is `null` when the business
 * does not track the product at all, which is a different answer from a tracked product nothing has
 * matched yet - and `suppliers` is empty for both, because there is no catalogue product whose
 * offers they would be.
 */
export interface CheapestSupplierProduct {
  productId: number;
  trackingStatus: ProductPriceTrackingStatus | null;
  cheapestUnitPrice: number | null;
  suppliers: CheapestSupplierOffer[];
}

/**
 * A page's worth of cheapest-supplier answers (issue #930). `displayBasis` is the GST basis every
 * `unitPrice` in it is on - ex-GST for a GST-registered business, inc-GST otherwise - and is
 * published so the figures can be labelled rather than guessed at.
 */
export interface CheapestSupplierSummary {
  displayBasis: SupplierPriceBasis;
  products: CheapestSupplierProduct[];
}
