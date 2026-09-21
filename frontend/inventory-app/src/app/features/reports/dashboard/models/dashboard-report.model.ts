import { NayaxProcessingFeeResult, ReportQuality } from '../../shared/models/reporting.models';

export interface DashboardReport {
  from: string; to: string; sales: number; grossProfit?: number | null; transactions: number;
  quantity: number; machineCount: number; productCount: number; unmappedProductCount: number;
  dataQuality: ReportQuality;
  nayaxFeesExGst?: number; netReimbursement?: number; siteCommission?: number;
  netProfit?: number | null; netMarginPercent?: number | null;
  directProfit?: number | null; directMarginPercent?: number | null;
  deliveryCosts?: number; packageCosts?: number; otherOperatingExpenses?: number;
  structuredOperatingExpenses?: number; operatingExpenseGst?: number; operatingExpensesByCategory?: Record<string, number>;
  cardSales?: number; cashSales?: number; cardTransactionCount?: number; cashTransactionCount?: number;
  totalSales?: number; costOfGoodsSold?: number | null; partialCostOfGoods?: number; isCogsComplete?: boolean;
  uncostedTransactionCount?: number; uncostedSalesAmount?: number; averageSale?: number; grossMarginPercent?: number | null;
  nayaxFeesIncludingGst?: number;
  expectedReimbursement?: number; actualReimbursement?: number; reimbursementDifference?: number;
  isReconciled?: boolean; reconciliationStatus?: string; reconciliationTolerance?: number;
  adjustmentsSupported?: boolean;
  nayaxProcessingFees?: NayaxProcessingFeeResult;
}
