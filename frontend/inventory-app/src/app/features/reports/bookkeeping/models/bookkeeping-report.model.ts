import { NayaxProcessingFeeResult, ReportProviderAvailability, ReportQuality } from '../../shared/models/reporting.models';

/**
 * One imported Nayax payout as the sales period it settles and the net total recorded for it
 * (issue #1253). The dates are the settlement's own declared period - never its payout, bank or
 * import date - and `total` is `null` when the import recorded none, which is not a payout of zero.
 */
export interface ImportedReimbursementPeriod {
  periodStart: string;
  periodEnd: string;
  total?: number | null;
}

export interface BookkeepingReport {
  from: string; to: string; financialYear: string; sales: number; costOfGoods?: number | null;
  partialCostOfGoods?: number; isCogsComplete?: boolean; uncostedTransactionCount?: number; uncostedSalesAmount?: number;
  grossProfit?: number | null; fees: number; netSettlement: number; gstOnSales: number; gstOnFees: number;
  dataQuality: ReportQuality;
  /**
   * The sales `gstOnSales` was taken from, by each sold product's own GST rule (issue #1255), all
   * GST-inclusive like `sales` itself. `taxableSales` plus `gstFreeSales` is `sales`; only a product
   * whose rule is explicitly GST-free is in `gstFreeSales`. `noGstRuleSales` is the part of
   * `taxableSales` that is taxable only because those products have no rule yet - the default every
   * product starts at - plus any sale whose product could not be resolved. All three are
   * provider-sourced and governed by `providerAvailability.sales`. Optional because an older API
   * response does not carry them.
   */
  taxableSales?: number; gstFreeSales?: number; noGstRuleSales?: number;
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
   * including GST; `actualReimbursement` is what the settlement import recorded for the period -
   * counting only the payouts whose own settlement period lies *wholly inside* the requested range
   * (issue #1253). It is `null` ("Not available") when the range has no such payout, and never a
   * zero for that reason: a settlement that really paid nothing is a measured zero while
   * `providerAvailability.fees` is `Complete`. `reimbursementDifference` is actual less expected,
   * signed, and is `null` whenever there is no actual to compare. None of them is `netSettlement`,
   * which keeps its own established meaning. Optional because an older API response does not carry
   * them.
   */
  expectedReimbursement?: number; actualReimbursement?: number | null; reimbursementDifference?: number | null;
  isReconciled?: boolean; reconciliationStatus?: string; reconciliationTolerance?: number;
  /**
   * The payouts `actualReimbursement` is the total of, with the period each settles, and the payouts
   * the requested range only *partly* overlaps (issue #1253). The partly covered ones are shown on
   * their own as "Payouts partly outside this range" and are part of no total in this response.
   * `isNetSettlementEstimated` says whether `netSettlement` is this report's own estimate - card
   * sales less fees - because no payout covering these dates in full recorded one.
   */
  reimbursementPeriods?: ImportedReimbursementPeriod[];
  partlyCoveredReimbursements?: ImportedReimbursementPeriod[];
  isNetSettlementEstimated?: boolean;
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
