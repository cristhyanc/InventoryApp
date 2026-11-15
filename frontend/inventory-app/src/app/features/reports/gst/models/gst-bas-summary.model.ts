import { ReportAvailability, ReportProviderAvailability, ReportQuality } from '../../shared/models/reporting.models';

/**
 * Why a period has no BAS summary, or that it has one (issue #1318). The client branches on this
 * code and never on whether `figures` holds a number: "no summary" and "a summary of zero" are
 * different statements, and only the server decides which one a period is.
 */
export type GstBasSummaryStatus = 'Available' | 'NotGstRegistered' | 'MachineFiltered';

/**
 * The BAS labels for the selected period, exactly as the server calculated them. Nothing here is
 * added up, divided or compared in the browser: the figures, the credit total, the net estimate,
 * its payable/credit/nil answer and whether the estimate is incomplete are all decided by the one
 * shared GST calculation the GST accounting aid is published from (AGENTS.md § Architecture rules:
 * one authoritative calculation feeds every presentation).
 */
export interface GstBasSummaryFigures {
  /** G1 - the period's whole GST-inclusive sales, GST-free sales included. Sales state. */
  totalSales: number;
  /** 1A - GST on the period's taxable sales. Sales state. */
  gstOnSales: number;
  /** 1B - the eligible credits, each source counted once. Settlement state, because fee GST is one. */
  gstCredits: number;
  /** Eligible stock purchase GST inside 1B, including eligible delivery and packaging. Exact. */
  purchaseGstCredits: number;
  /** Operating-expense GST inside 1B. This business's own records, so exact. */
  operatingExpenseGstCredits: number;
  /** Nayax fee GST inside 1B. Settlement state. */
  nayaxFeeGstCredits: number;
  /** 1A less 1B, signed. Governed by `netGstAvailability`. */
  netGst: number;
  netGstOutcome: 'Payable' | 'Credit' | 'Nil';
  /** Whether the estimate rests on something missing or estimated. The notes say which. */
  isIncomplete: boolean;
  gstFreeSales: number;
  /** The part of G1 taxable only because those products carry no GST rule (issue #1255). */
  noGstRuleSales: number;
  purchaseUnresolvedComponentCount: number;
  purchaseUnresolvedAmount: number;
  estimatedFeeGst: number;
  feeGstIncludesEstimates: boolean;
  providerAvailability?: ReportProviderAvailability;
  netGstAvailability?: ReportAvailability;
}

export interface GstBasSummary {
  from: string;
  to: string;
  status: GstBasSummaryStatus;
  /** Present only while `status` is `Available`; never a set of zeros in any other state. */
  figures?: GstBasSummaryFigures | null;
  dataQuality: ReportQuality;
}
