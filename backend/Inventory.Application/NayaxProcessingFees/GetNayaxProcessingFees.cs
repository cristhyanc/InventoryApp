using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.NayaxProcessingFees;

public sealed class GetNayaxProcessingFees : IGetNayaxProcessingFees
{
    private readonly INayaxProcessingFeeFactsProvider _factsProvider;
    private readonly INayaxFeeRateStore _feeRateStore;

    public GetNayaxProcessingFees(
        INayaxProcessingFeeFactsProvider factsProvider,
        INayaxFeeRateStore feeRateStore)
    {
        _factsProvider = factsProvider;
        _feeRateStore = feeRateStore;
    }

    public async Task<NayaxProcessingFeeResult> Handle(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        CancellationToken cancellationToken)
    {
        var facts = await _factsProvider.GetFactsAsync(fromDate, toDate, machineId, cancellationToken);
        var feeRates = await _feeRateStore.ListOrderedByEffectiveDateDescendingAsync(cancellationToken);
        var result = NayaxProcessingFeePolicy.Calculate(
            fromDate,
            toDate,
            machineId,
            facts.Reimbursements,
            facts.CompletedSales,
            feeRates.Select(rate => new EffectiveNayaxFeeRate(rate.EffectiveFrom, rate.FeeExGst)).ToList());

        return new NayaxProcessingFeeResult(
            result.ActualFeeExGst,
            result.ActualFeeGst,
            result.ActualFeeIncGst,
            result.EstimatedFeeExGst,
            result.EstimatedFeeGst,
            result.EstimatedFeeIncGst,
            result.EstimatedCardTransactionCount,
            result.ActualFeeCoverageEndDate,
            result.EstimatedFeeFromDate,
            result.MissingRateTransactionCount);
    }
}
