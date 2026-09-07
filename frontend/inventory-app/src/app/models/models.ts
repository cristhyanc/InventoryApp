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
  machinePrice: number | null;
  commissionValue: number | null;
  suggestedNetValue: number | null;
  suggestedPriceValue: number | null;
  quantityInStock: number;
  maxStockInMachine: number;
  machineReplenishmentNeed: number;
  reorderLevel: number;
  reorderShortfall: number;
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
  todayNetRevenue: number;
  twoWeeksAgoNetRevenue: number;
  lastWeekNetRevenue: number;
  currentWeekNetRevenue: number;
  previousComparableWeekGrossRevenue: number;
  previousComparableWeekNetRevenue: number;
  monthToDateGrossRevenue: number;
  monthToDateNetRevenue: number;
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
  unitPrice: number;
  sitePrice: number;
  profit: number;
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

export interface StockAdjustment {
  id: number;
  productId: number;
  quantityChange: number;
  quantityAfter: number;
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

export interface ReceiptItem {
  id?: number;
  receiptId?: number;
  productId: number;
  product?: Product | null;
  quantity: number;
  unitCost: number;
  lineTotal?: number;
}
