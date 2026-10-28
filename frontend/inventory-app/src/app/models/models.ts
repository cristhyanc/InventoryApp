import { NayaxUnavailability } from '../provider/nayax-availability';

export enum StockAdjustmentReason {
  Restock = 0,
  Sale = 1,
  Damaged = 2,
  Expired = 3,
  Correction = 4,
  MachineRefill = 5
}

/**
 * A product's configured GST rule (issue #430), from `/api/products/{id}/gst-rule`. It is rule
 * configuration only: it never reclassifies an existing purchase and never affects cost.
 */
export interface ProductGstRule {
  productId: number;
  gstRule: GstClassification;
}

/**
 * A supplier's explicitly configured GST defaults (issue #430), from
 * `/api/suppliers/{id}/gst-defaults`. The delivery and package defaults are separate from the
 * product-line default because a charge never inherits a product line's classification, and a
 * default only applies where the purchased product has no rule of its own.
 */
export interface SupplierGstDefaults {
  supplierId: number;
  productLineGstDefault: GstClassification;
  deliveryGstDefault: GstClassification;
  packageGstDefault: GstClassification;
}

export interface Category {
  id: number;
  name: string;
  description?: string | null;
  /**
   * The fields this business's sales provider manages on this category (issue #736/#737):
   * `["name"]` for a category the provider named, an empty array (or absent, from an older
   * response) for one the business maintains itself. A client locks exactly these inputs.
   */
  providerManagedFields?: string[] | null;
}

/** The category a client posts to `POST /api/categories` or puts to `PUT /api/categories/{id}` (issue #737). */
export interface CategoryWriteDto {
  name: string;
  description?: string | null;
}

export interface Supplier {
  id: number;
  name: string;
  contactName?: string | null;
  phone?: string | null;
  email?: string | null;
  address?: string | null;
  /**
   * The supplier's optional website (issue #847): `null` when none is recorded, otherwise an
   * absolute http(s) address the server accepted. Render it only through `isSafeSupplierUrl`.
   */
  url?: string | null;

  /**
   * Whether this supplier came from the platform's supplier list (issue #934). A linked copy's
   * name and website are the master's and it cannot be deleted; the business hides one it does not
   * buy from with `isHidden` instead.
   *
   * The four fields below are optional because this same shape is also the nested `supplier` of a
   * purchase, a supplier order and an operating expense, which is the legacy payload and carries
   * none of them. Only `/api/suppliers` publishes them, so treat an absent value as "not stated"
   * and never as `false`: the Suppliers page reads them from its own request.
   */
  isLinked?: boolean;

  /** Whether the business has marked this supplier "Not used" (issue #934). */
  isHidden?: boolean;

  /** Whether the server will accept a change to this supplier's name (issue #934). */
  canRename?: boolean;

  /** Whether the server will accept a delete of this supplier (issue #934). */
  canDelete?: boolean;
}

/**
 * One supplier from the platform's supplier list this business may add, as
 * `GET /api/suppliers/available-presets` publishes it (issue #935).
 *
 * Three fields, which is the whole of what that endpoint sends: the id an add names, the name
 * somebody reads and the website that tells two similarly named suppliers apart. It is not a
 * {@link Supplier} and must not be rendered as one - nothing here belongs to this business yet.
 */
export interface AvailableSupplierPreset {
  id: number;
  name: string;
  url: string | null;
}

export interface Product {
  id: number;
  name: string;
  sku?: string | null;
  description?: string | null;
  /**
   * No `unitPrice` since issue #792 (A2 of #786): a product carries no catalogue selling price at
   * all, and the API sends none rather than sending `0`. What a product sells for is the machine's
   * own live price, `machinePrice` below; what it costs is `averageUnitCost`.
   */
  averageUnitCost: number;
  /**
   * The unit cost a screen may actually **display** (issue #788, B of #786): the persisted
   * {@link averageUnitCost} once a cost rebuild has established it, and **absent** while none has.
   *
   * The two keys say different things and both are needed. `averageUnitCost` is a non-nullable
   * column, so an uncosted product still carries a number in it; this one is the server's
   * authoritative statement of whether that number means anything, from the same evidence the
   * Dashboard's unknown-cost count uses (a product with no inventory value has had no rebuild). A
   * value of `0` is a measured cost and must be shown as `$0.00`; **absent must be shown as unknown
   * (e.g. `—`) and never converted to `0`**, per AGENTS.md § Frontend rules.
   *
   * Published only by the `/api/products` reads. It is deliberately absent from a machine slot's
   * product and from the product snapshot nested in a purchase or supplier-order line, where its
   * absence says nothing about the cost - exactly like {@link canDelete}.
   */
  averageUnitCostForDisplay?: number | null;
  costingQuantity?: number | null;
  inventoryValue?: number | null;
  machinePrice: number | null;
  commissionValue: number | null;
  /**
   * The machine product listing's "Estimated Card Profit"/"50% Card Margin Price" - a profit and
   * a margin-derived price, hidden from a role without `Dashboard.ViewFinancials` by the Machine
   * Detail page (issue #699); optional and nullable on the same rule as {@link Site}/
   * {@link Machine}.
   */
  suggestedNetValue?: number | null;
  suggestedPriceValue?: number | null;
  quantityInStock: number;
  maxStockInMachine: number;
  machineReplenishmentNeed: number;
  onOrderQuantity: number;
  lowStockThreshold: number;
  restockTo: number;
  needToOrder: number;
  mdbCode: number | null;
  unit?: string | null;
  lastEatBefore1?: string | null;
  lastEatBefore2?: string | null;
  createdAt: string;
  updatedAt: string;
  categoryId?: number | null;
  category?: Category | null;
  // No `supplierId`/`supplier` (issue #787, A1 of #786): a product carries no supplier. The
  // supplier of a {@link Purchase} and of a {@link SupplierOrder} is unaffected, and the per-product
  // supplier price comparison still derives every supplier a product was really bought from out of
  // the purchase history.
  isActive: boolean;
  isLowStock: boolean;
  isReorderAlert: boolean;
  /**
   * The fields this business's sales provider manages on this product (issue #736/#737):
   * `["name", "categoryId"]` for a product the provider named, an empty array (or absent, from an
   * older response) for one the business maintains itself. A client locks exactly these inputs -
   * the price and every other field stay editable either way.
   */
  providerManagedFields?: string[] | null;
  /**
   * Whether `DELETE /api/products/{id}` would allow this product to be deleted (issue #737), from
   * the catalogue listing and the single-product read. `false` means the server would refuse it and
   * {@link deletionRefusal} says why; **absent** means the read never resolved the question (a
   * reorder listing, a machine slot, a nested snapshot) and is not permission - a screen that
   * offers Delete only on `true` never offers one that must fail.
   *
   * It is advisory: the delete counts again inside its own transaction, so a refusal can still come
   * back for a product this said was deletable, and that refusal must still be shown.
   */
  canDelete?: boolean;
  /**
   * Why this product may not be deleted, in the server's own words - the message the delete would
   * answer `409` with, naming what is in the way and pointing at deactivation. Absent when the
   * product may be deleted, and when the read did not resolve the question.
   */
  deletionRefusal?: string | null;
}

