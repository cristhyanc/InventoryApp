import { ReportAvailability, ReportProviderAvailability, ReportQuality } from '../../shared/models/reporting.models';

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
  /**
   * How well recorded provider coverage accounts for this period's provider-sourced GST figures
   * (issue #730): `sales` governs taxable sales and GST on sales, `fees` the taxable fees and the
   * GST on them. Purchase and operating-expense GST are this business's own records and carry no
   * state. Optional because an older API response does not carry it.
   */
  providerAvailability?: ReportProviderAvailability;
  /** The availability of net GST: only as complete as the weaker of the two provider inputs. */
  netGstAvailability?: ReportAvailability;
}
