namespace Inventory.Domain.Reporting.Transactions;

/// <summary>
/// One already-filtered transaction row's facts needed for the transaction sales report totals.
/// Carries no query or persistence behavior.
/// </summary>
public readonly record struct TransactionTotalsRowInputs(
    bool IsCompleted,
    decimal Sale,
    TransactionPaymentType PaymentType,
    // Whether this row is costed (see TransactionRowResult.IsCosted); irrelevant when not completed.
    bool IsCosted,
    decimal? CostOfGoods,
    decimal? GrossProfit,
    decimal? DirectProfit,
    bool FeeIsEstimated,
    decimal FeeExGst,
    decimal FeeGst,
    decimal FeeIncGst,
    decimal CommissionAmount);

public readonly record struct TransactionTotalsResult(
    int TransactionCount,
    int CompletedTransactionCount,
    decimal Sales,
    decimal CardSales,
    decimal CashSales,
    int CostedCompletedTransactionCount,
    int UncostedCompletedTransactionCount,
    bool IsCogsComplete,
    decimal? CostOfGoods,
    decimal PartialCostOfGoods,
    decimal? GrossProfit,
    decimal? GrossMarginPercent,
    decimal? DirectProfit,
    decimal? DirectMarginPercent,
    decimal? PartialGrossProfit,
    decimal? PartialDirectProfit,
    decimal EstimatedFeeExGst,
    decimal EstimatedFeeGst,
    decimal EstimatedFeeIncGst,
    decimal CommissionAmount);

/// <summary>
/// Aggregates the transaction sales report totals from the already-filtered set of rows. COGS
/// completeness, gross profit, and direct profit are gated on every completed row being costed,
/// mirroring the per-row gate in <see cref="TransactionRowPolicy"/> but applied across the scope.
/// </summary>
public static class TransactionTotalsPolicy
{
    public static TransactionTotalsResult Calculate(IReadOnlyList<TransactionTotalsRowInputs> rows)
    {
        var completedRows = rows.Where(x => x.IsCompleted).ToList();
        var costedRows = completedRows.Where(x => x.IsCosted).ToList();
        var isCogsComplete = completedRows.All(x => x.IsCosted);
        var partialCost = costedRows.Sum(x => x.CostOfGoods ?? 0m);
        decimal? cost = isCogsComplete ? partialCost : null;
        decimal? grossProfit = isCogsComplete ? completedRows.Sum(x => x.GrossProfit ?? 0m) : null;
        var directComplete = isCogsComplete && completedRows.All(x => x.DirectProfit.HasValue);
        decimal? directProfit = directComplete ? completedRows.Sum(x => x.DirectProfit!.Value) : null;
        var completedSales = completedRows.Sum(x => x.Sale);
        decimal? partialGross = costedRows.Count == 0 ? null : costedRows.Sum(x => x.GrossProfit!.Value);
        var directRows = completedRows.Where(x => x.DirectProfit.HasValue).ToList();
        decimal? partialDirect = directRows.Count == 0 ? null : directRows.Sum(x => x.DirectProfit!.Value);

        return new TransactionTotalsResult(
            rows.Count, completedRows.Count, rows.Sum(x => x.Sale),
            rows.Where(x => x.PaymentType == TransactionPaymentType.Card).Sum(x => x.Sale),
            rows.Where(x => x.PaymentType == TransactionPaymentType.Cash).Sum(x => x.Sale),
            costedRows.Count, completedRows.Count - costedRows.Count, isCogsComplete,
            cost, partialCost, grossProfit,
            grossProfit.HasValue ? ReportingCalculations.PercentageOf(grossProfit.Value, completedSales) : null,
            directProfit,
            directProfit.HasValue ? ReportingCalculations.PercentageOf(directProfit.Value, completedSales) : null,
            partialGross, partialDirect,
            rows.Where(x => x.FeeIsEstimated).Sum(x => x.FeeExGst),
            rows.Where(x => x.FeeIsEstimated).Sum(x => x.FeeGst),
            rows.Where(x => x.FeeIsEstimated).Sum(x => x.FeeIncGst),
            completedRows.Sum(x => x.CommissionAmount));
    }
}