/**
 * The product a client posts to `POST /api/products` (issue #737). It carries no `unitPrice` since
 * issue #792 (A2 of #786): a product has no selling price. `initialUnitCost` is a cost and stays.
 */
export interface ProductCreateDto {
  name: string;
  sku?: string | null;
  description?: string | null;
  quantityInStock: number;
  lowStockThreshold: number;
  restockTo: number;
  unit?: string | null;
  categoryId?: number | null;
  isActive: boolean;
  initialUnitCost?: number | null;
}

/**
 * One reorder-alert listing from `GET /api/products/alerts/low-stock`: the rows, plus the metadata
 * the endpoint publishes beside them as the `X-Reorder-Mode` and
 * `X-Reorder-Machine-Need-Unavailable-Reason` response headers (issue #726).
 *
 * The metadata belongs to the whole answer rather than to any row, which is exactly why an
 * **empty** listing still has it: "nothing alerts, machines included" and "nothing alerts on
 * storage stock, the machines were never read" are different answers. Both fields are `null` for a
 * response that carried no such header - an older API, or a gateway that did not forward it - and a
 * screen then shows the rows without claiming a mode (see
 * `provider/nayax-availability.ts`).
 */
export interface ReorderAlertListing {
  products: Product[];
  mode: ReorderMode | null;
  machineNeedUnavailableReason: NayaxUnavailability | null;
}

/**
 * The Dashboard "Inventory Value" tile's authoritative source: the business-owned perpetual
 * inventory value (cost basis), never a retail price. `totalInventoryValue` is `null` whenever
 * `isComplete` is `false` - a product with unknown cost must never be presented as a real `$0.00`.
 */
export interface InventoryValuationSummary {
  totalInventoryValue: number | null;
  isComplete: boolean;
  productsWithUnknownCost: number;
  totalProducts: number;
}

/**
 * One summary period, in both time bases the home Dashboard summary measures in (issue #459): the
 * UTC instants its sales are selected between (inclusive at both ends) and the business
 * dates those instants cover.
 */
export interface DashboardSummaryPeriod {
  startUtc: string;
  endUtc: string;
  firstBusinessDate: string;
  lastBusinessDate: string;
}

/**
 * The "Sales this week" card (issue #459, rendered by issue #460). `sales`/`transactionCount` are
 * known figures over `period` - zero means no completed sale was recorded, not missing data. Every
 * comparison field is `null` when `isComparisonAvailable` is `false`, and `changePercent` is also
 * `null` for a zero prior period, because there is no honest percentage change from nothing;
 * `comparisonNote` carries the reason to show instead. Angular never recomputes any of this.
 *
 * The whole object is **absent** from the summary of a caller whose role does not grant
 * `Dashboard.ViewFinancials` (issue #527); see `DashboardSummary.salesThisWeek`.
 */
export interface DashboardSalesThisWeek {
  sales: number;
  transactionCount: number;
  period: DashboardSummaryPeriod;
  comparisonPeriod: DashboardSummaryPeriod;
  isComparisonAvailable: boolean;
  comparisonSales: number | null;
  comparisonTransactionCount: number | null;
  changeAmount: number | null;
  changePercent: number | null;
  comparisonNote: string | null;
}

/**
 * The "Needs refill" card. `machinesNeedingRefill` is the distinct machine count with at least one
 * low or empty selection; the low/empty selection and machine counts are supporting detail only -
 * see `Inventory.Domain.Machines.MachineRefillAlertPolicy` for the overlap semantics this mirrors.
 * `machinesEvaluated`/`selectionsEvaluated` are what make a zero honest: zero of zero is nothing to
 * evaluate, zero of many is everything adequately stocked.
 */
