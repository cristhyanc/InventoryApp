import { ReportQuality } from '../../shared/models/reporting.models';

export interface ReconciliationPeriod {
  from: string; to: string; totalVendingSales: number; cardSales: number; cashSales: number;
  cardTransactionSales: number; nayaxReportedGrossCardSales: number;
  cardTransactionCount: number; nayaxReportedCardTransactionCount: number; countDifference: number;
  totalTransactionCount: number; cashTransactionCount: number;
  grossDifference: number; grossStatus: string; processingFeesExGst: number; feeGst: number;
  otherFees: number; adjustments: number;   adjustmentsSupported: boolean;
  pendingTransactionCount?: number; refundedTransactionCount?: number;
  declinedOrCancelledTransactionCount?: number; unknownStatusTransactionCount?: number; expectedNetReimbursement: number;
  actualNetReimbursement: number; settlementDifference: number; settlementStatus: string;
  status: string; payoutDate?: string; dataQuality: ReportQuality;
}
export interface ReconciliationTotals {
  totalVendingSales: number; cardSales: number; cashSales: number;
  cardTransactionSales: number; nayaxReportedGrossCardSales: number;
  cardTransactionCount: number; nayaxReportedCardTransactionCount: number; countDifference: number;
  totalTransactionCount: number; cashTransactionCount: number;
  grossDifference: number; processingFeesExGst: number; feeGst: number; otherFees: number;
  adjustments: number; adjustmentsSupported: boolean; expectedNetReimbursement: number; actualNetReimbursement: number;
  settlementDifference: number; grossStatus: string; settlementStatus: string; status: string;
}
export interface ReconciliationReport {
  from: string; to: string; nayaxSales: number; importedReimbursement: number; difference: number;
  tolerance: number; isMatch: boolean; dataQuality: ReportQuality; cardTransactionCount?: number;
  nayaxTransactionCount?: number; countDifference?: number; processingFees?: number;
  netReimbursement?: number; payoutDate?: string; totalVendingSales: number; cardSales: number;
  cashSales: number; totalTransactionCount: number; cashTransactionCount: number;
  cardTransactionSales: number; nayaxReportedGrossCardSales: number;
  nayaxReportedCardTransactionCount: number; grossDifference: number; grossStatus: string;
  processingFeesExGst: number; feeGst: number; otherFees: number; adjustments: number;
  expectedNetReimbursement: number; actualNetReimbursement: number; settlementDifference: number;
  adjustmentsSupported: boolean;
  pendingTransactionCount?: number; refundedTransactionCount?: number;
  declinedOrCancelledTransactionCount?: number; unknownStatusTransactionCount?: number;
  settlementStatus: string; status: string; periodRows: ReconciliationPeriod[]; totals?: ReconciliationTotals;
}
