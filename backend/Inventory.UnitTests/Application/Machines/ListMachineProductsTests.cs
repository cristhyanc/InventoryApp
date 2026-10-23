using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Nayax;
using InventoryApi.Tests.Application.Nayax;
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
    private const long MachineId = 1;

    /// <summary>
    /// A client whose machine list holds machine 1 at <paramref name="siteId"/>, which is the ordinary
    /// case since issue #846: the site comes from that list and the single-machine read is not made.
    /// </summary>
    private static Mock<INayaxLynxClient> NayaxClient(long? siteId, params NayaxMachineProduct[] machineProducts) =>
        NayaxClient(
            [new NayaxMachine { MachineID = MachineId, CustomerID = siteId }],
            machineProducts);

    /// <summary>
    /// A client whose machine list does not hold machine 1 - added in Nayax within the cache's ten
    /// minutes, or unknown to Nayax altogether - so only the single-machine read can say where it is.
    /// </summary>
    private static Mock<INayaxLynxClient> NayaxClientWithoutTheMachineListed(
        long? siteId, params NayaxMachineProduct[] machineProducts) =>
        NayaxClientWithoutTheMachineListed(
            mock => mock
                .Setup(client => client.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new NayaxMachine { MachineID = MachineId, CustomerID = siteId }),
            machineProducts);

    private static Mock<INayaxLynxClient> NayaxClientWithoutTheMachineListed(
        Action<Mock<INayaxLynxClient>> machineRead, params NayaxMachineProduct[] machineProducts)
    {
        var nayax = NayaxClient([new NayaxMachine { MachineID = 999, CustomerID = 404 }], machineProducts);
        machineRead(nayax);
        return nayax;
    }

    private static Mock<INayaxLynxClient> NayaxClient(
        List<NayaxMachine> fleet, params NayaxMachineProduct[] machineProducts)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(fleet);
        nayax.Setup(client => client.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. machineProducts]);
        return nayax;
    }

    private static ListMachineProducts UseCase(
        Mock<INayaxLynxClient> nayax,
        FakeProductCatalogStore catalog,
        decimal? feeExGst = 0.2m,
        INayaxProductLinkStore? productLinks = null,
        FakeSiteFactsStore? siteFacts = null) =>
        new(
            nayax.Object,
            catalog,
            productLinks ?? FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
            new ResolveMachineProductPricing(
                siteFacts ?? new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), feeExGst),
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

    /// <summary>
    /// The identity itself comes from the Nayax link table (issue #733): the machine slot reports a
    /// Nayax product id, and the row it produces is the local product that id is linked to - here a
    /// product with a different local key, which no comparison of the two numbers could have found.
    /// </summary>
    [Fact]
    public async Task Handle_MatchesTheSlotToTheProductItsNayaxLinkNames()
    {
        var catalog = new FakeProductCatalogStore(FakeProductCatalogStore.Product(4200, "Coke"));
        var nayax = NayaxClient(siteId: 91, new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 1 });

        var row = Assert.Single(await UseCase(
                nayax, catalog, productLinks: FakeNayaxProductLinkStore.Linking((200, 4200)))
            .Handle(1, CancellationToken.None));

        Assert.Equal(4200, row.Product.Id);
        Assert.Equal("Coke", row.Product.Name);
    }

    /// <summary>
    /// A local product numbered like somebody's Nayax product is not that product: with no link, the
    /// slot is dropped, exactly as an unmatched slot already was.
    /// </summary>
    [Fact]
    public async Task Handle_DropsASlotWhoseNayaxProductIsNotLinked_EvenWhenAProductHasThatId()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Unrelated product numbered 200"));
        var nayax = NayaxClient(siteId: 91, new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 1 });

        var rows = await UseCase(nayax, catalog, productLinks: FakeNayaxProductLinkStore.Linking((999, 4999)))
            .Handle(1, CancellationToken.None);

        Assert.Empty(rows);
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
    /// The live Nayax integration is called once per read and never once per row (AGENTS.md § Nayax
    /// integration). Since issue #846 those reads are the machine list - which the machine page has
    /// already read through the #840 cache - and the machine products, and the single-machine read
    /// <see cref="GetMachineDashboard"/> makes for the same page is not made a second time here.
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

        await UseCase(nayax, catalog).Handle(MachineId, CancellationToken.None);

        nayax.Verify(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Once);
        nayax.Verify(client => client.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()), Times.Once);
        nayax.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Issue #846: the machine's site is the <c>CustomerID</c> of its entry in the machine list, and
    /// the machine read the page already made in <see cref="GetMachineDashboard"/> is not repeated -
    /// one Nayax machine read across both requests instead of two.
    /// </summary>
    [Fact]
    public async Task Handle_PricesWithTheListedMachinesSite_WithoutReadingTheMachineAgain()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m));
        var siteFacts = new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m);
        var nayax = NayaxClient(siteId: 91, new NayaxMachineProduct { NayaxProductID = 200, RetailPrice = 5m });

        var row = Assert.Single(
            await UseCase(nayax, catalog, siteFacts: siteFacts).Handle(MachineId, CancellationToken.None));

        Assert.Equal([91L], siteFacts.ResolvedSiteIds);
        Assert.Equal(2.78m, row.SuggestedNetValue);
        nayax.Verify(client => client.GetMachineAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A listed machine with no <c>CustomerID</c> has no site, which is the list saying so - not a
    /// reason to go and ask again. Pricing runs with no site, exactly as it does today for a machine
    /// whose own record carries none.
    /// </summary>
    [Fact]
    public async Task Handle_PricesWithNoSite_WhenTheListedMachineHasNoCustomerId()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m));
        var siteFacts = new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m);
        var nayax = NayaxClient(siteId: null, new NayaxMachineProduct { NayaxProductID = 200, RetailPrice = 5m });

        var row = Assert.Single(
            await UseCase(nayax, catalog, siteFacts: siteFacts).Handle(MachineId, CancellationToken.None));

        Assert.Empty(siteFacts.ResolvedSiteIds);
        Assert.Null(row.SuggestedNetValue);
        Assert.Null(row.SuggestedPriceValue);
        nayax.Verify(client => client.GetMachineAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A machine the list does not hold - added in Nayax within the cached list's ten minutes, or
    /// unknown to Nayax - is read on its own, exactly as it was before issue #846.
    /// </summary>
    [Fact]
    public async Task Handle_ReadsTheMachineItself_WhenItIsNotInTheList()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m));
        var siteFacts = new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m);
        var nayax = NayaxClientWithoutTheMachineListed(
            siteId: 91,
            new NayaxMachineProduct { NayaxProductID = 200, RetailPrice = 5m });

        var row = Assert.Single(
            await UseCase(nayax, catalog, siteFacts: siteFacts).Handle(MachineId, CancellationToken.None));

        Assert.Equal([91L], siteFacts.ResolvedSiteIds);
        Assert.Equal(2.78m, row.SuggestedNetValue);
        nayax.Verify(client => client.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The listing is the same listing whichever read the site came from: the rows, their machine
    /// facts and their suggested values are identical for a machine the list holds at site 91 and for
    /// one only the machine read places there.
    /// </summary>
    [Fact]
    public async Task Handle_ReturnsTheSameListing_WhicheverReadTheSiteCameFrom()
    {
        var slots = new[]
        {
            new NayaxMachineProduct
            {
                NayaxProductID = 200,
                RetailPrice = 5m,
                CommissionValue = 1.5m,
                MDBCode = 12,
                PAR = 10,
                MissingStockByMDB = 4,
            },
            new NayaxMachineProduct { NayaxProductID = 201, RetailPrice = 8m, MDBCode = 11 },
        };

        // One catalogue for both runs, so the rows are compared over the very same catalogue products
        // and any difference between them is the listing's own.
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m),
            FakeProductCatalogStore.Product(201, "Chips", averageUnitCost: 3m));

        var fromTheList = await UseCase(NayaxClient(siteId: 91, slots), catalog)
            .Handle(MachineId, CancellationToken.None);
        var fromTheMachineRead = await UseCase(NayaxClientWithoutTheMachineListed(siteId: 91, slots), catalog)
            .Handle(MachineId, CancellationToken.None);

        Assert.Equal(fromTheList, fromTheMachineRead);
    }

    /// <summary>
    /// A machine Nayax does not know still fails as the controlled upstream failure the HTTP boundary
    /// turns into a 502, and the machine products are never read: the machine read is still made
    /// before them, so what the request answers with is unchanged.
    /// </summary>
    [Fact]
    public async Task Handle_SurfacesTheMachineReadFailure_WhenTheMachineIsNotInTheList()
    {
        var upstreamFailure = new InvalidOperationException("Nayax does not know this machine.");
        var nayax = NayaxClientWithoutTheMachineListed(
            mock => mock
                .Setup(client => client.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(upstreamFailure),
            new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 1 });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UseCase(nayax, new FakeProductCatalogStore(FakeProductCatalogStore.Product(200, "Coke")))
                .Handle(MachineId, CancellationToken.None));

        Assert.Same(upstreamFailure, thrown);
        nayax.Verify(
            client => client.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The permission regression issue #846's last criterion asks for: a Nayax account that grants
    /// <c>GET machines/{id}</c> and <c>GET machines/{id}/machineProducts</c> but refuses
    /// <c>GET machines</c> still gets its listing, priced by the machine's own record, because that
    /// is the read this listing was made of before the change. Taking the site from the fleet list
    /// was a request saving, not a new permission requirement.
    /// </summary>
    [Fact]
    public async Task Handle_ReadsTheMachineItself_WhenNayaxHasNotGrantedTheFleetList()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m));
        var siteFacts = new FakeSiteFactsStore(new Dictionary<decimal, decimal>(), 0.2m);
        var nayax = NayaxClientRefusingTheFleetList(
            new NayaxPermissionNotGrantedException("GetMachinesAsync"),
            mock => mock
                .Setup(client => client.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new NayaxMachine { MachineID = MachineId, CustomerID = 91 }),
            new NayaxMachineProduct { NayaxProductID = 200, RetailPrice = 5m });

        var row = Assert.Single(
            await UseCase(nayax, catalog, siteFacts: siteFacts).Handle(MachineId, CancellationToken.None));

        Assert.Equal([91L], siteFacts.ResolvedSiteIds);
        Assert.Equal(2.78m, row.SuggestedNetValue);
        nayax.Verify(client => client.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// And it is the same listing, row for row, as the one a granted fleet list produces: the
    /// fallback changes which read the site came from and nothing else.
    /// </summary>
    [Fact]
    public async Task Handle_ReturnsTheSameListing_WhenTheFleetListIsRefusedForLackOfPermission()
    {
        var slots = new[]
        {
            new NayaxMachineProduct
            {
                NayaxProductID = 200,
                RetailPrice = 5m,
                CommissionValue = 1.5m,
                MDBCode = 12,
                PAR = 10,
                MissingStockByMDB = 4,
            },
            new NayaxMachineProduct { NayaxProductID = 201, RetailPrice = 8m, MDBCode = 11 },
        };

        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(200, "Coke", averageUnitCost: 2m),
            FakeProductCatalogStore.Product(201, "Chips", averageUnitCost: 3m));

        var fromTheList = await UseCase(NayaxClient(siteId: 91, slots), catalog)
            .Handle(MachineId, CancellationToken.None);
        var afterTheRefusal = await UseCase(
                NayaxClientRefusingTheFleetList(
                    new NayaxPermissionNotGrantedException("GetMachinesAsync"),
                    mock => mock
                        .Setup(client => client.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new NayaxMachine { MachineID = MachineId, CustomerID = 91 }),
                    slots),
                catalog)
            .Handle(MachineId, CancellationToken.None);

        Assert.Equal(fromTheList, afterTheRefusal);
    }

    /// <summary>
    /// An account that is granted neither read is refused, exactly as it was before issue #846: the
    /// fallback serves the listing it used to serve, it does not manufacture one.
    /// </summary>
    [Fact]
    public async Task Handle_SurfacesThePermissionRefusal_WhenTheMachineReadIsRefusedToo()
    {
        var machineRefusal = new NayaxPermissionNotGrantedException("GetMachineAsync");
        var nayax = NayaxClientRefusingTheFleetList(
            new NayaxPermissionNotGrantedException("GetMachinesAsync"),
            mock => mock
                .Setup(client => client.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(machineRefusal),
            new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 1 });

        var thrown = await Assert.ThrowsAsync<NayaxPermissionNotGrantedException>(() =>
            UseCase(nayax, new FakeProductCatalogStore(FakeProductCatalogStore.Product(200, "Coke")))
                .Handle(MachineId, CancellationToken.None));

        Assert.Same(machineRefusal, thrown);
        nayax.Verify(
            client => client.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Nothing but that one classified permission answer falls back. An unusable connection - nothing
    /// stored, <c>NotConfigured</c>, or the <c>NeedsAttention</c> the status gate refuses - refuses
    /// the single-machine read in the same breath, so asking it would spend a request to reach the
    /// same refusal.
    /// </summary>
    [Fact]
    public async Task Handle_SurfacesTheNotConnectedRefusal_WithoutReadingTheMachine()
    {
        await AssertTheFleetFailurePropagates(
            new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention));
        await AssertTheFleetFailurePropagates(
            new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured));
    }

    /// <summary>
    /// A support session resolves no Nayax credentials at all (issue #551), and that refusal is not a
    /// statement about one permission: it propagates, so a reader from outside the business is still
    /// told the view is unavailable rather than being served a second request's worth of the
    /// business's data.
    /// </summary>
    [Fact]
    public async Task Handle_SurfacesTheSupportSessionRefusal_WithoutReadingTheMachine() =>
        await AssertTheFleetFailurePropagates(new SupportSessionNayaxUnavailableException());

    /// <summary>
    /// A cancellation is the caller's own and an upstream failure is an outage; neither says the
    /// fleet list is unavailable to this business, and a machine whose site came from a retry after
    /// an outage would be a working answer invented out of a broken one.
    /// </summary>
    [Fact]
    public async Task Handle_SurfacesACancellationAndAnUpstreamFleetFailure_WithoutReadingTheMachine()
    {
        await AssertTheFleetFailurePropagates(new OperationCanceledException());
        await AssertTheFleetFailurePropagates(new InvalidOperationException("The fleet read failed upstream."));
    }

    /// <summary>
    /// <paramref name="failure"/> reaches the caller unchanged, with neither the machine nor its
    /// products read: the one fallback is the classified fleet-permission answer and nothing else.
    /// </summary>
    private static async Task AssertTheFleetFailurePropagates(Exception failure)
    {
        var nayax = NayaxClientRefusingTheFleetList(
            failure,
            mock => mock
                .Setup(client => client.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new NayaxMachine { MachineID = MachineId, CustomerID = 91 }),
            new NayaxMachineProduct { NayaxProductID = 200, MDBCode = 1 });

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
            UseCase(nayax, new FakeProductCatalogStore(FakeProductCatalogStore.Product(200, "Coke")))
                .Handle(MachineId, CancellationToken.None));

        Assert.Same(failure, thrown);
        nayax.Verify(client => client.GetMachineAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        nayax.Verify(
            client => client.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A client whose machine-list read fails with <paramref name="fleetFailure"/>, with the
    /// single-machine read set up by <paramref name="machineRead"/> so a test can say whether the
    /// failure fell back to it or reached the caller.
    /// </summary>
    private static Mock<INayaxLynxClient> NayaxClientRefusingTheFleetList(
        Exception fleetFailure,
        Action<Mock<INayaxLynxClient>> machineRead,
        params NayaxMachineProduct[] machineProducts)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(fleetFailure);
        nayax.Setup(client => client.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. machineProducts]);
        machineRead(nayax);
        return nayax;
    }
}