export interface DashboardRefillSummary {
  machinesNeedingRefill: number;
  machinesWithEmptySelections: number;
  machinesWithLowSelections: number;
  emptySelectionCount: number;
  lowSelectionCount: number;
  machinesEvaluated: number;
  selectionsEvaluated: number;
}

/**
 * Which facts a reorder figure accounted for, as `Inventory.Application.Reorder.ReorderModes`
 * publishes the code (issue #726). `MachineAware` accounted for storage stock, the low-stock
 * threshold, the restock target, outstanding supplier orders **and** the machines' current
 * replenishment need; `StorageOnly` accounted for the four local facts with the machines' need
 * taken as `0`, for a business whose Nayax connection is unusable or whose Nayax account has not
 * granted the fleet reads.
 *
 * A mode is never inferred from a `machineReplenishmentNeed` that happens to be `0`: that `0` means
 * "the machines need nothing" in one mode and "the machines were never read" in the other.
 */
export type ReorderMode = 'StorageOnly' | 'MachineAware';

/**
 * The "Needs ordering" card: the distinct catalogue products the authoritative reorder policy says
 * must be purchased - the same set `GET /api/products/alerts/low-stock` lists for the unnarrowed
 * catalogue. `productsEvaluated` is the whole business-owned catalogue the count was taken over.
 *
 * `mode` is which facts that count accounted for and is always present (it is `required` on the
 * published DTO), so a storage-only count is never read as a machine-aware one.
 */
export interface DashboardOrderingSummary {
  productsNeedingOrdering: number;
  productsEvaluated: number;
  mode: ReorderMode;
}

/**
 * A summary section the backend deliberately did not compute for this request, published as
 * `{ "available": false, "reason": "..." }` (`UnavailableDashboardSectionDto`, issue #552). Since
 * issue #554 the "Needs refill" and "Needs ordering" cards publish this inside a support session,
 * with `reason: "SupportView"`: a session never resolves the business's Nayax credentials, so the
 * live fleet those two cards need is never read at all.
 *
 * `reason` is a stable code for branching, never a sentence to display - see
 * `support-session/support-session-messages.ts` for the one sentence this application shows for
 * `"SupportView"`.
 */
export interface UnavailableDashboardSection {
  available: false;
  reason: string;
}

/** Whether `section` is the withheld shape rather than the card's real figures. */
export function isDashboardSectionUnavailable(
  section: DashboardRefillSummary | DashboardOrderingSummary | UnavailableDashboardSection
): section is UnavailableDashboardSection {
  return (section as UnavailableDashboardSection).available === false;
}

/**
 * The "Inventory" card. The three figures have deliberately different scopes and are not
 * interchangeable: `inventoryValueAtCost` is the business-owned perpetual AVCO valuation, `null`
 * whenever `isInventoryValueComplete` is `false` (an unknown cost, never a real `$0.00`);
 * `unitsInStorage` is physical storage/home stock only - it excludes units already loaded into a
 * machine and is never a valuation input; `productCount` is the whole catalogue, active and
 * inactive, that the other two figures are taken over.
 *
 * `inventoryValueAtCost` is **absent** - not `null` - for a caller whose role does not grant
 * `Dashboard.ViewFinancials` (issue #527). The two states are different answers and must stay
 * distinguishable: `null` means the valuation is incomplete, while an absent property means the
 * amount was withheld from this caller, so the page hides the figure instead of reporting it as
 * unavailable.
 */
export interface DashboardInventorySummary {
  inventoryValueAtCost?: number | null;
  isInventoryValueComplete: boolean;
  productsWithUnknownCost: number;
  productCount: number;
  unitsInStorage: number;
}

/**
 * The authoritative home Dashboard summary contract, from `GET /api/dashboard/summary`
 * (`Inventory.Application.Dashboard.GetDashboardSummary`, issue #459) and rendered by
 * `DashboardComponent` (issue #460). The backend owns every sales, refill, ordering and valuation
 * decision behind the four headline cards; Angular displays the figures and completeness flags
 * exactly as returned, with no revenue, percentage, refill, reorder or valuation formula of its own.
 *
 * The API publishes two shapes of this response and the caller's role picks one (issue #527).
 * `salesThisWeek` and `inventory.inventoryValueAtCost` are both **absent** for a caller whose role
 * does not grant `Dashboard.ViewFinancials`, so they are declared optional here: the page hides
 * the two money cards rather than rendering a figure it was not given (issue #528). Neither is
 * ever sent as `null` or `0` to mean "withheld", and the page must not read either absence as a
 * zero week or an incomplete valuation.
 *
 * `needsRefill` and `needsOrdering` are each either the card's real figures or an
 * {@link UnavailableDashboardSection} (issue #554) - a support session's own shape of this
 * response, which replaces both with `{ available: false, reason: "SupportView" }` rather than
 * blanking a figure inside either card.
 */
export interface DashboardSummary {
  asOfUtc: string;
  businessDate: string;
  salesThisWeek?: DashboardSalesThisWeek;
  needsRefill: DashboardRefillSummary | UnavailableDashboardSection;
  needsOrdering: DashboardOrderingSummary | UnavailableDashboardSection;
  inventory: DashboardInventorySummary;
}

