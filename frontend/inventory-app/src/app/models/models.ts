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
  reorderLevel: number;
  reorderShortfall: number;
  onOrderQuantity: number;
  lowStockThreshold: number;
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
  machineId?: number | null;
  notes?: string | null;
  eatBefore?: string | null;
  createdAt: string;
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

export interface Receipt {
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
  items?: ReceiptItem[];
}

export interface ReceiptValidation {
  hasTotalMismatch: boolean;
  calculatedItemSubtotal?: number | null;
  calculatedTotal?: number | null;
  totalDifference?: number | null;
}

export interface ReceiptResponse {
  receipt: Receipt;
  validation?: ReceiptValidation | null;
}

export interface ReceiptItem {
  id?: number;
  receiptId?: number;
  productId: number;
  product?: Product | null;
  quantity: number;
  unitCost: number;
  lineTotal?: number;
}
