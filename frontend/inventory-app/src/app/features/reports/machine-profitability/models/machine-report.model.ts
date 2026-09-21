import { NayaxProcessingFeeResult, ReportQuality } from '../../shared/models/reporting.models';

export interface MachineRow {
  machineId: number; machineName: string; sales: number; quantity: number; costOfGoods?: number | null;
  partialCostOfGoods?: number; isCogsComplete?: boolean; uncostedTransactionCount?: number; uncostedSalesAmount?: number;
  grossProfit?: number | null; marginPercent?: number | null; transactionCount: number;
  siteCommission?: number; directProfit?: number | null; directMarginPercent?: number | null; commissionPercent?: number;
  cardSales?: number; cashSales?: number;
  directOperatingExpenses?: number;
  nayaxProcessingFees?: NayaxProcessingFeeResult;
}
export interface MachineReport { from: string; to: string; rows: MachineRow[]; dataQuality: ReportQuality; }