/**
 * One recorded actual Purchase-item cost for a product. `supplierName` is `null` exactly when the
 * source Purchase has no supplier recorded and must be presented explicitly (e.g. "None"), never
 * omitted from the history.
 */
export interface ProductPriceHistoryEntry {
  purchaseItemId: number;
  purchaseId: number;
  purchaseTitle: string;
  purchaseDate: string;
  supplierId: number | null;
  supplierName: string | null;
  unitCost: number;
}

/**
 * The supplier-product price comparison for one product: its lowest and most recent recorded
 * actual Purchase unit cost, and every recorded entry newest-first for the drill-down view.
 * `lowest`/`latest` are `null` only when the product has no Purchase history at all.
 * `percentageDifference` is `null` whenever `percentageIsMeaningful` is `false` (a zero lowest cost
 * makes the percentage undefined, not zero or infinite).
 */
export interface ProductPriceComparison {
  lowest: ProductPriceHistoryEntry | null;
  latest: ProductPriceHistoryEntry | null;
  absoluteDifference: number | null;
  percentageDifference: number | null;
  percentageIsMeaningful: boolean;
  historyNewestFirst: ProductPriceHistoryEntry[];
}

/**
 * One of the business's **own** machine records, as `/api/local-machines` publishes it (issue #740,
 * parent #329 child G1). It exists with or without a sales provider and carries no live figure at
 * all, which is what makes it a different thing from {@link Machine} - that one is the live Nayax
 * fleet with this business's sales and costs resolved onto it.
 *
 * `isProviderNamed` is whether the business's sales provider names the machine - the fact, never the
 * identifier, which is deliberately not on the wire. Nothing a client sends can change it.
 */
export interface LocalMachine {
  id: number;
  name: string;
  isActive: boolean;
  isProviderNamed: boolean;
  /**
   * Whether `DELETE /api/local-machines/{id}` would allow this machine to be deleted (issue #896),
   * from the machine list and the single-machine read. `false` means the server would refuse it and
   * {@link deletionRefusal} says why; **absent** means the read never resolved the question and is
   * not permission - a screen that offers Delete only on `true` never offers one that must fail.
   *
   * It is advisory: the delete counts the machine's references again inside its own transaction, so
   * a refusal can still come back for a machine this said was deletable, and that refusal must
   * still be shown.
   */
  canDelete?: boolean;
  /**
   * Why this machine may not be deleted, in the server's own words - the message the delete would
   * answer `400` with, naming what refers to the machine or which provider manages it. Absent when
   * the machine may be deleted, and when the read did not resolve the question. It never carries the
   * provider's identifier for the machine, only the provider's name.
   */
  deletionRefusal?: string | null;
}

/**
 * Every revenue, direct-profit and profitability-status figure is optional and nullable: a role
 * without `Dashboard.ViewFinancials` is not shown them at all, by the frontend-level check this
 * page and the Machines list/detail apply (issue #699), and the API itself is expected to start
 * withholding them the same way once #697/#698 land. The frontend must not assume any of these
 * figures is always present.
 */
export interface Machine {
  machineID: number;
  /**
   * This business's own machine record for the same machine (issue #780), or `null`/absent when it
   * has recorded none. Since issue #741 it is the id every machine page and stock workflow speaks:
   * the `api/machines/{id}` routes take it, a stock movement and a machine count record it, and the
   * stock history filters by it. `machineID` stays the provider's own number, which the pick list
   * and the report filters still speak. The two are never interchangeable: a local id equal to a
   * provider number is a coincidence. A machine without one cannot be opened or attributed.
   */
  localMachineId?: number | null;
  machineName?: string | null;
  machineNumber?: string | null;
  todayGrossRevenue?: number | null;
  currentWeekGrossRevenue?: number | null;
  lastWeekGrossRevenue?: number | null;
  twoWeeksAgoGrossRevenue?: number | null;
  todayDirectProfit: number | null;
  twoWeeksAgoDirectProfit: number | null;
  lastWeekDirectProfit: number | null;
  currentWeekDirectProfit: number | null;
  previousComparableWeekGrossRevenue?: number | null;
  previousComparableWeekDirectProfit: number | null;
  monthToDateGrossRevenue?: number | null;
  monthToDateDirectProfit: number | null;
  profitabilityStatus?: string | null;
}

/**
 * Every revenue figure is optional and nullable, on the same `Dashboard.ViewFinancials` rule as
 * {@link Machine} (issue #699).
 */
export interface Site {
  siteId: number;
  siteName: string;
  machineCount: number;
  totalStockPercentage: number;
  lowProductCount: number;
  emptyProductCount: number;
  todayRevenue?: number | null;
  currentWeekRevenue?: number | null;
  previousComparableWeekRevenue?: number | null;
}

/**
 * One row of `GET api/sites/list`, the light site list every site picker fills from (issue #841):
 * the id a filter submits, the name it shows, and how many machines the fleet has at that site. A
 * stock percentage, alert count or revenue figure would each need the per-machine Nayax read that
 * endpoint exists to avoid; a screen that shows one of those reads {@link Site} instead.
 *
 * `machineCount` is here because it is free: the backend's one fleet read already groups every
 * machine by its site, so the count is the size of that group, counted from the fleet and never
 * from product mappings - a machine with no mapped product still counts, and a machine with no site
 * counts nowhere. The Site Products subtitle states it; the two other pickers ignore it.
 */
