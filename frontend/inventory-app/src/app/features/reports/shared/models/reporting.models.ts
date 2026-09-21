export interface ReportingFilter {
  from?: string;
  to?: string;
  machineId?: number | null;
  financialYear?: string;
}

export interface ReportQuality {
  missingStatus: boolean;
  historicalCostUnavailable: boolean;
  gstClassificationMissing: boolean;
  commissionNotPersisted: boolean;
  containsUnmappedProducts: boolean;
  notes?: string[];
}

export interface NayaxProcessingFeeResult {
  actualFeeExGst: number; actualFeeGst: number; actualFeeIncGst: number;
  estimatedFeeExGst: number; estimatedFeeGst: number; estimatedFeeIncGst: number;
  totalFeeExGst: number; totalFeeGst: number; totalFeeIncGst: number;
  estimatedCardTransactionCount: number; hasEstimatedFees: boolean; isFullyActual: boolean;
  missingRateTransactionCount?: number; hasMissingRates?: boolean;
  actualFeeCoverageEndDate?: string; estimatedFeeFromDate?: string;
}
