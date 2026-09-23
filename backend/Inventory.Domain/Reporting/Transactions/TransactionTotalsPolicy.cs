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
        var accumulator = new TransactionTotalsAccumulator();
        foreach (var row in rows) accumulator.Add(row);
        return accumulator.ToResult();
    }
}

/// <summary>
/// Incrementally accumulates the same totals as <see cref="TransactionTotalsPolicy.Calculate"/>, one
/// row at a time, so a caller can fold a large or streamed row sequence into constant-size state
/// instead of retaining the complete row list. <see cref="TransactionTotalsPolicy.Calculate"/>
/// delegates to this accumulator so there is a single authoritative formula path; batch and
/// incremental use always agree because they run the same code.
/// </summary>
public sealed class TransactionTotalsAccumulator
{
    private int _transactionCount;
    private int _completedTransactionCount;
    private decimal _sales;
    private decimal _cardSales;
    private decimal _cashSales;
    private decimal _completedSales;
    private int _costedCompletedTransactionCount;
    private bool _allCompletedCosted = true;
    private decimal _costOfGoods;
    private decimal _costedGrossProfitSum;
    private bool _allCompletedHaveDirectProfit = true;
    private int _directProfitRowCount;
    private decimal _directProfitSum;
    private decimal _estimatedFeeExGst;
    private decimal _estimatedFeeGst;
    private decimal _estimatedFeeIncGst;
    private decimal _completedCommissionAmount;

    public void Add(TransactionTotalsRowInputs row)
    {
        _transactionCount++;
        _sales += row.Sale;
        if (row.PaymentType == TransactionPaymentType.Card) _cardSales += row.Sale;
        else if (row.PaymentType == TransactionPaymentType.Cash) _cashSales += row.Sale;

        if (row.FeeIsEstimated)
        {
            _estimatedFeeExGst += row.FeeExGst;
            _estimatedFeeGst += row.FeeGst;
            _estimatedFeeIncGst += row.FeeIncGst;
        }

        if (!row.IsCompleted) return;

        _completedTransactionCount++;
        _completedSales += row.Sale;
        _completedCommissionAmount += row.CommissionAmount;

        if (row.IsCosted)
        {
            _costedCompletedTransactionCount++;
            _costOfGoods += row.CostOfGoods ?? 0m;
            _costedGrossProfitSum += row.GrossProfit ?? 0m;
        }
        else
        {
            _allCompletedCosted = false;
        }

        if (row.DirectProfit.HasValue)
        {
            _directProfitRowCount++;
            _directProfitSum += row.DirectProfit.Value;
        }
        else
        {
            _allCompletedHaveDirectProfit = false;
        }
    }

    public TransactionTotalsResult ToResult()
    {
        var isCogsComplete = _allCompletedCosted;
        decimal? cost = isCogsComplete ? _costOfGoods : null;
        decimal? grossProfit = isCogsComplete ? _costedGrossProfitSum : null;
        var directComplete = isCogsComplete && _allCompletedHaveDirectProfit;
        decimal? directProfit = directComplete ? _directProfitSum : null;
        decimal? partialGross = _costedCompletedTransactionCount == 0 ? null : _costedGrossProfitSum;
        decimal? partialDirect = _directProfitRowCount == 0 ? null : _directProfitSum;

        return new TransactionTotalsResult(
            _transactionCount, _completedTransactionCount, _sales, _cardSales, _cashSales,
            _costedCompletedTransactionCount, _completedTransactionCount - _costedCompletedTransactionCount, isCogsComplete,
            cost, _costOfGoods, grossProfit,
            grossProfit.HasValue ? ReportingCalculations.PercentageOf(grossProfit.Value, _completedSales) : null,
            directProfit,
            directProfit.HasValue ? ReportingCalculations.PercentageOf(directProfit.Value, _completedSales) : null,
            partialGross, partialDirect,
            _estimatedFeeExGst, _estimatedFeeGst, _estimatedFeeIncGst,
            _completedCommissionAmount);
    }
}
