using Inventory.Domain.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Transactions;

public class TransactionTotalsPolicyTests
{
    private static TransactionTotalsRowInputs Completed(decimal sale, TransactionPaymentType paymentType,
        bool isCosted, decimal? costOfGoods, decimal? grossProfit, decimal? directProfit,
        bool feeIsEstimated = false, decimal feeExGst = 0m, decimal feeGst = 0m, decimal feeIncGst = 0m,
        decimal commissionAmount = 0m) =>
        new(true, sale, paymentType, isCosted, costOfGoods, grossProfit, directProfit,
            feeIsEstimated, feeExGst, feeGst, feeIncGst, commissionAmount);

    private static TransactionTotalsRowInputs NotCompleted(decimal sale, TransactionPaymentType paymentType) =>
        new(false, sale, paymentType, false, null, null, null, false, 0m, 0m, 0m, 0m);

    [Fact]
    public void Sales_and_payment_splits_include_every_row_regardless_of_completion()
    {
        var rows = new[]
        {
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 6m),
            NotCompleted(9m, TransactionPaymentType.Card),
            NotCompleted(5m, TransactionPaymentType.Cash),
        };

        var totals = TransactionTotalsPolicy.Calculate(rows);

        Assert.Equal(3, totals.TransactionCount);
        Assert.Equal(1, totals.CompletedTransactionCount);
        Assert.Equal(24m, totals.Sales);
        Assert.Equal(19m, totals.CardSales);
        Assert.Equal(5m, totals.CashSales);
    }

    [Fact]
    public void Cogs_complete_when_every_completed_row_is_costed()
    {
        var rows = new[]
        {
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 6m),
            Completed(5m, TransactionPaymentType.Cash, true, 2m, 3m, 3m),
        };

        var totals = TransactionTotalsPolicy.Calculate(rows);

        Assert.True(totals.IsCogsComplete);
        Assert.Equal(6m, totals.CostOfGoods);
        Assert.Equal(9m, totals.GrossProfit);
        Assert.Equal(60m, totals.GrossMarginPercent);
    }

    [Fact]
    public void One_uncosted_completed_row_makes_cogs_and_profit_totals_unavailable_but_keeps_partial_totals()
    {
        var rows = new[]
        {
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 6m),
            Completed(5m, TransactionPaymentType.Cash, false, null, null, null),
        };

        var totals = TransactionTotalsPolicy.Calculate(rows);

        Assert.False(totals.IsCogsComplete);
        Assert.Null(totals.CostOfGoods);
        Assert.Null(totals.GrossProfit);
        Assert.Null(totals.DirectProfit);
        Assert.Equal(1, totals.CostedCompletedTransactionCount);
        Assert.Equal(1, totals.UncostedCompletedTransactionCount);
        Assert.Equal(4m, totals.PartialCostOfGoods);
        Assert.Equal(6m, totals.PartialGrossProfit);
        Assert.Equal(6m, totals.PartialDirectProfit);
    }

    [Fact]
    public void Direct_profit_total_requires_every_completed_row_to_have_a_direct_profit()
    {
        var rows = new[]
        {
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 6m),
            Completed(5m, TransactionPaymentType.Card, true, 2m, 3m, null), // fee/commission unavailable
        };

        var totals = TransactionTotalsPolicy.Calculate(rows);

        Assert.True(totals.IsCogsComplete);
        Assert.Equal(9m, totals.GrossProfit);
        Assert.Null(totals.DirectProfit);
        Assert.Equal(6m, totals.PartialDirectProfit);
    }

    [Fact]
    public void Estimated_fee_totals_sum_only_estimated_rows_regardless_of_completion()
    {
        var rows = new[]
        {
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 5.78m, feeIsEstimated: true,
                feeExGst: 0.2m, feeGst: 0.02m, feeIncGst: 0.22m),
            NotCompleted(9m, TransactionPaymentType.Card),
        };

        var totals = TransactionTotalsPolicy.Calculate(rows);

        Assert.Equal(0.2m, totals.EstimatedFeeExGst);
        Assert.Equal(0.02m, totals.EstimatedFeeGst);
        Assert.Equal(0.22m, totals.EstimatedFeeIncGst);
    }

    [Fact]
    public void Commission_total_sums_only_completed_rows()
    {
        var rows = new[]
        {
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 5m, commissionAmount: 1m),
            NotCompleted(9m, TransactionPaymentType.Card),
        };

        var totals = TransactionTotalsPolicy.Calculate(rows);

        Assert.Equal(1m, totals.CommissionAmount);
    }

    [Fact]
    public void Empty_scope_reports_zero_totals_and_complete_cogs()
    {
        var totals = TransactionTotalsPolicy.Calculate([]);

        Assert.Equal(0, totals.TransactionCount);
        Assert.True(totals.IsCogsComplete);
        Assert.Equal(0m, totals.CostOfGoods);
        Assert.Equal(0m, totals.GrossProfit);
    }
}
