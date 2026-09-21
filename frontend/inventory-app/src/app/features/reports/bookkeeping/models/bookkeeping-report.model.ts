import { NayaxProcessingFeeResult, ReportQuality } from '../../shared/models/reporting.models';

export interface BookkeepingReport {
  from: string; to: string; financialYear: string; sales: number; costOfGoods?: number | null;
  partialCostOfGoods?: number; isCogsComplete?: boolean; uncostedTransactionCount?: number; uncostedSalesAmount?: number;
  grossProfit?: number | null; fees: number; netSettlement: number; gstOnSales: number; gstOnFees: number;
  dataQuality: ReportQuality;
  siteCommission?: number; netProfit?: number | null; netMarginPercent?: number | null;
  directProfit?: number | null; directMarginPercent?: number | null;
  nayaxFeesExGst?: number; nayaxFeesIncludingGst?: number;
  deliveryCosts?: number; packageCosts?: number; otherOperatingExpenses?: number;
  cardSales?: number; cashSales?: number; cardTransactionCount?: number; cashTransactionCount?: number;
  nayaxProcessingRate?: number;
  nayaxProcessingFees?: NayaxProcessingFeeResult;
  structuredOperatingExpenses?: number; operatingExpenseGst?: number; operatingExpensesByCategory?: Record<string, number>;
}
