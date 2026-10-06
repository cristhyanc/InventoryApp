import { ReportQuality } from '../../shared/models/reporting.models';

export interface GstReport {
  from: string; to: string; taxableSales: number; gstOnSales: number; taxableFees: number;
  gstOnFees: number; netGst: number; dataQuality: ReportQuality;
  operatingExpenseGst?: number;
  /** Total purchase input GST for the period: product lines plus delivery/package charges. */
  inventoryPurchaseGst?: number;
  purchaseLineGst?: number;
  purchaseChargeGst?: number;
  /** Purchase components in the period with no GST classification; they contribute no GST. */
  purchaseUnresolvedComponentCount?: number;
  purchaseUnresolvedAmount?: number;
  /** Set by the API when the purchase input GST shown, and therefore net GST, is incomplete. */
  purchaseGstIncomplete?: boolean;
}
