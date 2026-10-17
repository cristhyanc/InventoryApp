import { NayaxProcessingFeeResult, ReportProviderAvailability, ReportQuality } from '../../shared/models/reporting.models';

export interface MachineRow {
  machineId: number; machineName: string; sales: number; quantity: number; costOfGoods?: number | null;
  partialCostOfGoods?: number; isCogsComplete?: boolean; uncostedTransactionCount?: number; uncostedSalesAmount?: number;
  grossProfit?: number | null; marginPercent?: number | null; transactionCount: number;
  siteCommission?: number; directProfit?: number | null; directMarginPercent?: number | null; commissionPercent?: number;
  cardSales?: number; cashSales?: number;
  directOperatingExpenses?: number;
  nayaxProcessingFees?: NayaxProcessingFeeResult;
}
export interface MachineReport {
  from: string; to: string; rows: MachineRow[]; dataQuality: ReportQuality;
  /**
   * How well recorded provider coverage accounts for this period (issue #731). `sales` governs each
   * machine's sales, the card/cash split, quantity, cost of goods, gross profit, margin and site
   * commission, and the machine list itself - a machine with no sales simply does not appear;
   * `fees` governs the actual part of its Nayax processing fees; `combined` governs direct profit
   * and its margin. The per-machine direct operating expenses carry no state.
   */
  providerAvailability?: ReportProviderAvailability;
}