export interface SiteListItem {
  siteId: number;
  name: string;
  machineCount: number;
}

/** One machine's MDB code for a Site Products row (issue #495); null when Nayax has none. */
export interface SiteProductMachineMdbCode {
  machineId: number;
  machineLabel: string;
  mdbCode: number | null;
}

export interface SiteProduct {
  productId: number;
  name: string;
  averageUnitCost: number | null;
  sitePrice: number;
  /**
   * Hidden from a role without `Dashboard.ViewFinancials` by the Site Products page (issue #699);
   * optional and nullable on the same rule as {@link Site}/{@link Machine}.
   */
  estimatedCardProfit?: number | null;
  quantityInStock: number;
  maxStock: number;
  /**
   * The row's representative MDB code (issue #495): the lowest non-null code across
   * `machineMdbCodes`, or null when none of them has one. Not assumed to be the product's single
   * globally unique code - see `machineMdbCodes` for the per-machine values.
   */
  mdbCode: number | null;
  machineMdbCodes: SiteProductMachineMdbCode[];
}

/** The product a client puts to `PUT /api/products/{id}`. No `unitPrice` since issue #792. */
export interface ProductUpdateDto {
  name: string;
  sku?: string | null;
  description?: string | null;
  lowStockThreshold: number;
  restockTo: number;
  unit?: string | null;
  categoryId?: number | null;
  isActive: boolean;
}

export interface SupplierOrderLine {
  id: number;
  productId: number;
  product?: Product | null;
  quantityOrdered: number;
  quantityReceived: number;
  outstandingQuantity: number;
  unitPrice?: number | null;
  notes?: string | null;
}

export interface SupplierOrder {
  id: number;
  supplierId?: number | null;
  supplier?: Supplier | null;
  orderDate: string;
  expectedDate?: string | null;
  reference?: string | null;
  notes?: string | null;
  status: number;
  lines: SupplierOrderLine[];
}

export interface SupplierOrderCreateDto {
  supplierId: number;
  orderDate: string;
  expectedDate?: string | null;
  reference?: string | null;
  notes?: string | null;
  lines: Array<{ productId: number; quantityOrdered: number; unitPrice?: number | null; notes?: string | null }>;
}

export enum StockAdjustmentSource {
  Manual = 0,
  Nayax = 1
}

export interface StockAdjustment {
  id: number;
  productId: number;
  quantityChange: number;
  quantityAfter: number;
  costingQuantityAfter?: number | null;
  averageUnitCostAfter?: number | null;
  inventoryValueAfter?: number | null;
  unitCost?: number | null;
  totalCost?: number | null;
  reason: StockAdjustmentReason;
  source: StockAdjustmentSource;
  machineId?: number | null;
  notes?: string | null;
  eatBefore?: string | null;
  createdAt: string;
}

/**
 * One movement on the global Stock History endpoint `GET /api/stock-history` (issue #384).
 *
 * It is not a {@link StockAdjustment}: the cross-product listing carries the owning product's name
 * on every row, so the page never has to look a product up per row, and it does not carry
 * `effectiveAt` - the global history orders and filters on `createdAt` only.
 *
 * `createdAt` is a true UTC instant and must be rendered with `BusinessDateTimePipe`, never the
 * built-in `date` pipe.
 */
export interface StockHistoryEntry {
  id: number;
  productId: number;
  productName: string;
  receiptItemId?: number | null;
  quantityChange: number;
  quantityAfter: number;
  unitCost?: number | null;
  totalCost?: number | null;
  costingQuantityAfter?: number | null;
  averageUnitCostAfter?: number | null;
  inventoryValueAfter?: number | null;
  reason: StockAdjustmentReason;
  source: StockAdjustmentSource;
  machineId?: number | null;
  notes?: string | null;
  eatBefore?: string | null;
  createdAt: string;
}

/**
 * One bounded page of the global Stock History. `page`/`pageSize` are what the server served, which
 * may be smaller than what was asked for: the maximum page size is the server's decision.
 * `totalCount` counts every movement matching the filters, not the rows in `items`.
 */
export interface StockHistoryPage {
  items: StockHistoryEntry[];
  page: number;
  pageSize: number;
  totalCount: number;
  hasMore: boolean;
}

/**
 * The global Stock History filters. `from`/`to` are inclusive business calendar days
 * (`yyyy-MM-dd`), which the backend converts to that business day's UTC boundaries; the browser's
 * own timezone never takes part.
 */
export interface StockHistoryFilters {
  productId?: number | null;
  from?: string | null;
  to?: string | null;
  reason?: StockAdjustmentReason | null;
  machineId?: number | null;
  source?: StockAdjustmentSource | null;
  page?: number | null;
  pageSize?: number | null;
}

export enum NayaxStockEventMatchStatus {
  Matched = 0,
  NeedsReview = 1
}

export enum NayaxStockEventProcessingStatus {
  Unprocessed = 0,
  Applied = 1
}

/**
 * An operator's explicit resolution of a Nayax event flagged as a possible duplicate of a manual
 * refill (issue #196), distinct from {@link NayaxStockEventProcessingStatus}.
 */
export enum NayaxDuplicateResolution {
  None = 0,
  ReconciledManually = 1,
  AppliedAsSeparateRestock = 2
}

/** The two explicit resolutions an operator may choose for a flagged possible duplicate (issue #196). */
export enum NayaxDuplicateResolutionChoice {
  AlreadyRecordedManually = 0,
  ApplyAsSeparateRestock = 1
}

