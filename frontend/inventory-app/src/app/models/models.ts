export enum StockAdjustmentReason {
  Restock = 0,
  Sale = 1,
  Damaged = 2,
  Expired = 3,
  Correction = 4,
  MachineRefill = 5
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

// The Purchase business record served under the "/api/purchases" JSON contract
// (see backend Purchase.cs / PurchaseResponseDto).
export interface Purchase {
  id: number;
  title: string;
  notes?: string | null;
  totalAmount?: number | null;
  deliveryCost?: number | null;
  packageCost?: number | null;
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

export interface PurchaseResponse {
  purchase: Purchase;
  validation?: PurchaseValidation | null;
}

export interface PurchaseItem {
  id?: number;
  receiptId?: number;
  productId: number;
  product?: Product | null;
  quantity: number;
  unitCost: number;
  lineTotal?: number;
}

/**
 * One machine's current/target/pick figures for a product in the Pick List matrix (issue #221/#222),
 * from the read-only `GET /api/pick-list` projection.
 */
export interface PickListMachineQuantity {
  machineId: number;
  currentQuantity: number;
  targetQuantity: number;
  quantityToPick: number;
}

/** One product row of the Pick List matrix. */
export interface PickListProduct {
  productId: number;
  productName: string;
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
