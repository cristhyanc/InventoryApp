using Inventory.Domain.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Transactions;

/// <summary>
/// Proves <see cref="TransactionTotalsAccumulator"/>, fed one row at a time (as the streaming
/// transaction sales report use case does), always reaches the same result as
/// <see cref="TransactionTotalsPolicy.Calculate"/> given the complete row list at once. Both already
/// share one code path (<c>Calculate</c> delegates to the accumulator), but these tests pin that
/// invariant explicitly across every scenario the batch tests cover, so a future change to either
/// path is caught if it breaks parity.
/// </summary>
public class TransactionTotalsAccumulatorTests
{
    private static TransactionTotalsRowInputs Completed(decimal sale, TransactionPaymentType paymentType,
        bool isCosted, decimal? costOfGoods, decimal? grossProfit, decimal? directProfit,
        bool feeIsEstimated = false, decimal feeExGst = 0m, decimal feeGst = 0m, decimal feeIncGst = 0m,
        decimal commissionAmount = 0m) =>
        new(true, sale, paymentType, isCosted, costOfGoods, grossProfit, directProfit,
            feeIsEstimated, feeExGst, feeGst, feeIncGst, commissionAmount);

    private static TransactionTotalsRowInputs NotCompleted(decimal sale, TransactionPaymentType paymentType) =>
        new(false, sale, paymentType, false, null, null, null, false, 0m, 0m, 0m, 0m);

    private static void AssertParity(IReadOnlyList<TransactionTotalsRowInputs> rows)
    {
        var batch = TransactionTotalsPolicy.Calculate(rows);

        var accumulator = new TransactionTotalsAccumulator();
        foreach (var row in rows) accumulator.Add(row);
        var incremental = accumulator.ToResult();

        Assert.Equal(batch, incremental);
    }

    [Fact]
    public void Empty_input()
    {
        AssertParity([]);
    }

    [Fact]
    public void Mixed_completed_and_not_completed_card_and_cash()
    {
        AssertParity([
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 6m),
            NotCompleted(9m, TransactionPaymentType.Card),
            NotCompleted(5m, TransactionPaymentType.Cash),
        ]);
    }

    [Fact]
    public void Complete_cogs_across_every_completed_row()
    {
        AssertParity([
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 6m),
            Completed(5m, TransactionPaymentType.Cash, true, 2m, 3m, 3m),
        ]);
    }

    [Fact]
    public void Incomplete_cogs_from_one_uncosted_completed_row()
    {
        AssertParity([
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 6m),
            Completed(5m, TransactionPaymentType.Cash, false, null, null, null),
        ]);
    }

    [Fact]
    public void Estimated_fees_mixed_with_completion_status()
    {
        AssertParity([
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 5.78m, feeIsEstimated: true,
                feeExGst: 0.2m, feeGst: 0.02m, feeIncGst: 0.22m),
            NotCompleted(9m, TransactionPaymentType.Card),
        ]);
    }

    [Fact]
    public void Commission_availability_varies_per_row()
    {
        AssertParity([
            Completed(10m, TransactionPaymentType.Card, true, 4m, 6m, 5m, commissionAmount: 1m),
            Completed(5m, TransactionPaymentType.Card, true, 2m, 3m, null), // commission/fee unavailable
            NotCompleted(9m, TransactionPaymentType.Card),
        ]);
    }

    [Fact]
    public void Large_mixed_scope_of_many_rows()
    {
        var rows = new List<TransactionTotalsRowInputs>();
        for (var i = 0; i < 2000; i++)
        {
            if (i % 7 == 0) { rows.Add(NotCompleted(i % 11, i % 2 == 0 ? TransactionPaymentType.Card : TransactionPaymentType.Cash)); continue; }
            var costed = i % 5 != 0;
            var sale = 3m + i % 13;
            var cost = costed ? (decimal?)(i % 6) : null;
            var gross = costed ? sale - cost : null;
            var direct = costed && i % 3 != 0 ? gross - 0.1m : null;
            rows.Add(Completed(sale, i % 2 == 0 ? TransactionPaymentType.Card : TransactionPaymentType.Cash,
                costed, cost, gross, direct,
                feeIsEstimated: i % 4 == 0, feeExGst: 0.1m, feeGst: 0.01m, feeIncGst: 0.11m,
                commissionAmount: i % 9 == 0 ? 0.5m : 0m));
        }

        AssertParity(rows);
    }
}