export interface NayaxStockEventPreview {
  id: number;
  /** The Nayax EventLogID: the upstream identity of the alert. */
  nayaxEventLogId: number;
  machineId: number;
  /** The Nayax EventDateTimeGMT: the canonical event instant. */
  eventDateTimeGmt: string;
  /** The Nayax EventDateTimeVMC (machine clock); null for events imported before it was kept. */
  eventDateTimeVmc: string | null;
  rawEventData: string;
  parsedMdb: number | null;
  parsedProductName: string | null;
  parsedQuantity: number | null;
  matchedProductId: number | null;
  matchedProductName: string | null;
  matchStatus: NayaxStockEventMatchStatus;
  needsReviewReason: string | null;
  processingStatus: NayaxStockEventProcessingStatus;
  availableStorageQuantity: number | null;
  isInsufficientStock: boolean;
  unaccountedDifference: number | null;
  isDiscrepancy: boolean;
  isPossibleDuplicate: boolean;
  possibleDuplicateNotes: string | null;
  duplicateResolution: NayaxDuplicateResolution;
}

export interface NayaxProductImpactPreview {
  productId: number;
  productName: string;
  availableStorageQuantity: number;
  pendingRefillQuantity: number;
}

export interface NayaxMachineStockSyncPreview {
  machineId: number;
  newEventCount: number;
  events: NayaxStockEventPreview[];
  productImpacts: NayaxProductImpactPreview[];
  /** Reconciled-manually events in the current From date window hidden because Show reconciled is off. */
  hiddenReconciledCount: number;
  message: string | null;
}

/**
 * One machine on the Dashboard's restock-sync reminder (issue #891), exactly as
 * `GET /api/machine-restock-sync/reminder` published it. Nothing here is derived in the browser:
 * the three-day rule, the business dates and the pending definition are all the backend's.
 *
 * `lastSuccessfulSyncAtUtc` is `null` for "No recorded sync", which includes coverage recorded
 * under Nayax credentials that have since been replaced. It is never read as "never synced":
 * machines restocked before this metadata existed have no record of syncs that really happened.
 *
 * `knownPendingEventCount` counts **persisted** imported events that are still unresolved. A zero
 * says nothing about alerts Nayax holds that nobody has fetched - that is what `isFetchOverdue`
 * is for - and the two conditions are deliberately reported separately.
 */
export interface MachineRestockSyncReminderMachine {
  localMachineId: number;
  machineName: string;
  lastSuccessfulSyncAtUtc: string | null;
  lastSuccessfulSyncBusinessDate: string | null;
  daysSinceLastSuccessfulSync: number | null;
  isFetchOverdue: boolean;
  knownPendingEventCount: number;
  oldestKnownPendingEventAtUtc: string | null;
  /**
   * The business calendar date (`yyyy-MM-ddT00:00:00`) of that oldest counted event, or `null`
   * (issue #948). The machine link hands its date part to Sync Restock as `restockFrom`.
   */
  oldestKnownPendingEventBusinessDate: string | null;
  /** Some of that pending work predates the Sync Restock dialog's default From date. */
  hasPendingWorkBeforeDefaultFrom: boolean;
}

/**
 * Whether a completed Nayax connection check confirmed the machine alerts read behind Sync
 * Restock (`MachineRestockSyncAvailability`, issue #891). A stable code, never a sentence: with
 * `NotConfirmed` the reminder says the sync may be unavailable instead of presenting it as a
 * working action.
 */
export type MachineRestockSyncAvailability = 'Confirmed' | 'NotConfirmed';

/** The reminder itself: the provider-named machines whose Sync Restock needs attention. */
export interface MachineRestockSyncReminder {
  applicable: true;
  asOfUtc: string;
  businessDate: string;
  overdueAfterDays: number;
  /** The From date the Sync Restock dialog will default to, in the business's own timezone. */
  defaultFromBusinessDate: string;
  /** How many business calendar days back pending events are counted (issue #948). */
  pendingLookbackDays: number;
  /** The first business calendar date whose pending events are counted (issue #948). */
  pendingFromBusinessDate: string;
  syncAvailability: MachineRestockSyncAvailability;
  machines: MachineRestockSyncReminderMachine[];
}

/**
 * The reminder as it is withheld (issue #891), for a support session, a business with no usable
 * Nayax connection, or one whose provider names none of its machines. `reason` is a stable code
 * for branching, never a sentence to display.
 */
export interface HiddenMachineRestockSyncReminder {
  applicable: false;
  reason: string;
}

export type MachineRestockSyncReminderResponse =
  | MachineRestockSyncReminder
  | HiddenMachineRestockSyncReminder;

/** Whether the response is the reminder rather than the withheld shape. */
export function isMachineRestockSyncReminderApplicable(
  response: MachineRestockSyncReminderResponse
): response is MachineRestockSyncReminder {
  return response.applicable === true;
}

export enum NayaxStockEventApplyOutcome {
  Applied = 0,
  InsufficientStock = 1,
  NotMatched = 2,
  NotApplicable = 3,
  Error = 4,
  /** Reconciled as already recorded manually (issue #196): no movement was applied. */
  Reconciled = 5,
  /** Flagged as a possible duplicate and not yet explicitly resolved; the ordinary Apply action refused it. */
  DuplicateRequiresResolution = 6
}

