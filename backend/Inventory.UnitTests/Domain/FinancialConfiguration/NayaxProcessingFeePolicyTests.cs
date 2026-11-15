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

    /// <summary>
    /// Issue #1257: a fee row that records only its GST-inclusive amount. The ex-GST amount already
    /// removed one eleventh, so the GST is the other eleventh - not zero, which reported an 11.00
    /// fee as 10.00 including GST and credited no fee GST at all. The fee keeps its own total.
    /// </summary>
    [Fact]
    public void An_inclusive_only_fee_reports_its_gst_and_keeps_its_total()
    {
        var result = Calculate(new ImportedProcessingFee("processing", null, false, null, 11m, null));

        Assert.Equal(10m, result.ActualFeeExGst);
        Assert.Equal(1m, result.ActualFeeGst);
        Assert.Equal(11m, result.ActualFeeIncGst);
    }

    /// <summary>
    /// A fee that states its own VAT percentage is untouched: that percentage decides the GST, not
    /// the Australian one eleventh.
    /// </summary>
    [Fact]
    public void A_fee_with_an_explicit_vat_percentage_is_unchanged()
    {
        var result = Calculate(new ImportedProcessingFee("processing", null, false, null, 11m, 10m));

        Assert.Equal(10m, result.ActualFeeExGst);
        Assert.Equal(1m, result.ActualFeeGst);
        Assert.Equal(11m, result.ActualFeeIncGst);
    }

    /// <summary>A fee that records both its ex-GST and its inclusive amount is untouched too.</summary>
    [Fact]
    public void A_fee_with_both_amounts_is_unchanged()
    {
        var result = Calculate(new ImportedProcessingFee("processing", null, false, 10m, 11m, null));

        Assert.Equal(10m, result.ActualFeeExGst);
        Assert.Equal(1m, result.ActualFeeGst);
        Assert.Equal(11m, result.ActualFeeIncGst);
    }

    private static NayaxProcessingFeeTotals Calculate(ImportedProcessingFee fee)
    {
        var reimbursement = new ProcessingFeeReimbursement(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 1), [fee], []);

        return NayaxProcessingFeePolicy.Calculate(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 1), null, [reimbursement], [], []);
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
