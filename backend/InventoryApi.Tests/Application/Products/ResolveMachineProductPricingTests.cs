using Inventory.Application.Products;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

public class ResolveMachineProductPricingTests
{
    [Fact]
    public async Task Handle_ReturnsNoSuggestions_WhenTheMachineHasNoSite()
    {
        var useCase = new ResolveMachineProductPricing(new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m));

        var results = await useCase.Handle(
            siteId: null,
            facts: [new MachineProductPricingFact(200, 10m, 2m)],
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Null(result.SuggestedNetValue);
        Assert.Null(result.SuggestedPriceValue);
    }

    [Fact]
    public async Task Handle_ReturnsNoSuggestions_WhenTheCommissionConfigurationIsUnavailable()
    {
        var facts = new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m, configurationUnavailable: true);
        var useCase = new ResolveMachineProductPricing(facts);

        var results = await useCase.Handle(91, [new MachineProductPricingFact(200, 10m, 2m)], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Null(result.SuggestedNetValue);
        Assert.Null(result.SuggestedPriceValue);
    }

    /// <summary>
    /// A vending machine can stock the same catalogue product in more than one slot at a different
    /// price. Results must stay positional (not deduplicated by product id), or one slot's suggested
    /// values would silently overwrite the other's.
    /// </summary>
    [Fact]
    public async Task Handle_ComputesEachFactIndependently_WhenTheSameProductAppearsTwiceAtDifferentPrices()
    {
        var commissionByPrice = new Dictionary<decimal, decimal> { [10m] = 1.0m, [20m] = 4.0m, [1m] = 0.1m };
        var store = new FakeSiteFactsStore(commissionByPrice, 0.2m);
        var useCase = new ResolveMachineProductPricing(store);

        var results = await useCase.Handle(
            siteId: 91,
            facts:
            [
                new MachineProductPricingFact(200, MachinePrice: 10m, AverageUnitCost: 2m),
                new MachineProductPricingFact(200, MachinePrice: 20m, AverageUnitCost: 2m),
            ],
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(6.78m, results[0].SuggestedNetValue);
        Assert.Equal(5.55m, results[0].SuggestedPriceValue);
        Assert.Equal(13.78m, results[1].SuggestedNetValue);
        Assert.Equal(5.55m, results[1].SuggestedPriceValue);
    }

    [Fact]
    public async Task Handle_TreatsNoAgreementsAsZeroCommission_NotAsUnavailable()
    {
        var store = new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m, configurationUnavailable: false);
        var useCase = new ResolveMachineProductPricing(store);

        var results = await useCase.Handle(91, [new MachineProductPricingFact(200, 10m, 2m)], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(7.78m, result.SuggestedNetValue);
    }
}
