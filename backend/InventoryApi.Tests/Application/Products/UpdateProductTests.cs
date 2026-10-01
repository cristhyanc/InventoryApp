using Inventory.Application.Products;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

public class UpdateProductTests
{
    private static ProductUpdateFields ValidFields(int lowStockThreshold = 5, int restockTo = 10) =>
        new("SKU-1", null, lowStockThreshold, restockTo, "unit", null, true);

    [Fact]
    public async Task Handle_UpdatesTheProduct_WhenFieldsAreValid()
    {
        var store = new FakeProductStore([1]);
        var useCase = new UpdateProduct(store);

        var result = await useCase.Handle(1, ValidFields(), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.Success, result.Outcome);
        Assert.Equal(1, store.LastUpdatedId);
    }

    [Fact]
    public async Task Handle_ReturnsNotFound_WhenTheProductDoesNotExist()
    {
        var store = new FakeProductStore();
        var useCase = new UpdateProduct(store);

        var result = await useCase.Handle(999, ValidFields(), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.NotFound, result.Outcome);
        Assert.Null(store.LastUpdatedId);
    }

    [Fact]
    public async Task Handle_ReturnsNotFound_EvenWhenTheRequestAlsoHasInvalidRestockSettings()
    {
        var store = new FakeProductStore();
        var useCase = new UpdateProduct(store);

        var result = await useCase.Handle(999, ValidFields(lowStockThreshold: 10, restockTo: 9), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.NotFound, result.Outcome);
    }

    /// <summary>
    /// The existence check is a read, so it cannot be the authoritative not-found answer: another
    /// request may delete the product before the write lands. Deleting it in exactly that window must
    /// report not-found, never a success that changed nothing (which the API would answer as 204).
    /// </summary>
    [Fact]
    public async Task Handle_ReturnsNotFound_WhenTheProductIsDeletedBetweenTheExistenceCheckAndTheUpdate()
    {
        var store = new FakeProductStore([1]);
        store.AfterExistsAsync = id => store.Remove(id);
        var useCase = new UpdateProduct(store);

        var result = await useCase.Handle(1, ValidFields(), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.NotFound, result.Outcome);
        Assert.Null(store.LastUpdatedId);
    }

    [Fact]
    public async Task Handle_RejectsInvalidRestockSettings_WithoutPersisting_WhenTheProductExists()
    {
        var store = new FakeProductStore([1]);
        var useCase = new UpdateProduct(store);

        var result = await useCase.Handle(1, ValidFields(lowStockThreshold: 10, restockTo: 9), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.Invalid, result.Outcome);
        Assert.Equal(Inventory.Domain.Products.ProductRestockPolicy.RestockToBelowThresholdMessage, result.ValidationError);
        Assert.Null(store.LastUpdatedId);
    }
}