export interface NayaxStockEventApplyResult {
  eventId: number;
  outcome: NayaxStockEventApplyOutcome;
  message: string;
  stockAdjustmentId: number | null;
}

export interface NayaxMachineStockApplyResponse {
  results: NayaxStockEventApplyResult[];
}

/**
 * Why a machine's latest provider sales could not be read (issue #768). `NoSalesProvider` is a
 * business that has no sales provider at all (#759) and is **not** a failure; the other two are the
 * same two per-feature availability answers the reorder and site-commission surfaces publish.
 */
export type MachineLatestSalesUnavailableReason =
  | 'NoSalesProvider'
  | 'NayaxNotConnected'
  | 'PermissionNotGranted';

/**
 * One sale in a machine's latest Nayax sales window (issue #768).
 *
 * Every field is nullable because the provider reports it as nullable, or because the API withholds
 * it. `saleAmount` and `currencyCode` are `null` for a role without `Dashboard.ViewFinancials` -
 * the API withholds them, never sends `0` - and `saleDateTimeUtc` is `null` when the payload
 * carried no usable instant. Never convert any of them to `0` or to an empty string: an absent
 * value and a reported one have to stay distinguishable.
 */
export interface MachineLatestSale {
  transactionId: number;
  /** A true UTC instant, rendered in the current business's own time zone. */
  saleDateTimeUtc: string | null;
  productName: string | null;
  paymentMethod: string | null;
  saleAmount: number | null;
  currencyCode: string | null;
}

/**
 * The machine's latest provider sales (issue #768): the provider's own rolling window, newest
 * first, or the reason there are none to show.
 *
 * `isAvailable: false` with an empty `sales` list means "nothing could be read" and must never be
 * presented as "this machine has sold nothing"; an available result with an empty list is the
 * honest empty state. The response carries no date range or row count, because the Nayax endpoint
 * behind it guarantees neither.
 */
export interface MachineLatestSales {
  machineId: number;
  isAvailable: boolean;
  unavailableReason: MachineLatestSalesUnavailableReason | null;
  unavailableDetail: string | null;
  sales: MachineLatestSale[];
}

export interface StockAdjustmentDto {
  quantityChange: number;
  reason: StockAdjustmentReason;
  notes?: string | null;
  machineId?: number | null;
  EatBefore?: string | null;
  unitCost?: number | null;
}

export interface RestockCostSuggestion {
  unitCost: number | null;
  source: 'LastPurchase' | 'AverageUnitCost' | 'None';
  purchaseDate: string | null;
}

/**
 * The GST status of one purchase component - a purchase line, or the purchase's delivery or package
 * charge (issue #429, under the approved GST design of parent issue #62). The numbers are the
 * contract the API serializes, so they must stay in step with the backend
 * `Inventory.Domain.Gst.GstClassification`.
 *
 * `Unknown` is a real persisted state, not a missing value: it contributes no input GST and stays
 * visibly unresolved. Never present it as GST-free, and never derive GST from an amount here - the
 * server returns the calculated figures (`PurchaseGstSummary`).
 *
 * The same vocabulary describes a product's GST rule and a supplier's GST defaults (issue #430);
 * there `Unknown` means "no rule configured", which must stay distinct from an explicit `GstFree`
 * rule.
 */
export enum GstClassification {
  Unknown = 0,
  Taxable = 1,
  GstFree = 2
}

/**
 * How a `GstClassification` was established, as the API returns it (backend
 * `Inventory.Domain.Gst.GstClassificationSource`). It is audit provenance, never submitted by the
 * client: the server records a person's explicit choice as `Manual`. A purchase edit therefore omits
 * the classifications the person did not change, so a rule-derived provenance survives an unrelated
 * edit.
 */
export enum GstClassificationSource {
  Unknown = 0,
  Manual = 1,
  ProductRule = 2,
  SupplierDefault = 3,
  SupplierFeeDefault = 4
}

// The Purchase business record served under the "/api/purchases" JSON contract
// (see backend Purchase.cs / PurchaseResponseDto).
export interface Purchase {
  id: number;
  title: string;
  notes?: string | null;
  totalAmount?: number | null;
  deliveryCost?: number | null;
  deliveryGstClassification?: GstClassification;
  deliveryGstClassificationSource?: GstClassificationSource;
  packageCost?: number | null;
  packageGstClassification?: GstClassification;
  packageGstClassificationSource?: GstClassificationSource;
  purchaseDate: string;
  supplierId?: number | null;
  supplier?: Supplier | null;
  fileName: string;
  storedFileName: string;
  contentType: string;
  fileSizeBytes: number;
  createdAt: string;
  items?: PurchaseItem[];
}

export interface PurchaseValidation {
  hasTotalMismatch: boolean;
  calculatedItemSubtotal?: number | null;
  calculatedTotal?: number | null;
  totalDifference?: number | null;
}

/**
 * The saved purchase's input GST as the API calculated it (issue #431). `unresolvedComponentCount`
 * and `unresolvedAmount` cover the components nobody has classified; they are reported separately so
 * an incomplete purchase never looks like a resolved $0. Absent delivery/package charges are not
 * components and are never unresolved.
 *
 * These figures describe the saved purchase. They do not reflect unsaved edits.
 */
export interface PurchaseGstSummary {
  inputGst: number;
  unresolvedComponentCount: number;
  unresolvedAmount: number;
}

