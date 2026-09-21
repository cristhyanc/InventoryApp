namespace Inventory.Domain.Reporting.Profitability;

/// <summary>
/// Inputs required to determine a single machine/product row's cost, gross profit, and margin.
/// Already-aggregated facts for the row's scope; carries no query or persistence behavior.
/// </summary>
public readonly record struct ProfitabilityRowInputs(
    decimal Sales,
    decimal PartialCostOfGoods,
    bool IsCogsComplete);

public readonly record struct ProfitabilityRowResult(
    decimal? CostOfGoods,
    decimal? GrossProfit,
    decimal? MarginPercent);

/// <summary>
/// Shared per-group profit/margin/completeness policy: gross profit and margin are reported only
/// when COGS is complete for the row's scope, otherwise both are unknown (never zero). Used by both
/// machine and product profitability, since the gate and formula are identical for both reports.
/// </summary>
public static class ProfitabilityRowPolicy
{
    public static ProfitabilityRowResult Calculate(ProfitabilityRowInputs inputs)
    {
        if (!inputs.IsCogsComplete)
            return new ProfitabilityRowResult(null, null, null);

        var grossProfit = ReportingCalculations.GrossProfit(inputs.Sales, inputs.PartialCostOfGoods);
        var marginPercent = ReportingCalculations.MarginPercent(inputs.Sales, inputs.PartialCostOfGoods);
        return new ProfitabilityRowResult(inputs.PartialCostOfGoods, grossProfit, marginPercent);
    }
}
