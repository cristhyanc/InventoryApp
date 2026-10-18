import { ReportAvailability, ReportQuality } from '../../shared/models/reporting.models';

export interface ProductRow {
  productId: number | null; productName: string; categoryName?: string; sales: number; quantity: number;
  costOfGoods?: number | null; partialCostOfGoods?: number; isCogsComplete?: boolean; uncostedTransactionCount?: number;
  uncostedSalesAmount?: number; grossProfit?: number | null; marginPercent?: number | null; transactionCount: number;
  isUnmapped: boolean; historicalCostAvailable: boolean;
  cardRevenue?: number; cashRevenue?: number;
  /** Purchasing insight (issue #207): the product's actual Purchase-history Last/Lowest cost and
   * supplier, and the saving between them. Null when no Purchase history is recorded; never fed
   * into costOfGoods/grossProfit/marginPercent above. */
  lastCost?: number | null; lastCostSupplierName?: string | null;
  lowestCost?: number | null; lowestCostSupplierName?: string | null;
  savingPerUnit?: number | null;
}
export interface ProductReport {
  from: string; to: string; rows: ProductRow[]; dataQuality: ReportQuality;
  /**
   * How well recorded provider coverage accounts for this period's stored sales (issue #731). One
   * state, not the two-family split: nothing in this report comes from a settlement import, and the
   * Last/Lowest purchase costs are this business's own records. It governs the product list itself
   * too - an empty table is only "nothing sold" when coverage says the period was read end to end.
   */
  salesAvailability?: ReportAvailability;
}
