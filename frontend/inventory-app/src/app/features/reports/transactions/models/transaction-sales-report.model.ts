import { ReportQuality } from '../../shared/models/reporting.models';

export interface TransactionSalesFilter {
  from?: string; to?: string; machineId?: number | null; siteId?: number | null; productId?: number | null;
  paymentType?: string | null; status?: string | null; cogsStatus?: string | null; search?: string | null;
  page?: number; pageSize?: number; sortBy?: string; sortDescending?: boolean;
}
export interface TransactionSalesFilterOption { id: number | null; name: string; }
export interface TransactionSalesRow {
  transactionDate: string; transactionId: number; machineId: number; machineName: string;
  siteId?: number | null; siteName?: string | null; productId?: number | null; productName: string;
  paymentType: string; rawPaymentMethod?: string | null; sale: number; unitCostAtSale?: number | null;
  nayaxProductCostPrice?: number | null; costOfGoods?: number | null; costingStatus: string; costSource: string;
  grossProfit?: number | null; grossMarginPercent?: number | null;
  directProfit?: number | null; directMarginPercent?: number | null; feeExGst?: number | null;
  feeGst?: number | null; feeIncGst?: number | null; feeSource: string; commissionRate?: number | null;
  commissionBasis?: string | null; commissionAmount?: number | null; transactionStatusId?: number | null;
  transactionStatus: string; isCompleted: boolean;
}
export interface TransactionSalesTotals {
  transactionCount: number; completedTransactionCount: number; sales: number; cardSales: number; cashSales: number;
  costedCompletedTransactionCount: number; uncostedCompletedTransactionCount: number; isCogsComplete: boolean;
  costOfGoods?: number | null; partialCostOfGoods: number; grossProfit?: number | null; grossMarginPercent?: number | null;
  directProfit?: number | null; directMarginPercent?: number | null; partialGrossProfit?: number | null;
  partialDirectProfit?: number | null; estimatedFeeExGst: number; estimatedFeeGst: number;
  estimatedFeeIncGst: number; commissionAmount: number;
}
export interface TransactionSalesReport {
  from: string; to: string; rows: TransactionSalesRow[]; totals: TransactionSalesTotals; dataQuality: ReportQuality;
  page: number; pageSize: number; totalCount: number;
  filterOptions: { sites: TransactionSalesFilterOption[]; products: TransactionSalesFilterOption[]; };
}
