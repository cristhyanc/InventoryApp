using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Application.Time;
using Inventory.Domain.Sites;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// How the two site reads identify a product (issue #733): the <c>NayaxProductID</c> each machine
/// selection reports is resolved through the Nayax link table, so a site product row - and the
/// catalogue activity a low/empty alert is decided from - belong to the local product the link
/// names rather than to whichever product happens to carry that number as its key.
/// </summary>
public class GetSiteProductLinkResolutionTests
{
    private const long SiteId = 42;
    private const long NayaxProductId = 100;
    private const long LocalProductId = 4100;

    private static readonly IBusinessCalendar Calendar = new FakeBusinessCalendar(new DateTime(2026, 3, 12));
    private static readonly FixedSydneyTime Time = FixedSydneyTime.PinnedToNow();

    /// <summary>One machine at the site, holding one nearly empty slot of Nayax product 100.</summary>
    private static RecordingNayaxLynxClient Fleet() =>
        new(
            [new NayaxMachine { MachineID = 10, CustomerID = SiteId, MachineName = "Lobby" }],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [10] =
                [
                    new()
                    {
                        MachineID = 10,
                        NayaxProductID = NayaxProductId,
                        MDBCode = 3,
                        RetailPrice = 5m,
                        PAR = 5,
                        MissingStockByMDB = 5,
                        VendOutAlertThreshold = 1,
                    },
                ],
            });

    private static RecordingSiteFactsStore ProductFacts(long localProductId) =>
        new(
            costBasis: new Dictionary<long, SiteProductCostBasis>
            {
                [localProductId] = new("Chips", AverageUnitCost: 2m, HasCostBasis: true),
            },
            commissionByPrice: new Dictionary<decimal, decimal> { [5m] = 0.5m },
            feeExGst: 0.20m);

    private static RecordingSiteFactsStore ActivityFacts(long localProductId) =>
        new(productActivity: [new SiteProductActivityFact(localProductId, IsActive: true)]);

    private static Task<IReadOnlyList<SiteProductRecord>> ProductsAsync(
        ISiteFactsStore facts, INayaxProductLinkStore productLinks) =>
        new GetSiteProducts(Fleet(), facts, productLinks, Calendar, TestCurrentCapabilities.Owner)
            .Handle(SiteId, CancellationToken.None);

    private static Task<IReadOnlyList<SiteSummary>> SummariesAsync(
        ISiteFactsStore facts, INayaxProductLinkStore productLinks) =>
        new GetSiteSummaries(
                Fleet(), facts, productLinks, new SiteNameResolver(), Time.Clock, Time.Calendar,
                TestCurrentCapabilities.Owner)
            .Handle(CancellationToken.None);

    [Fact]
    public async Task A_site_product_row_is_the_local_product_the_link_names()
    {
        var product = Assert.Single(await ProductsAsync(
            ProductFacts(LocalProductId),
            FakeNayaxProductLinkStore.Linking((NayaxProductId, LocalProductId))));

        Assert.Equal(LocalProductId, product.ProductId);
        Assert.Equal("Chips", product.Name);
        Assert.Equal(3, product.MdbCode);
    }

    /// <summary>
    /// With no link the selection names no product of this business, so no row is produced - which
    /// is what already happened to a selection with no catalogue cost basis. In particular the row
    /// is not produced for the unrelated local product numbered 100.
    /// </summary>
    [Fact]
    public async Task An_unlinked_selection_produces_no_row_for_the_product_whose_id_equals_it()
    {
        var products = await ProductsAsync(
            ProductFacts(NayaxProductId),
            FakeNayaxProductLinkStore.Linking((999, 4999)));

        Assert.Empty(products);
    }

    [Fact]
    public async Task A_site_alert_counts_the_linked_local_product()
    {
        var site = Assert.Single(await SummariesAsync(
            ActivityFacts(LocalProductId),
            FakeNayaxProductLinkStore.Linking((NayaxProductId, LocalProductId))));

        Assert.Equal(1, site.EmptyProductCount);
    }

    /// <summary>
    /// An unlinked selection is unmapped: it raises no product alert - not even through the
    /// unrelated active product numbered 100 - while still counting toward the site's physical stock
    /// percentage, exactly as an unmapped selection always has.
    /// </summary>
    [Fact]
    public async Task An_unlinked_selection_raises_no_alert_for_the_product_whose_id_equals_it()
    {
        var site = Assert.Single(await SummariesAsync(
            ActivityFacts(NayaxProductId),
            FakeNayaxProductLinkStore.Linking((999, 4999))));

        Assert.Equal(0, site.EmptyProductCount);
        Assert.Equal(0, site.LowProductCount);
        Assert.Equal(0m, site.TotalStockPercentage);
    }
}
