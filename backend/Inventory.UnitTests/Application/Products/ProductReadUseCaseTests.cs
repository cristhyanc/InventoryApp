using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using Inventory.Domain.Nayax;
using InventoryApi.Tests.Application.Nayax;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// Application-layer tests for the migrated product read use cases (issue #240), against the fake
/// catalogue port so no EF Core or SQLite is involved. The equivalent behaviour over a real database -
/// which is what proves the EF adapter's filtering, ordering and include graph - stays covered by
/// <c>InventoryApi.Tests.Services.ProductServiceTests</c> and
/// <c>EfProductCatalogStoreTenancyTests</c>.
/// </summary>
public class ProductReadUseCaseTests
{
    private static CalculateReorderNeeds ReorderNeeds(
        IReadOnlyDictionary<long, int>? machineReplenishmentNeed = null,
        IReadOnlyDictionary<long, decimal>? onOrderQuantity = null)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineReplenishmentNeed is null || machineReplenishmentNeed.Count == 0
                ? []
                : [new NayaxMachine { MachineID = 1 }]);
        nayax.Setup(client => client.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((machineReplenishmentNeed ?? new Dictionary<long, int>())
                .Select(entry => new NayaxMachineProduct { NayaxProductID = entry.Key, MissingStockByMDB = entry.Value })
                .ToList());

        var outstanding = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstanding.Setup(store => store.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(onOrderQuantity ?? new Dictionary<long, decimal>());

        return new CalculateReorderNeeds(
            nayax.Object, outstanding.Object, FakeNayaxProductLinkStore.EveryIdLinkedToItself());
    }

    /// <summary>
    /// The same reorder calculation for a business the Nayax status gate refuses (issue #726): the
    /// fleet read fails closed inside the credential provider, so reorder answers in storage-only
    /// mode and the outstanding supplier orders are still read.
    /// </summary>
    private static CalculateReorderNeeds ReorderNeedsWithoutAProvider(
        IReadOnlyDictionary<long, decimal> onOrderQuantity)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured));

        var outstanding = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstanding.Setup(store => store.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(onOrderQuantity);

        return new CalculateReorderNeeds(
            nayax.Object, outstanding.Object, FakeNayaxProductLinkStore.EveryIdLinkedToItself());
    }

    private static (ListProducts List, ListLowStockProducts LowStock, GetProduct Get) UseCases(
        FakeProductCatalogStore catalog, CalculateReorderNeeds? reorderNeeds = null)
    {
        var lowStock = new ListLowStockProducts(catalog, reorderNeeds ?? ReorderNeeds());
        return (new ListProducts(catalog, lowStock), lowStock, new GetProduct(catalog));
    }

    [Fact]
    public async Task ListProducts_ReturnsTheFilteredCatalogueOrderedByName()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Zulu"),
            FakeProductCatalogStore.Product(2, "Alpha"));
        var useCases = UseCases(catalog);

        var listing = await useCases.List.Handle(ProductCatalogFilter.None, lowStockOnly: null, CancellationToken.None);

        Assert.Equal(["Alpha", "Zulu"], listing.Products.Select(product => product.Name));
        Assert.Equal(1, catalog.ListOrderedByNameCalls);
    }

    [Fact]
    public async Task ListProducts_PassesTheCallersFilterThroughUnchanged()
    {
        var catalog = new FakeProductCatalogStore(FakeProductCatalogStore.Product(1, "Coke"));
        var filter = new ProductCatalogFilter("Co", 7, 9);

        await UseCases(catalog).List.Handle(filter, lowStockOnly: false, CancellationToken.None);

        Assert.Equal(filter, catalog.LastFilter);
    }

    /// <summary>
    /// <c>lowStockOnly</c> replaces the plain listing rather than narrowing it, so the reorder-alert
    /// ordering (need descending, then name) wins over the by-name ordering - exactly as the former
    /// <c>ProductService.GetAll</c> did by delegating to <c>LowStock</c>.
    /// </summary>
    [Fact]
    public async Task ListProducts_DelegatesToTheLowStockListing_WhenLowStockOnlyIsRequested()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Zulu", quantityInStock: 10, lowStockThreshold: 20, restockTo: 40),
            FakeProductCatalogStore.Product(2, "Alpha", quantityInStock: 10, lowStockThreshold: 20, restockTo: 40),
            FakeProductCatalogStore.Product(3, "Middle", quantityInStock: 10, lowStockThreshold: 20, restockTo: 30),
            FakeProductCatalogStore.Product(4, "Stocked", quantityInStock: 99, lowStockThreshold: 1, restockTo: 1));

        var listing = await UseCases(catalog).List.Handle(
            ProductCatalogFilter.None, lowStockOnly: true, CancellationToken.None);

        Assert.Equal(["Alpha", "Zulu", "Middle"], listing.Products.Select(product => product.Name));
        Assert.Equal(0, catalog.ListOrderedByNameCalls);
    }

    [Fact]
    public async Task ListLowStockProducts_EnrichesEachProductWithItsLiveMachineNeedAndOutstandingOrders()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Coke", quantityInStock: 2, lowStockThreshold: 10, restockTo: 17));
        var lowStock = UseCases(
            catalog,
            ReorderNeeds(
                machineReplenishmentNeed: new Dictionary<long, int> { [1] = 1 },
                onOrderQuantity: new Dictionary<long, decimal> { [1] = 8m })).LowStock;

        var listing = await lowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

        var product = Assert.Single(listing.Products);
        Assert.Equal(1, product.MachineReplenishmentNeed);
        Assert.Equal(8m, product.OnOrderQuantity);
        Assert.Equal(8m, product.NeedToOrder);
        Assert.True(product.IsReorderAlert);
    }

    /// <summary>
    /// A negative outstanding quantity (over-received order) must not inflate projected stock; the
    /// former <c>ProductService.LowStock</c> clamped it at zero and this must keep doing so.
    /// </summary>
    [Fact]
    public async Task ListLowStockProducts_ClampsNegativeOutstandingOrderQuantitiesToZero()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Coke", quantityInStock: 4, lowStockThreshold: 20, restockTo: 20));
        var lowStock = UseCases(
            catalog,
            ReorderNeeds(onOrderQuantity: new Dictionary<long, decimal> { [1] = -5m })).LowStock;

        var product = Assert.Single(
            (await lowStock.Handle(ProductCatalogFilter.None, CancellationToken.None)).Products);

        Assert.Equal(0m, product.OnOrderQuantity);
        Assert.Equal(16m, product.NeedToOrder);
    }

    /// <summary>
    /// Issue #726: the reorder-alert listing of a business with no usable Nayax connection is the
    /// storage-only one, not a refusal. Storage stock, the threshold, the restock target and the
    /// outstanding supplier order are all still accounted for, and the machines' need is 0 -
    /// <c>GET api/products/alerts/low-stock</c> answered 409 <c>nayax_not_connected</c> before this,
    /// taking a purely local screen down with the fleet read.
    /// </summary>
    [Fact]
    public async Task ListLowStockProducts_ReturnsTheStorageOnlyAlerts_WhenTheBusinessHasNoUsableConnection()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Coke", quantityInStock: 2, lowStockThreshold: 10, restockTo: 17));
        var lowStock = UseCases(
            catalog,
            ReorderNeedsWithoutAProvider(onOrderQuantity: new Dictionary<long, decimal> { [1] = 8m })).LowStock;

        var listing = await lowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

        var product = Assert.Single(listing.Products);
        Assert.Equal(0, product.MachineReplenishmentNeed);
        Assert.Equal(8m, product.OnOrderQuantity);
        Assert.Equal(7m, product.NeedToOrder);
        Assert.True(product.IsReorderAlert);
    }

    [Fact]
    public async Task ListLowStockProducts_ExcludesInactiveProducts()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(
                1, "Retired", quantityInStock: 0, lowStockThreshold: 10, restockTo: 20, isActive: false));

        var listing = await UseCases(catalog).LowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

        Assert.Empty(listing.Products);
    }

    /// <summary>
    /// Issue #726, review repair: the listing carries the mode its reorder needs were calculated in,
    /// so a caller is not left reading <c>machineReplenishmentNeed: 0</c> as "the machines need
    /// nothing" when the machines were never read. A machine-aware listing has nothing unavailable to
    /// explain, so it carries no reason.
    /// </summary>
    [Fact]
    public async Task ListLowStockProducts_CarriesTheMachineAwareMode_WhenTheFleetWasRead()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Coke", quantityInStock: 2, lowStockThreshold: 10, restockTo: 17));
        var lowStock = UseCases(
            catalog,
            ReorderNeeds(machineReplenishmentNeed: new Dictionary<long, int> { [1] = 1 })).LowStock;

        var listing = await lowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

        Assert.Equal(ReorderMode.MachineAware, listing.Mode);
        Assert.Null(listing.MachineNeedUnavailableReason);
    }

    [Fact]
    public async Task ListLowStockProducts_CarriesTheStorageOnlyModeAndItsReason_WhenTheConnectionIsUnusable()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Coke", quantityInStock: 2, lowStockThreshold: 10, restockTo: 17));
        var lowStock = UseCases(
            catalog, ReorderNeedsWithoutAProvider(new Dictionary<long, decimal>())).LowStock;

        var listing = await lowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

        Assert.Equal(ReorderMode.StorageOnly, listing.Mode);
        Assert.Equal(MachineNeedUnavailableReason.NayaxNotConnected, listing.MachineNeedUnavailableReason);
    }

    [Fact]
    public async Task ListLowStockProducts_CarriesThePermissionNotGrantedReason_WhenNayaxRefusesTheFleetRead()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NayaxPermissionNotGrantedException("GetMachinesAsync"));
        var outstanding = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstanding.Setup(store => store.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Coke", quantityInStock: 2, lowStockThreshold: 10, restockTo: 17));

        var listing = await UseCases(catalog, new CalculateReorderNeeds(
            nayax.Object, outstanding.Object, FakeNayaxProductLinkStore.EveryIdLinkedToItself()))
            .LowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

        Assert.Equal(ReorderMode.StorageOnly, listing.Mode);
        Assert.Equal(MachineNeedUnavailableReason.PermissionNotGranted, listing.MachineNeedUnavailableReason);
    }

    /// <summary>
    /// An empty alert list is exactly where the mode matters most: "nothing alerts, machines
    /// included" and "nothing alerts on storage stock alone" are different answers, and only the mode
    /// tells them apart.
    /// </summary>
    [Fact]
    public async Task ListLowStockProducts_KeepsItsMode_WhenNothingAlerts()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(1, "Stocked", quantityInStock: 99, lowStockThreshold: 1, restockTo: 1));
        var lowStock = UseCases(
            catalog, ReorderNeedsWithoutAProvider(new Dictionary<long, decimal>())).LowStock;

        var listing = await lowStock.Handle(ProductCatalogFilter.None, CancellationToken.None);

        Assert.Empty(listing.Products);
        Assert.Equal(ReorderMode.StorageOnly, listing.Mode);
        Assert.Equal(MachineNeedUnavailableReason.NayaxNotConnected, listing.MachineNeedUnavailableReason);
    }

    /// <summary>
    /// The plain catalogue listing calculates no reorder needs at all, so it carries no mode: an
    /// absent mode is "no reorder facts were involved", never a guess at which mode would have
    /// applied. <c>lowStockOnly=true</c> is the reorder-alert listing and carries that listing's mode.
    /// </summary>
    [Fact]
    public async Task ListProducts_CarriesNoReorderMode_ForThePlainCatalogueListing()
    {
        var catalog = new FakeProductCatalogStore(FakeProductCatalogStore.Product(1, "Coke"));
        var useCases = UseCases(catalog, ReorderNeedsWithoutAProvider(new Dictionary<long, decimal>()));

        var plain = await useCases.List.Handle(
            ProductCatalogFilter.None, lowStockOnly: null, CancellationToken.None);
        var alerts = await useCases.List.Handle(
            ProductCatalogFilter.None, lowStockOnly: true, CancellationToken.None);

        Assert.Null(plain.Mode);
        Assert.Null(plain.MachineNeedUnavailableReason);
        Assert.Equal(ReorderMode.StorageOnly, alerts.Mode);
    }

    [Fact]
    public async Task GetProduct_ReturnsTheProduct_WhenItExists()
    {
        var catalog = new FakeProductCatalogStore(FakeProductCatalogStore.Product(7, "Coke"));

        var product = await UseCases(catalog).Get.Handle(7, CancellationToken.None);

        Assert.Equal("Coke", product!.Name);
    }

    [Fact]
    public async Task GetProduct_ReturnsNull_WhenNoSuchProductIsVisible()
    {
        var catalog = new FakeProductCatalogStore(FakeProductCatalogStore.Product(7, "Coke"));

        Assert.Null(await UseCases(catalog).Get.Handle(999, CancellationToken.None));
    }
}
