using Inventory.Application.Products;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

public class CreateProductTests
{
    private static ProductCreateFields ValidFields(
        int lowStockThreshold = 5,
        int restockTo = 10,
        decimal? initialUnitCost = null,
        string name = "Coke",
        string? unit = "unit") =>
        new(name, "SKU-1", null, 0, lowStockThreshold, restockTo, unit, null, true,
            initialUnitCost);

    [Fact]
    public async Task Handle_PersistsAndReturnsTheNewProductId_WhenFieldsAreValid()
    {
        var store = new FakeProductStore();
        var useCase = new CreateProduct(store);

        var result = await useCase.Handle(ValidFields(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.ProductId);
        Assert.Equal("Coke", store.LastCreated!.Name);
    }

    [Fact]
    public async Task Handle_RejectsNegativeInitialUnitCost_WithoutPersisting()
    {
        var store = new FakeProductStore();
        var useCase = new CreateProduct(store);

        var result = await useCase.Handle(ValidFields(initialUnitCost: -1m), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(Inventory.Domain.Products.ProductInitialCostPolicy.NegativeInitialUnitCostMessage, result.ValidationError);
        Assert.Null(store.LastCreated);
    }

    /// <summary>
    /// Issue #736 gave the create use case its first route, and with it the catalogue invariants a
    /// manually created product needs - which since issue #792 removed the selling price is this
    /// one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_RejectsAMissingName_WithoutPersisting(string name)
    {
        var store = new FakeProductStore();

        var result = await new CreateProduct(store).Handle(ValidFields(name: name), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(Inventory.Domain.Products.ProductCataloguePolicy.NameRequiredMessage, result.ValidationError);
        Assert.Null(store.LastCreated);
    }

    /// <summary>
    /// Issue #895: a product created by hand has a unit label. The create refuses a blank one
    /// outright - unlike an update, which refuses only a blank that would erase a stored label -
    /// and nothing is persisted.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_RejectsAMissingUnitLabel_WithoutPersisting(string? unit)
    {
        var store = new FakeProductStore();

        var result = await new CreateProduct(store).Handle(ValidFields(unit: unit), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(Inventory.Domain.Products.ProductCataloguePolicy.UnitRequiredMessage, result.ValidationError);
        Assert.Null(store.LastCreated);
        Assert.Null(result.ProductId);
    }

    /// <summary>
    /// The store's own refusal - a category this business does not own - reaches the caller with its
    /// message rather than as a created product.
    /// </summary>
    [Fact]
    public async Task Handle_ReportsTheStoresOwnRefusal()
    {
        var store = new FakeProductStore
        {
            RefusalToReturn = Inventory.Domain.Products.ProductCataloguePolicy.UnknownCategoryMessage,
        };

        var result = await new CreateProduct(store).Handle(ValidFields(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(
            Inventory.Domain.Products.ProductCataloguePolicy.UnknownCategoryMessage,
            result.ValidationError);
        Assert.Null(result.ProductId);
    }

    [Fact]
    public async Task Handle_RejectsInvalidRestockSettings_WithoutPersisting()
    {
        var store = new FakeProductStore();
        var useCase = new CreateProduct(store);

        var result = await useCase.Handle(ValidFields(lowStockThreshold: 10, restockTo: 9), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(Inventory.Domain.Products.ProductRestockPolicy.RestockToBelowThresholdMessage, result.ValidationError);
        Assert.Null(store.LastCreated);
    }
}
