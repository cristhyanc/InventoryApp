namespace Inventory.Application.Reporting.Shared;

public record NayaxProcessingFeeResult(
    decimal ActualFeeExGst,
    decimal ActualFeeGst,
    decimal ActualFeeIncGst,
    decimal EstimatedFeeExGst,
    decimal EstimatedFeeGst,
    decimal EstimatedFeeIncGst,
    int EstimatedCardTransactionCount,
    DateTime? ActualFeeCoverageEndDate,
    DateTime? EstimatedFeeFromDate,
    int MissingRateTransactionCount = 0)
{
    public decimal TotalFeeExGst => ActualFeeExGst + EstimatedFeeExGst;
    public decimal TotalFeeGst => ActualFeeGst + EstimatedFeeGst;
    public decimal TotalFeeIncGst => ActualFeeIncGst + EstimatedFeeIncGst;
    public bool HasEstimatedFees => EstimatedCardTransactionCount > 0;
    public bool IsFullyActual => EstimatedCardTransactionCount == 0;
    public bool HasMissingRates => MissingRateTransactionCount > 0;
}
