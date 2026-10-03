using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Domain.Sites;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// Scoped-EF-context concurrency regression tests for <see cref="GetSiteProducts"/> (issue #313). Its
/// cost-basis, commission and fee reads all land on one scoped <c>AppDbContext</c>, which supports a
/// single operation at a time, so they must be awaited one at a time - while the site's independent
/// Nayax machine-product requests must stay a concurrent fan-out.
/// </summary>
public class GetSiteProductsTests
{
    private const long SiteId = 42;

    private static readonly Dictionary<long, SiteProductCostBasis> CostBasis = new()
    {
        [1] = new SiteProductCostBasis("Chips", AverageUnitCost: 2m, HasCostBasis: true),
    };

    private static readonly Dictionary<decimal, decimal> CommissionByPrice = new() { [5m] = 0.5m };

    /// <summary>
    /// The three reads used to be started together and awaited with <c>Task.WhenAll</c>. The recorder
    /// yields inside each call, so an overlapping implementation produces an interleaved trace and a
    /// concurrency count above one, which SQLite's synchronous async implementation would otherwise hide
    /// locally while a real asynchronous provider rejects it.
    /// </summary>
    [Fact]
    public async Task Handle_AwaitsTheCostBasisCommissionAndFeeReadsOneAtATime()
    {
        var facts = new RecordingSiteFactsStore(
            costBasis: CostBasis, commissionByPrice: CommissionByPrice, feeExGst: 0.20m);
        var useCase = new GetSiteProducts(Fleet(), facts);

        await useCase.Handle(SiteId, CancellationToken.None);

        Assert.Equal(
            new[] { "start:costBasis", "end:costBasis", "start:commission", "end:commission", "start:fee", "end:fee" },
            facts.Trace);
        Assert.Equal(1, facts.MaxConcurrentCalls);
    }

    /// <summary>Serializing the reads must not change any priced value the preview reports.</summary>
    [Fact]
    public async Task Handle_KeepsTheEstimatedCardProfitAndStockFigures()
    {
        var facts = new RecordingSiteFactsStore(
            costBasis: CostBasis, commissionByPrice: CommissionByPrice, feeExGst: 0.20m);
        var useCase = new GetSiteProducts(Fleet(), facts);

        var product = Assert.Single(await useCase.Handle(SiteId, CancellationToken.None));

        Assert.Equal("Chips", product.Name);
        Assert.Equal(2m, product.AverageUnitCost);
        Assert.Equal(5m, product.SitePrice);
        // 5.00 retail - 0.50 commission - 2.00 cost - 0.22 fee including GST.
        Assert.Equal(2.28m, product.EstimatedCardProfit);
        Assert.Equal(4, product.QuantityInStock);
        Assert.Equal(5, product.MaxStock);
    }

    /// <summary>
    /// Nayax machine-product requests share no scoped EF context, so serializing the database reads must
    /// leave this site's bounded fan-out concurrent. The gated client waits (bounded) until both machine
    /// requests have started, so a serialized implementation fails rather than hangs.
    /// </summary>
    [Fact]
    public async Task Handle_KeepsTheNayaxMachineProductFanOutConcurrent()
    {
        var nayax = new RecordingNayaxLynxClient(
            [
                new NayaxMachine { MachineID = 10, CustomerID = SiteId },
                new NayaxMachine { MachineID = 11, CustomerID = SiteId },
            ],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [10] = [new() { MachineID = 10, NayaxProductID = 1, RetailPrice = 5m, PAR = 5, MissingStockByMDB = 1 }],
                [11] = [new() { MachineID = 11, NayaxProductID = 1, RetailPrice = 5m, PAR = 5, MissingStockByMDB = 1 }],
            },
            expectedConcurrentMachineProductCalls: 2);
        var facts = new RecordingSiteFactsStore(
            costBasis: CostBasis, commissionByPrice: CommissionByPrice, feeExGst: 0.20m);
        var useCase = new GetSiteProducts(nayax, facts);

        var product = Assert.Single(await useCase.Handle(SiteId, CancellationToken.None));

        Assert.Equal(2, nayax.MaxConcurrentMachineProductCalls);
        Assert.Equal(5m, product.SitePrice);
        Assert.Equal(8, product.QuantityInStock);
    }

    private static RecordingNayaxLynxClient Fleet() =>
        new(
            [
                new NayaxMachine { MachineID = 10, CustomerID = SiteId },
                new NayaxMachine { MachineID = 99, CustomerID = 7 },
            ],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [10] = [new() { MachineID = 10, NayaxProductID = 1, RetailPrice = 5m, PAR = 5, MissingStockByMDB = 1 }],
            });
}
