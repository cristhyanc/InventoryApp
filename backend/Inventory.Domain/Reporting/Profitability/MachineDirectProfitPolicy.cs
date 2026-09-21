namespace Inventory.Domain.Reporting.Profitability;

/// <summary>
/// Inputs required to determine a single machine's direct profit and margin. Already-aggregated
/// facts for the machine's scope; carries no query or persistence behavior.
/// </summary>
public readonly record struct MachineDirectProfitInputs(
    decimal Sales,
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    decimal FeesIncludingGst,
    bool HasMissingFeeRates,
    decimal SiteCommission,
    bool CommissionComplete,
    decimal OperatingExpenses);

public readonly record struct MachineDirectProfitResult(
    bool IsComplete,
    decimal? DirectProfit,
    decimal? DirectMarginPercent);

/// <summary>
/// Machine profitability's completeness/direct-profit rule: direct profit applies only when COGS is
/// complete, no card transaction has a missing Nayax processing fee rate, and commission coverage is
/// complete for the machine. Mirrors <c>Inventory.Domain.Reporting.Bookkeeping.BookkeepingProfitPolicy</c>'s
/// machine-filtered direct-profit branch, reusing the shared <see cref="ReportingCalculations"/>.
/// </summary>
public static class MachineDirectProfitPolicy
{
    public static MachineDirectProfitResult Calculate(MachineDirectProfitInputs inputs)
    {
        var isComplete = inputs.IsCogsComplete && !inputs.HasMissingFeeRates && inputs.CommissionComplete;

        decimal? directProfit = isComplete
            ? inputs.Sales - inputs.PartialCostOfGoods - inputs.SiteCommission - inputs.OperatingExpenses - inputs.FeesIncludingGst
            : null;

        decimal? directMarginPercent = isComplete
            ? ReportingCalculations.MarginPercent(inputs.Sales,
                inputs.PartialCostOfGoods + inputs.SiteCommission + inputs.OperatingExpenses + inputs.FeesIncludingGst)
            : null;

        return new MachineDirectProfitResult(isComplete, directProfit, directMarginPercent);
    }
}
