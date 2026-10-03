using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.NayaxProcessingFees;

public sealed class GetNayaxProcessingFees : IGetNayaxProcessingFees
{
    private readonly INayaxProcessingFeeFactsProvider _factsProvider;
    private readonly INayaxFeeRateStore _feeRateStore;
    private readonly IBusinessCalendar _businessCalendar;

    public GetNayaxProcessingFees(
        INayaxProcessingFeeFactsProvider factsProvider,
        INayaxFeeRateStore feeRateStore,
        IBusinessCalendar businessCalendar)
    {
        _factsProvider = factsProvider;
        _feeRateStore = feeRateStore;
        _businessCalendar = businessCalendar;
    }

    public async Task<NayaxProcessingFeeResult> Handle(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        CancellationToken cancellationToken)
    {
        var facts = await _factsProvider.GetFactsAsync(fromDate, toDate, machineId, cancellationToken);
        var feeRates = await EffectiveRatesAsync(cancellationToken);
        var result = NayaxProcessingFeePolicy.Calculate(
            fromDate,
            toDate,
            machineId,
            facts.Reimbursements,
            facts.CompletedSales,
            feeRates);

        return Map(result);
    }

    public async Task<NayaxProcessingFeeResult> HandleBusinessPeriod(
        NayaxProcessingFeeBusinessPeriod period,
        long? machineId,
        CancellationToken cancellationToken)
    {
        var facts = await _factsProvider.GetBusinessPeriodFactsAsync(period, machineId, cancellationToken);

        // The sales were selected by instant, so each one is already inside the period; bucketing each
        // by its Australia/Sydney business date is what makes the day whose imported fee covers it and
        // the effective rate that estimates it the same day the period counted its revenue in. The
        // business date comes from the time port, not from the adapter that read the sale.
        var completedSales = facts.CompletedSales
            .Select(sale => sale with
            {
                FeeDate = _businessCalendar.ToBusinessDate(sale.MachineAuthorizationTime)
            })
            .ToList();

        var feeRates = await EffectiveRatesAsync(cancellationToken);
        var result = NayaxProcessingFeePolicy.Calculate(
            period.FirstBusinessDate,
            period.LastBusinessDate,
            machineId,
            facts.Reimbursements,
            completedSales,
            feeRates);

        return Map(result);
    }

    private async Task<List<EffectiveNayaxFeeRate>> EffectiveRatesAsync(CancellationToken cancellationToken)
    {
        var feeRates = await _feeRateStore.ListOrderedByEffectiveDateDescendingAsync(cancellationToken);
        return feeRates.Select(rate => new EffectiveNayaxFeeRate(rate.EffectiveFrom, rate.FeeExGst)).ToList();
    }

    private static NayaxProcessingFeeResult Map(NayaxProcessingFeeTotals result) =>
        new(
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
