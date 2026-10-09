using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
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

        return new CalculateReorderNeeds(nayax.Object, outstanding.Object);
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

        var products = await useCases.List.Handle(ProductCatalogFilter.None, lowStockOnly: null, CancellationToken.None);

        Assert.Equal(["Alpha", "Zulu"], products.Select(product => product.Name));
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

        var products = await UseCases(catalog).List.Handle(
            ProductCatalogFilter.None, lowStockOnly: true, CancellationToken.None);

        Assert.Equal(["Alpha", "Zulu", "Middle"], products.Select(product => product.Name));
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

        var product = Assert.Single(await lowStock.Handle(ProductCatalogFilter.None, CancellationToken.None));

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

        var product = Assert.Single(await lowStock.Handle(ProductCatalogFilter.None, CancellationToken.None));

        Assert.Equal(0m, product.OnOrderQuantity);
        Assert.Equal(16m, product.NeedToOrder);
    }

    [Fact]
    public async Task ListLowStockProducts_ExcludesInactiveProducts()
    {
        var catalog = new FakeProductCatalogStore(
            FakeProductCatalogStore.Product(
                1, "Retired", quantityInStock: 0, lowStockThreshold: 10, restockTo: 20, isActive: false));

        Assert.Empty(await UseCases(catalog).LowStock.Handle(ProductCatalogFilter.None, CancellationToken.None));
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
