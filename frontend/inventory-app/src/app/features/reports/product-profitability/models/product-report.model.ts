import { ReportQuality } from '../../shared/models/reporting.models';

export interface ProductRow {
  productId: number | null; productName: string; categoryName?: string; sales: number; quantity: number;
  costOfGoods?: number | null; partialCostOfGoods?: number; isCogsComplete?: boolean; uncostedTransactionCount?: number;
  uncostedSalesAmount?: number; grossProfit?: number | null; marginPercent?: number | null; transactionCount: number;
  isUnmapped: boolean; historicalCostAvailable: boolean;
  cardRevenue?: number; cashRevenue?: number;
}
export interface ProductReport { from: string; to: string; rows: ProductRow[]; dataQuality: ReportQuality; }
