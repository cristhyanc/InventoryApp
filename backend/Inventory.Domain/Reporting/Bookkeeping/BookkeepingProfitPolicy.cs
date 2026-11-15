namespace Inventory.Domain.Reporting.Bookkeeping;

/// <summary>
/// Inputs required to determine bookkeeping profit, margin and net settlement. All amounts are
/// already-aggregated facts for the reporting period/machine scope; this type carries no query or
/// persistence behavior.
/// </summary>
public readonly record struct BookkeepingProfitInputs(
    decimal Sales,
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    decimal CardSales,
    decimal FeesIncludingGst,
    bool HasMissingFeeRates,
    bool IsMachineFiltered,
    bool CommissionCompleteForScope,
    decimal SiteCommission,
    decimal ReceiptCosts,
    decimal OperatingExpensesTotal,
    bool ImportedHasNetSettlement,
    decimal ImportedNetSettlement);

public readonly record struct BookkeepingProfitResult(
    decimal? GrossProfit,
    bool IsProfitComplete,
    decimal? DirectProfit,
    decimal? DirectMarginPercent,
    decimal? NetProfit,
    decimal? NetMarginPercent,
    decimal NetSettlement);

/// <summary>
/// Direct profit applies when a machine filter is active; net (whole-business) profit applies
/// otherwise, because shared overhead (receipt delivery/package costs) is not allocated per machine.
/// Both are gated on complete COGS, no missing Nayax fee rates, and complete commission coverage
/// for the requested scope.
///
/// <para>GST on sales is deliberately not here: since issue #1255 it follows each product's own GST
/// rule, so it is <c>Inventory.Domain.Reporting.Gst.SalesGstPolicy</c>'s one calculation over the
/// period's sales split by rule, not a figure this profit policy can derive from a sales total.</para>
/// </summary>
public static class BookkeepingProfitPolicy
{
    public static BookkeepingProfitResult Calculate(BookkeepingProfitInputs inputs)
    {
        decimal? grossProfit = inputs.IsCogsComplete
            ? ReportingCalculations.GrossProfit(inputs.Sales, inputs.PartialCostOfGoods)
            : null;

        var isProfitComplete = grossProfit.HasValue && !inputs.HasMissingFeeRates && inputs.CommissionCompleteForScope;

        decimal? directProfit = inputs.IsMachineFiltered && isProfitComplete
            ? grossProfit!.Value - inputs.FeesIncludingGst - inputs.SiteCommission - inputs.OperatingExpensesTotal
            : null;

        decimal? netProfit = !inputs.IsMachineFiltered && isProfitComplete
            ? grossProfit!.Value - inputs.FeesIncludingGst - inputs.SiteCommission - inputs.ReceiptCosts - inputs.OperatingExpensesTotal
            : null;

        var netSettlement = inputs.ImportedHasNetSettlement
            ? inputs.ImportedNetSettlement
            : inputs.CardSales - inputs.FeesIncludingGst;

        decimal? directMarginPercent = directProfit.HasValue
            ? ReportingCalculations.MarginPercent(inputs.Sales,
                inputs.PartialCostOfGoods + inputs.FeesIncludingGst + inputs.SiteCommission + inputs.OperatingExpensesTotal)
            : null;

        decimal? netMarginPercent = netProfit.HasValue
            ? ReportingCalculations.MarginPercent(inputs.Sales,
                inputs.PartialCostOfGoods + inputs.FeesIncludingGst + inputs.SiteCommission + inputs.ReceiptCosts + inputs.OperatingExpensesTotal)
            : null;

        return new BookkeepingProfitResult(grossProfit, isProfitComplete, directProfit, directMarginPercent,
            netProfit, netMarginPercent, netSettlement);
    }
}