export interface PurchaseResponse {
  purchase: Purchase;
  validation?: PurchaseValidation | null;
  gst?: PurchaseGstSummary | null;
}

export interface PurchaseItem {
  id?: number;
  receiptId?: number;
  productId: number;
  product?: Product | null;
  quantity: number;
  unitCost: number;
  gstClassification?: GstClassification;
  gstClassificationSource?: GstClassificationSource;
  lineTotal?: number;
}

/**
 * One machine's current/target/pick figures for a product in the Pick List matrix (issue #221/#222),
 * from the read-only `GET /api/pick-list` projection.
 */
export interface PickListMachineQuantity {
  machineId: number;
  /** The MDB code for this specific machine-product entry (issue #496); null when Nayax has none. */
  mdbCode: number | null;
  currentQuantity: number;
  targetQuantity: number;
  quantityToPick: number;
}

/** One product row of the Pick List matrix. */
export interface PickListProduct {
  productId: number;
  productName: string;
  /**
   * The row's representative MDB code (issue #496): the lowest non-null code across
   * `machineQuantities`, or null when none of them has one. Not assumed to be the product's single
   * globally unique code - see each `machineQuantities[].mdbCode` for the per-machine value.
   */
  mdbCode: number | null;
  storageQuantityInStock: number;
  totalQuantityToPick: number;
  storageShortageQuantity: number;
  machineQuantities: PickListMachineQuantity[];
}

/** The complete read-only Pick List projection for the requested machines. */
export interface PickListResult {
  products: PickListProduct[];
}

/**
 * One row of a `POST /api/pick-list/refills` request (issue #743, frontend #744): a product of the
 * caller's own catalogue and the whole number of units taken out of storage for it.
 */
export interface PickListRefillRowRequest {
  productId: number;
  quantityTaken: number;
}

/**
 * A batch refill request (issue #743): one request recording everything a worker took out of
 * storage. Since issue #904 this frontend sends it only for the manual pick list's **No machine**
 * mode, so `machineId` is never set: a pick that names any machine is sent as a
 * {@link MultiMachinePickListRefillRequest} instead, which attributes each line to its own machine.
 * The field stays on the contract because the endpoint still accepts it.
 */
export interface PickListRefillRequest {
  idempotencyKey: string;
  machineId?: number | null;
  notes?: string | null;
  rows: PickListRefillRowRequest[];
}

/** One recorded row of a batch refill: the product, the units taken, and the movement it created. */
export interface PickListRefillRecordedRow {
  productId: number;
  quantityTaken: number;
  stockAdjustmentId: number;
}

/** What `POST /api/pick-list/refills` answers, for a newly recorded refill and a replayed retry alike. */
export interface PickListRefillResponse {
  idempotencyKey: string;
  replayed: boolean;
  machineId: number | null;
  notes: string | null;
  recordedAtUtc: string;
  rows: PickListRefillRecordedRow[];
}

/**
 * One allocation of a `POST /api/pick-list/multi-machine-refills` request (issue #897, frontend
 * #904): a **local** machine of the caller's own business (`LocalMachine.id`, never a provider
 * machine number), a product of its own catalogue, and the whole number of units taken out of
 * storage for that pair. Every allocation names a machine; a refill for no machine at all is still
 * a {@link PickListRefillRequest}.
 */
export interface MultiMachineRefillAllocationRequest {
  machineId: number;
  productId: number;
  quantityTaken: number;
}

/**
 * A multi-machine refill batch (issue #897): one request recording a whole pick run, machine by
 * machine, all or nothing. `idempotencyKey` is the client's own string for one submission - the same
 * key with the same allocations, in any order, is answered with what the server already recorded,
 * and the key namespace is shared with {@link PickListRefillRequest}.
 */
export interface MultiMachinePickListRefillRequest {
  idempotencyKey: string;
  notes?: string | null;
  allocations: MultiMachineRefillAllocationRequest[];
}

/** One recorded allocation of a batch: the machine, the product, the units taken, and the movement it created. */
export interface MultiMachinePickListRefillRecordedAllocation {
  machineId: number;
  productId: number;
  quantityTaken: number;
  stockAdjustmentId: number;
}

/**
 * What `POST /api/pick-list/multi-machine-refills` answers, for a newly recorded batch and a
 * replayed retry alike; `replayed` is `true` when the key had already recorded it and nothing new
 * was written.
 */
export interface MultiMachinePickListRefillResponse {
  idempotencyKey: string;
  replayed: boolean;
  notes: string | null;
  recordedAtUtc: string;
  allocations: MultiMachinePickListRefillRecordedAllocation[];
}

/** Whether a Take Inventory Apply request created a stock movement (issue #245). */
export enum InventoryCountApplyOutcome {
  /** Counted matched the authoritative current quantity: no stock movement was created. */
  Confirmed = 0,
  /** A non-zero difference was applied through the existing Restock/Correction movement. */
  Applied = 1
}

/** One product's Take Inventory Apply request. `expectedCurrentStock` is the quantity the operator counted against, so the backend can refuse a stale count instead of silently overwriting a concurrent change. */
export interface InventoryCountApplyRequest {
  countedStock: number;
  expectedCurrentStock: number;
}

export interface InventoryCountApplyResult {
  outcome: InventoryCountApplyOutcome;
  quantityChange: number;
  currentStock: number;
  stockAdjustmentId: number | null;
}
