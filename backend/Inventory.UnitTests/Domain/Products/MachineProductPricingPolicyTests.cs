using Inventory.Domain.Products;
using Xunit;

namespace InventoryApi.Tests.Domain.Products;

public class MachineProductPricingPolicyTests
{
    [Fact]
    public void Calculate_ReturnsSuggestedNetAndPrice_WhenEveryFactIsKnown()
    {
        var (net, price) = MachineProductPricingPolicy.Calculate(
            machinePrice: 10m,
            averageUnitCost: 2m,
            hasCostBasis: true,
            commissionAmount: 1.0m,
            commissionPerDollar: 0.10m,
            feeIncGst: 0.22m,
            commissionConfigurationUnavailable: false);

        Assert.Equal(6.78m, net);
        Assert.Equal(5.55m, price);
    }

    [Fact]
    public void Calculate_SuggestedPriceIsNull_WhenCommissionPerDollarWouldMakeDenominatorNonPositive()
    {
        var (net, price) = MachineProductPricingPolicy.Calculate(
            machinePrice: 10m,
            averageUnitCost: 2m,
            hasCostBasis: true,
            commissionAmount: 5m,
            commissionPerDollar: 0.6m,
            feeIncGst: 0.22m,
            commissionConfigurationUnavailable: false);

        Assert.Equal(2.78m, net);
        Assert.Null(price);
    }

    [Fact]
    public void Calculate_ReturnsNoSuggestion_WhenFeeRateIsUnavailable()
    {
        var (net, price) = MachineProductPricingPolicy.Calculate(
            machinePrice: 10m, averageUnitCost: 2m, hasCostBasis: true,
            commissionAmount: 1m, commissionPerDollar: 0.1m, feeIncGst: null, commissionConfigurationUnavailable: false);

        Assert.Null(net);
        Assert.Null(price);
    }

    [Fact]
    public void Calculate_ReturnsNoSuggestion_WhenCommissionConfigurationIsUnavailable()
    {
        var (net, price) = MachineProductPricingPolicy.Calculate(
            machinePrice: 10m, averageUnitCost: 2m, hasCostBasis: true,
            commissionAmount: 1m, commissionPerDollar: 0.1m, feeIncGst: 0.22m, commissionConfigurationUnavailable: true);

        Assert.Null(net);
        Assert.Null(price);
    }

    [Fact]
    public void Calculate_ReturnsNoSuggestion_WhenThereIsNoCostBasis()
    {
        var (net, price) = MachineProductPricingPolicy.Calculate(
            machinePrice: 10m, averageUnitCost: 0m, hasCostBasis: false,
            commissionAmount: 1m, commissionPerDollar: 0.1m, feeIncGst: 0.22m, commissionConfigurationUnavailable: false);

        Assert.Null(net);
        Assert.Null(price);
    }
}
