namespace Inventory.Domain.Reporting.Daily;

/// <summary>
/// Already-aggregated facts for one calendar day, needed to determine that day's profit, margin,
/// average sale, and reconciliation status against its imported card reimbursement. Carries no
/// query or persistence behavior.
/// </summary>
public readonly record struct DailyRowInputs(
    decimal GrossSales,
    int TransactionCount,
    decimal PartialCostOfGoods,
    bool IsCogsComplete,
    decimal CardSales,
    decimal ImportedReimbursement,
    bool HasImportedReimbursement,
    bool HasPeriodOnlyImportedData,
    bool HasDataQualityWarning,
    decimal Tolerance);

public readonly record struct DailyRowResult(
    decimal? CostOfGoods,
    decimal? GrossProfit,
    decimal AverageSale,
    decimal? GrossMarginPercent,
    bool IsReconciled,
    string ReconciliationStatus);

/// <summary>
/// Computes one daily report row's profit/margin and reconciliation status. Mirrors
/// <see cref="Bookkeeping.BookkeepingProfitPolicy"/>'s role for the bookkeeping report.
/// </summary>
public static class DailyRowPolicy
{
    public static DailyRowResult Calculate(DailyRowInputs inputs)
    {
        decimal? costOfGoods = inputs.IsCogsComplete ? inputs.PartialCostOfGoods : null;
        decimal? grossProfit = inputs.IsCogsComplete
            ? ReportingCalculations.GrossProfit(inputs.GrossSales, inputs.PartialCostOfGoods)
            : null;
        decimal? marginPercent = inputs.IsCogsComplete
            ? ReportingCalculations.MarginPercent(inputs.GrossSales, inputs.PartialCostOfGoods)
            : null;
        var averageSale = ReportingCalculations.Average(inputs.GrossSales, inputs.TransactionCount);

        var difference = inputs.CardSales - inputs.ImportedReimbursement;
        var isReconciled = inputs.HasImportedReimbursement &&
            ReconciliationStatusPolicy.IsReconciled(difference, inputs.Tolerance);
        var status = ReconciliationStatusPolicy.StatusFor(
            !inputs.HasImportedReimbursement && !inputs.HasPeriodOnlyImportedData,
            difference, inputs.Tolerance,
            inputs.HasDataQualityWarning || inputs.HasPeriodOnlyImportedData);

        return new DailyRowResult(costOfGoods, grossProfit, averageSale, marginPercent, isReconciled, status);
    }
}
