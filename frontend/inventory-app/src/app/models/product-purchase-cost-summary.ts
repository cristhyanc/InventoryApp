/**
 * One product's last and cheapest recorded actual purchase cost, with the supplier each one was
 * paid to, as `GET /api/products/purchase-cost-summary` publishes it (issue #788, B of #786).
 *
 * It is the bulk form of the `latest`/`lowest` entries {@link ProductPriceComparison} already
 * carries for one product, derived by the same authoritative backend calculation
 * (`Inventory.Domain.Purchases.SupplierPriceComparisonPolicy`). **Nothing in the frontend may
 * recompute a last or cheapest cost from a history, or compare two of these to derive a saving** -
 * that is the one place those decisions are made, and a second copy here could only disagree with
 * the product's own price-history view.
 *
 * A summary exists only for a product with at least one recorded purchase line, which is why every
 * cost and date is non-nullable: a product that has never been bought is **absent** from the array
 * rather than present with nulls or zeros. A screen must therefore look its product up by
 * `productId` and show "no purchases recorded" when it finds nothing - never a `0`.
 *
 * `lastSupplierName`/`lowestSupplierName` are `null` exactly when the purchase they come from has
 * no supplier recorded, and must be presented explicitly (e.g. "None"), never omitted and never
 * replaced with another purchase's supplier.
 *
 * A `unitCost` of `0` is a real recorded cost - a free sample, a replacement delivery - and is not
 * a missing figure. Both costs are plain numbers and are formatted only for display, per
 * AGENTS.md § Frontend rules.
 *
 * `lastPurchaseDate`/`lowestPurchaseDate` are the purchase's own recorded date, not an instant.
 */
export interface ProductPurchaseCostSummary {
  productId: number;
  lastUnitCost: number;
  lastSupplierName: string | null;
  lastPurchaseDate: string;
  lowestUnitCost: number;
  lowestSupplierName: string | null;
  lowestPurchaseDate: string;
}
