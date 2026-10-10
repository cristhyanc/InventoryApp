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
}

export interface Supplier {
  id: number;
  name: string;
  contactName?: string | null;
  phone?: string | null;
  email?: string | null;
  address?: string | null;
}

export interface Product {
  id: number;
  name: string;
  sku?: string | null;
  description?: string | null;
  unitPrice: number;
  averageUnitCost: number;
  costingQuantity?: number | null;
  inventoryValue?: number | null;
  machinePrice: number | null;
  commissionValue: number | null;
  suggestedNetValue: number | null;
  suggestedPriceValue: number | null;
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
  supplierId?: number | null;
  supplier?: Supplier | null;
  isActive: boolean;
  isLowStock: boolean;
  isReorderAlert: boolean;
}

/**
 * The Dashboard "Inventory Value" tile's authoritative source: the business-owned perpetual
 * inventory value (cost basis), never retail UnitPrice. `totalInventoryValue` is `null` whenever
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
 * UTC instants its sales are selected between (inclusive at both ends) and the Sydney business
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
 * The "Needs ordering" card: the distinct catalogue products the authoritative reorder policy says
 * must be purchased - the same set `GET /api/products/alerts/low-stock` lists for the unnarrowed
 * catalogue. `productsEvaluated` is the whole business-owned catalogue the count was taken over.
 */
export interface DashboardOrderingSummary {
  productsNeedingOrdering: number;
  productsEvaluated: number;
}

/**
 * The "Inventory" card. The three figures have deliberately different scopes and are not
 * interchangeable: `inventoryValueAtCost` is the business-owned perpetual AVCO valuation, `null`
 * whenever `isInventoryValueComplete` is `false` (an unknown cost, never a real `$0.00`);
 * `unitsInStorage` is physical storage/home stock only - it excludes units already loaded into a
 * machine and is never a valuation input; `productCount` is the whole catalogue, active and
 * inactive, that the other two figures are taken over.
 */
export interface DashboardInventorySummary {
  inventoryValueAtCost: number | null;
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
 */
export interface DashboardSummary {
  asOfUtc: string;
  businessDate: string;
  salesThisWeek: DashboardSalesThisWeek;
  needsRefill: DashboardRefillSummary;
  needsOrdering: DashboardOrderingSummary;
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

export interface Machine {
  machineID: number;
  machineName?: string | null;
  machineNumber?: string | null;
  todayGrossRevenue: number;
  currentWeekGrossRevenue: number;
  lastWeekGrossRevenue: number;
  twoWeeksAgoGrossRevenue: number;
  todayDirectProfit: number | null;
  twoWeeksAgoDirectProfit: number | null;
  lastWeekDirectProfit: number | null;
  currentWeekDirectProfit: number | null;
  previousComparableWeekGrossRevenue: number;
  previousComparableWeekDirectProfit: number | null;
  monthToDateGrossRevenue: number;
  monthToDateDirectProfit: number | null;
  profitabilityStatus?: string | null;
}

export interface Site {
  siteId: number;
  siteName: string;
  machineCount: number;
  totalStockPercentage: number;
  lowProductCount: number;
  emptyProductCount: number;
  todayRevenue: number;
  currentWeekRevenue: number;
  previousComparableWeekRevenue: number;
}

export interface SiteProduct {
  productId: number;
  name: string;
  averageUnitCost: number | null;
  sitePrice: number;
  estimatedCardProfit: number | null;
  quantityInStock: number;
  maxStock: number;
}

export interface ProductUpdateDto {
  name: string;
  sku?: string | null;
  description?: string | null;
  unitPrice: number;
  lowStockThreshold: number;
  restockTo: number;
  unit?: string | null;
  categoryId?: number | null;
  supplierId?: number | null;
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
 * The global Stock History filters. `from`/`to` are inclusive `Australia/Sydney` calendar days
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
