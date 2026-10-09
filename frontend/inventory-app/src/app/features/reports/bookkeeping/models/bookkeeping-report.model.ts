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
  // Transaction-status counts for the requested period/machine, scoped by the API before the
  // completed-sale filter excluded them (issue #476). Pending, refunded and cancelled/declined rows
  // are normal Nayax outcomes; only an unrecognised or absent status ID is a data-quality problem,
  // and the report's own data-quality notes are what report it.
  pendingTransactionCount?: number; refundedTransactionCount?: number;
  declinedOrCancelledTransactionCount?: number; unknownStatusTransactionCount?: number;
  missingStatusTransactionCount?: number;
  nayaxProcessingRate?: number;
  nayaxProcessingFees?: NayaxProcessingFeeResult;
  structuredOperatingExpenses?: number; operatingExpenseGst?: number; operatingExpensesByCategory?: Record<string, number>;
}
