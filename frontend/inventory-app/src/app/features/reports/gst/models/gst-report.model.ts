import { ReportQuality } from '../../shared/models/reporting.models';

export interface GstReport {
  from: string; to: string; taxableSales: number; gstOnSales: number; taxableFees: number;
  gstOnFees: number; netGst: number; dataQuality: ReportQuality;
  operatingExpenseGst?: number;
}
