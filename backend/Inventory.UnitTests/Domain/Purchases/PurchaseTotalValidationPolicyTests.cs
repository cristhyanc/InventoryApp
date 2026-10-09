using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

public class PurchaseTotalValidationPolicyTests
{
    [Fact]
    public void Null_total_amount_is_not_a_mismatch_and_has_no_calculated_values()
    {
        var result = PurchaseTotalValidationPolicy.Evaluate(
            totalAmount: null,
            deliveryCost: null,
            packageCost: null,
            items: Array.Empty<PurchaseTotalValidationItem>());

        Assert.False(result.HasMismatch);
        Assert.Null(result.ItemSubtotal);
        Assert.Null(result.CalculatedTotal);
        Assert.Null(result.Difference);
    }

    [Fact]
    public void Matching_total_within_tolerance_is_not_a_mismatch()
    {
        var result = PurchaseTotalValidationPolicy.Evaluate(
            totalAmount: 27m,
            deliveryCost: 5m,
            packageCost: 2m,
            items: new[] { new PurchaseTotalValidationItem(20m, 1m) });

        Assert.False(result.HasMismatch);
        Assert.Equal(20m, result.ItemSubtotal);
        Assert.Equal(27m, result.CalculatedTotal);
        Assert.Null(result.Difference);
    }

    [Fact]
    public void Total_exceeding_tolerance_is_a_mismatch_with_the_difference()
    {
        var result = PurchaseTotalValidationPolicy.Evaluate(
            totalAmount: 30m,
            deliveryCost: null,
            packageCost: null,
            items: new[] { new PurchaseTotalValidationItem(20m, 1m) });

        Assert.True(result.HasMismatch);
        Assert.Equal(20m, result.ItemSubtotal);
        Assert.Equal(20m, result.CalculatedTotal);
        Assert.Equal(10m, result.Difference);
    }

    [Fact]
    public void Difference_at_exactly_the_tolerance_boundary_is_not_a_mismatch()
    {
        var result = PurchaseTotalValidationPolicy.Evaluate(
            totalAmount: 21.00m,
            deliveryCost: null,
            packageCost: null,
            items: new[] { new PurchaseTotalValidationItem(20m, 1m) });

        Assert.False(result.HasMismatch);
        Assert.Null(result.Difference);
    }

    [Fact]
    public void Difference_just_beyond_the_tolerance_boundary_is_a_mismatch()
    {
        var result = PurchaseTotalValidationPolicy.Evaluate(
            totalAmount: 21.01m,
            deliveryCost: null,
            packageCost: null,
            items: new[] { new PurchaseTotalValidationItem(20m, 1m) });

        Assert.True(result.HasMismatch);
        Assert.Equal(1.01m, result.Difference);
    }

    [Theory]
    // Entered $100.00 against each calculated total from the issue's acceptance criteria.
    [InlineData(100.00, false)] // no difference
    [InlineData(100.99, false)] // $0.99 over - within tolerance
    [InlineData(99.01, false)]  // $0.99 under - within tolerance
    [InlineData(101.00, false)] // exactly $1.00 over - still within tolerance (inclusive)
    [InlineData(99.00, false)]  // exactly $1.00 under - still within tolerance (inclusive)
    [InlineData(101.01, true)]  // $1.01 over - exceeds tolerance
    [InlineData(98.99, true)]   // $1.01 under - exceeds tolerance
    public void Warning_triggers_only_when_the_difference_from_a_100_dollar_entry_exceeds_one_dollar(
        double calculatedTotal, bool expectedMismatch)
    {
        var result = PurchaseTotalValidationPolicy.Evaluate(
            totalAmount: 100.00m,
            deliveryCost: null,
            packageCost: null,
            items: new[] { new PurchaseTotalValidationItem(1m, (decimal)calculatedTotal) });

        Assert.Equal(expectedMismatch, result.HasMismatch);
    }

    [Fact]
    public void A_triggered_warning_still_reports_the_actual_entered_and_calculated_amounts()
    {
        var result = PurchaseTotalValidationPolicy.Evaluate(
            totalAmount: 100.00m,
            deliveryCost: null,
            packageCost: null,
            items: new[] { new PurchaseTotalValidationItem(1m, 101.01m) });

        Assert.True(result.HasMismatch);
        Assert.Equal(101.01m, result.CalculatedTotal);
        Assert.Equal(1.01m, result.Difference);
    }

    [Fact]
    public void Sub_cent_item_subtotal_precision_does_not_falsely_cross_the_one_dollar_boundary()
    {
        // Quantity 3 at a unit cost carrying a third decimal place totals $99.9951, a sub-cent
        // amount the entered total (itself always currency precision) can never carry. The raw
        // difference from $101.00 is $1.0049 - just over $1.00 - but it rounds to exactly $1.00 in
        // currency terms, so it must not warn.
        var result = PurchaseTotalValidationPolicy.Evaluate(
            totalAmount: 101.00m,
            deliveryCost: null,
            packageCost: null,
            items: new[] { new PurchaseTotalValidationItem(3m, 33.3317m) });

        Assert.False(result.HasMismatch);
        Assert.Null(result.Difference);
    }
}
