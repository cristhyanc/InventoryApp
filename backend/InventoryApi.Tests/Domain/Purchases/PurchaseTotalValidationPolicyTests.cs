using System;
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
            totalAmount: 20.02m,
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
            totalAmount: 20.03m,
            deliveryCost: null,
            packageCost: null,
            items: new[] { new PurchaseTotalValidationItem(20m, 1m) });

        Assert.True(result.HasMismatch);
        Assert.Equal(0.03m, result.Difference);
    }
}
