export enum StockAdjustmentReason {
  Restock = 0,
  Sale = 1,
  Damaged = 2,
  Expired = 3,
  Correction = 4,
  Other = 5
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
  quantityInStock: number;
  maxStockInMachine: number;
  lowStockThreshold: number;
  mdbCode: number | null;
  unit?: string | null;
  createdAt: string;
  updatedAt: string;
  categoryId?: number | null;
  category?: Category | null;
  supplierId?: number | null;
  supplier?: Supplier | null;
  isLowStock: boolean;
  mapped: boolean;
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
}

export interface ProductCreateDto {
  name: string;
  sku?: string | null;
  description?: string | null;
  unitPrice: number;
  quantityInStock: number;
  lowStockThreshold: number;
  unit?: string | null;
  categoryId?: number | null;
  supplierId?: number | null;
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
}

export interface StockAdjustment {
  id: number;
  productId: number;
  quantityChange: number;
  quantityAfter: number;
  reason: StockAdjustmentReason;
  notes?: string | null;
  createdAt: string;
}

export interface StockAdjustmentDto {
  quantityChange: number;
  reason: StockAdjustmentReason;
  notes?: string | null;
}

export interface Receipt {
  id: number;
  title: string;
  notes?: string | null;
  totalAmount?: number | null;
  purchaseDate: string;
  supplierId?: number | null;
  supplier?: Supplier | null;
  fileName: string;
  storedFileName: string;
  contentType: string;
  fileSizeBytes: number;
  createdAt: string;
}
