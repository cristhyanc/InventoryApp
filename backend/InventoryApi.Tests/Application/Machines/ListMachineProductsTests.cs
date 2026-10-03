using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using InventoryApi.Tests.Application.Products;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Machines;

/// <summary>
/// Application-layer tests for the migrated machine product listing use case (issue #240), against
/// fake catalogue/commission ports and a mocked Nayax client so no EF Core or SQLite is involved. The
/// financial outcome of the suggested-pricing calculation stays covered by
/// <c>MachineProfitabilityTests</c>'s pre-existing <c>Product_pricing_*</c> cases over a real
/// database; what these cases pin is the orchestration the use case took over from
/// <c>MachineService.GetMachineProducts</c>.
/// </summary>
public class ListMachineProductsTests
{
    private static Mock<INayaxLynxClient> NayaxClient(long? siteId, params NayaxMachineProduct[] machineProducts)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachineAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NayaxMachine { MachineID = 1, CustomerID = siteId });
        nayax.Setup(client => client.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. machineProducts]);
        return nayax;
    }

    private static ListMachineProducts UseCase(
        Mock<INayaxLynxClient> nayax, FakeProductCatalogStore catalog, decimal? feeExGst = 0.2m) =>
        new(
            nayax.Object,
            catalog,
            new ResolveMachineProductPricing(
                new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), feeExGst),
                new FakeBusinessCalendar(new DateTime(2026, 3, 12))));

    [Fact]
    public async Task Handle_OverlaysTheMachineSlotFactsOnTheCatalogueProduct()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", quantityInStock: 50, averageUnitCost: 2m));
        var nayax = NayaxClient(
            siteId: 91,
            new NayaxMachineProduct
            {
                NayaxProductID = 200,
                RetailPrice = 5m,
                CommissionValue = 1.5m,
                MDBCode = 12,
                PAR = 10,
                MissingStockByMDB = 4,
            });

        var row = Assert.Single(await UseCase(nayax, catalog).Handle(1, CancellationToken.None));

        Assert.Equal("Coke", row.Product.Name);
        Assert.Equal(5m, row.MachinePrice);
        Assert.Equal(1.5m, row.CommissionValue);
        Assert.Equal(12, row.MdbCode);
        // The slot's own stock (PAR minus missing), not the product's storage stock of 50.
        Assert.Equal(6, row.QuantityInStock);
        Assert.Equal(10, row.MaxStockInMachine);
    }

    [Fact]
    public async Task Handle_DefaultsMissingNayaxPriceCommissionAndStockToZero()
    {
        var catalog = new FakeProductCatalogStore(FakeProductCatalogStore.Product(200, "Coke"));
        var nayax = NayaxClient(siteId: 91, new NayaxMachineProduct { NayaxProductID = 200 });

        var row = Assert.Single(await UseCase(nayax, catalog).Handle(1, CancellationToken.None));

        Assert.Equal(0m, row.MachinePrice);
        Assert.Equal(0m, row.CommissionValue);
        Assert.Equal(0, row.QuantityInStock);
        Assert.Null(row.MdbCode);
        Assert.Null(row.MaxStockInMachine);
    }

    [Fact]
    public async Task Handle_DropsAMachineProductWithNoMatchingCatalogueProduct()
    {
        var catalog = new FakeProductCatalogStore(FakeProductCatalogStore.Product(200, "Coke"));
        var nayax = NayaxClient(
            siteId: 91,
            new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 1 },
            new NayaxMachineProduct { NayaxProductID = 999, MDBCode = 2 },
            new NayaxMachineProduct { NayaxProductID = null, MDBCode = 3 });

        var row = Assert.Single(await UseCase(nayax, catalog).Handle(1, CancellationToken.None));

        Assert.Equal(200, row.Product.Id);
    }

    [Fact]
    public async Task Handle_OrdersTheListingByMdbCode()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke"),
            FakeProductCatalogStore.Product(201, "Chips"));
        var nayax = NayaxClient(
            siteId: 91,
            new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 20 },
            new NayaxMachineProduct { NayaxProductID = 201, MDBCode = 10 });

        var rows = await UseCase(nayax, catalog).Handle(1, CancellationToken.None);

        Assert.Equal([10, 20], rows.Select(row => row.MdbCode));
    }

    /// <summary>
    /// One machine can list the same catalogue product in two slots at different prices. Each slot
    /// keeps its own price and its own suggested values; a product-id-keyed match would collapse them.
    /// </summary>
    [Fact]
    public async Task Handle_KeepsEachSlotSeparate_WhenOneProductAppearsTwiceAtDifferentPrices()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m));
        var nayax = NayaxClient(
            siteId: 91,
            new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 10, RetailPrice = 5m },
            new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 20, RetailPrice = 8m });

        var rows = await UseCase(nayax, catalog).Handle(1, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal([5m, 8m], rows.Select(row => row.MachinePrice));
        Assert.Equal(2.78m, rows[0].SuggestedNetValue);
        Assert.Equal(5.78m, rows[1].SuggestedNetValue);
    }

    /// <summary>
    /// A machine with no site has nothing to resolve commission against, so there is nothing
    /// authoritative to suggest - the rows still come back, with no suggestion.
    /// </summary>
    [Fact]
    public async Task Handle_ReturnsRowsWithoutSuggestions_WhenTheMachineHasNoSite()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m));
        var nayax = NayaxClient(siteId: null, new NayaxMachineProduct { NayaxProductID = 200, RetailPrice = 5m });

        var row = Assert.Single(await UseCase(nayax, catalog).Handle(1, CancellationToken.None));

        Assert.Null(row.SuggestedNetValue);
        Assert.Null(row.SuggestedPriceValue);
    }

    /// <summary>
    /// The migration must not change how often, or with what, the live Nayax integration is called:
    /// one machine read and one machine-product read per request, and never one call per row
    /// (AGENTS.md § Nayax integration).
    /// </summary>
    [Fact]
    public async Task Handle_CallsNayaxExactlyOncePerReadRegardlessOfRowCount()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke"),
            FakeProductCatalogStore.Product(201, "Chips"));
        var nayax = NayaxClient(
            siteId: 91,
            new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 1 },
            new NayaxMachineProduct { NayaxProductID = 201, MDBCode = 2 });

        await UseCase(nayax, catalog).Handle(1, CancellationToken.None);

        nayax.Verify(client => client.GetMachineAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        nayax.Verify(client => client.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        nayax.VerifyNoOtherCalls();
    }
}
