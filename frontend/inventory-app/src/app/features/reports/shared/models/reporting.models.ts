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

/**
 * How well recorded provider coverage accounts for a report period's provider-sourced figures
 * (issue #730, parent #329 decision P9). The four states are distinct answers, never labels on each
 * other, and only `Complete` means a zero in the figures it governs is a real zero:
 *
 * - `Complete`   - coverage spans the whole period.
 * - `Stale`      - covered without a gap from the period start up to `asOfUtc`, and no further.
 * - `Unverified` - data exists that coverage does not account for (it predates coverage recording,
 *                  or a failed or partial run left a gap).
 * - `Missing`    - no coverage and no data: the figures are absent, not zero.
 */
export type ReportAvailabilityState = 'Complete' | 'Stale' | 'Unverified' | 'Missing';

export interface ReportAvailability {
  state: ReportAvailabilityState;
  /** For `Stale`, the UTC instant the figures are complete up to. Null otherwise. */
  asOfUtc?: string | null;
  isComplete?: boolean;
}

/**
 * The availability of a report's provider-sourced figures, split by the data each one comes from.
 * `sales` governs sales, the card/cash split, COGS, the profit figures, estimated GST on sales,
 * site commission and net settlement; `fees` governs the Nayax processing fees and their GST. A
 * figure the business records itself - delivery, packaging, operating expenses, purchase GST -
 * carries no provider state and stays visible and exact.
 */
export interface ReportProviderAvailability {
  sales: ReportAvailability;
  fees: ReportAvailability;
}

export interface NayaxProcessingFeeResult {
  actualFeeExGst: number; actualFeeGst: number; actualFeeIncGst: number;
  estimatedFeeExGst: number; estimatedFeeGst: number; estimatedFeeIncGst: number;
  totalFeeExGst: number; totalFeeGst: number; totalFeeIncGst: number;
  estimatedCardTransactionCount: number; hasEstimatedFees: boolean; isFullyActual: boolean;
  missingRateTransactionCount?: number; hasMissingRates?: boolean;
  actualFeeCoverageEndDate?: string; estimatedFeeFromDate?: string;
}
