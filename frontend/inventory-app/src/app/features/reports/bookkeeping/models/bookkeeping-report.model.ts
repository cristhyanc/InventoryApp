import { NayaxProcessingFeeResult, ReportProviderAvailability, ReportQuality } from '../../shared/models/reporting.models';

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
  /**
   * Completed (status 12) sales in the requested period and machine scope, and the average sale
   * over them (issue #804). Both are provider-sourced and governed by `providerAvailability.sales`,
   * so a period nothing covered shows that state instead of their zero. Optional because an older
   * API response does not carry them.
   */
  transactionCount?: number; averageSale?: number;
  /**
   * The expected-versus-actual Nayax reimbursement comparison (issue #805).
   * `expectedReimbursement` is calculated from this period's card sales less the Nayax fees
   * including GST; `actualReimbursement` is what the settlement import recorded for the period, and
   * it is a measured zero only while `providerAvailability.fees` is `Complete` and
   * `reconciliationStatus` is not `Pending` - `Pending` means no imported row covers the period at
   * all. `reimbursementDifference` is actual less expected, signed. None of them is `netSettlement`,
   * which keeps its own established meaning. Optional because an older API response does not carry
   * them.
   */
  expectedReimbursement?: number; actualReimbursement?: number; reimbursementDifference?: number;
  isReconciled?: boolean; reconciliationStatus?: string; reconciliationTolerance?: number;
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
  /**
   * How well recorded provider coverage accounts for this period (issue #730). `sales` governs
   * sales, the card/cash split, COGS, gross profit and site commission; `fees` governs the Nayax
   * processing fees, their GST and the imported actual reimbursement; `combined` governs the figures
   * that depend on both - net and direct profit and their margins, the net card settlement, the
   * total of operating costs, and the expected reimbursement and its difference from the actual.
   * Delivery, packaging and operating expenses are this business's own records and carry no state.
   * Optional because an older API response does not carry it.
   */
  providerAvailability?: ReportProviderAvailability;
}
