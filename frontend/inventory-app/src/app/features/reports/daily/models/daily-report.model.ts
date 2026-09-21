import { NayaxProcessingFeeResult, ReportQuality } from '../../shared/models/reporting.models';

export interface DailyRow {
  date: string; sales: number; grossSales?: number; cardSales?: number; cashSales?: number;
  quantity: number; costOfGoods?: number | null; partialCostOfGoods?: number; grossProfit?: number | null; transactionCount: number;
  averageSale?: number; isCogsComplete?: boolean; uncostedTransactionCount?: number;
  uncostedSalesAmount?: number; grossMarginPercent?: number | null;
  nayaxFeesExGst?: number; nayaxFeesIncludingGst?: number;
  importedReimbursement?: number; netReimbursement?: number;
  isReconciled?: boolean; reconciliationStatus?: string;
  completedTransactionCount?: number; pendingTransactionCount?: number;
  declinedOrCancelledTransactionCount?: number; refundedTransactionCount?: number;
  unknownStatusTransactionCount?: number;
  nayaxFeeSource?: string;
}
export interface DailyTotals {
  grossSales: number; cardSales: number; cashSales: number; quantity: number;
  costOfGoods?: number | null; partialCostOfGoods?: number; grossProfit?: number | null; transactionCount: number; averageSale: number;
  isCogsComplete: boolean; uncostedTransactionCount: number; uncostedSalesAmount: number;
  grossMarginPercent?: number | null; nayaxFeesExGst: number; nayaxFeesIncludingGst: number;
  importedReimbursement: number; netReimbursement: number;
  nayaxProcessingFees?: NayaxProcessingFeeResult;
}
export interface DailyReport { from: string; to: string; rows: DailyRow[]; dataQuality: ReportQuality; totals?: DailyTotals; }
