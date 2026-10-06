using Inventory.Application.Products;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

public class CreateProductTests
{
    private static ProductCreateFields ValidFields(int lowStockThreshold = 5, int restockTo = 10, decimal? initialUnitCost = null) =>
        new("Coke", "SKU-1", null, 1.5m, 0, lowStockThreshold, restockTo, "unit", null, null, true, initialUnitCost);

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
