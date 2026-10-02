using Inventory.Domain.FinancialConfiguration;
using Xunit;

namespace InventoryApi.Tests.Domain.FinancialConfiguration;

public class NayaxProcessingFeePolicyTests
{
    [Fact]
    public void A_zero_actual_fee_is_authoritative_for_its_covered_day()
    {
        var reimbursement = new ProcessingFeeReimbursement(
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 1),
            [new ImportedProcessingFee("processing", null, false, 0m, 0m, null)],
            []);
        var sales = new[]
        {
            new CompletedCardTransaction(new DateTime(2026, 9, 1, 12, 0, 0), "Credit Card"),
            new CompletedCardTransaction(new DateTime(2026, 9, 2, 12, 0, 0), "Credit Card")
        };

        var result = NayaxProcessingFeePolicy.Calculate(
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 2),
            null,
            [reimbursement],
            sales,
            [new EffectiveNayaxFeeRate(new DateTime(2026, 1, 1), .17m)]);

        Assert.Equal(0m, result.ActualFeeExGst);
        Assert.Equal(0m, result.ActualFeeGst);
        Assert.Equal(.17m, result.EstimatedFeeExGst);
        Assert.Equal(1, result.EstimatedCardTransactionCount);
        Assert.Equal(new DateTime(2026, 9, 1), result.ActualFeeCoverageEndDate);
    }

    [Fact]
    public void Cash_and_unknown_methods_do_not_get_estimated_and_missing_card_rates_are_counted()
    {
        var result = NayaxProcessingFeePolicy.Calculate(
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 1),
            null,
            [],
            [
                new CompletedCardTransaction(new DateTime(2026, 9, 1), "Credit Card"),
                new CompletedCardTransaction(new DateTime(2026, 9, 1), "Cash"),
                new CompletedCardTransaction(new DateTime(2026, 9, 1), "Unknown")
            ],
            []);

        Assert.Equal(0m, result.EstimatedFeeExGst);
        Assert.Equal(0, result.EstimatedCardTransactionCount);
        Assert.Equal(1, result.MissingRateTransactionCount);
    }
}
