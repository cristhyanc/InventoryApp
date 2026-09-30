using Inventory.Domain.Sites;
using Xunit;

namespace InventoryApi.Tests.Domain.Sites;

public class SiteProductPricingPolicyTests
{
    private static readonly Dictionary<long, SiteProductCostBasis> CostBasis = new()
    {
        [1] = new SiteProductCostBasis("Product", 2m, true),
    };

    [Fact]
    public void Estimated_card_profit_is_null_when_commission_configuration_is_unavailable()
    {
        var facts = new[] { new SiteProductPriceFact(1, RetailPrice: 5m, CommissionAmount: 0.5m, Par: 5, MissingStockByMdb: 0) };

        var result = Assert.Single(SiteProductPricingPolicy.Calculate(facts, CostBasis, feeExGst: 0.2m, commissionConfigurationUnavailable: true));

        Assert.Null(result.EstimatedCardProfit);
        Assert.Equal(5m, result.SitePrice);
    }

    [Fact]
    public void Estimated_card_profit_subtracts_commission_cost_and_fee_including_gst()
    {
        var facts = new[] { new SiteProductPriceFact(1, RetailPrice: 5m, CommissionAmount: 0.5m, Par: 5, MissingStockByMdb: 0) };

        var result = Assert.Single(SiteProductPricingPolicy.Calculate(facts, CostBasis, feeExGst: 0.2m, commissionConfigurationUnavailable: false));

        // feeIncGst = 0.2 + 0.2*10/100 = 0.22; profit = 5 - 0.5 - 2 - 0.22 = 2.28
        Assert.Equal(2.28m, result.EstimatedCardProfit);
    }

    [Fact]
    public void Unpriced_items_still_contribute_to_quantity_but_not_to_price_or_profit()
    {
        var facts = new[]
        {
            new SiteProductPriceFact(1, RetailPrice: null, CommissionAmount: 0m, Par: 5, MissingStockByMdb: 1),
        };

        var result = Assert.Single(SiteProductPricingPolicy.Calculate(facts, CostBasis, feeExGst: 0.2m, commissionConfigurationUnavailable: false));

        Assert.Equal(0m, result.SitePrice);
        Assert.Null(result.EstimatedCardProfit);
        Assert.Equal(4, result.QuantityInStock);
        Assert.Equal(5, result.MaxStock);
    }

    [Fact]
    public void Products_without_a_resolvable_cost_basis_are_excluded()
    {
        var facts = new[] { new SiteProductPriceFact(99, RetailPrice: 5m, CommissionAmount: 0m, Par: 5, MissingStockByMdb: 0) };

        Assert.Empty(SiteProductPricingPolicy.Calculate(facts, CostBasis, feeExGst: 0.2m, commissionConfigurationUnavailable: false));
    }
}
