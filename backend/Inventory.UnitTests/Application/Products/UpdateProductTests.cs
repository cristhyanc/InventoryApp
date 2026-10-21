using Inventory.Application.Products;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

public class UpdateProductTests
{
    private static ProductUpdateFields ValidFields(
        int lowStockThreshold = 5,
        int restockTo = 10,
        string? name = "Coke") =>
        new(name, "SKU-1", null, lowStockThreshold, restockTo, "unit", null, true);

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

    /// <summary>
    /// Issue #736: the name and the category reach the store, because a business without a sales
    /// provider maintains its own catalogue and this is the only place those fields can be changed.
    /// The selling price that reached it alongside them is gone with the field (issue #792).
    /// </summary>
    [Fact]
    public async Task Handle_PassesTheNameAndCategoryToTheStore()
    {
        var store = new FakeProductStore([1]);

        var result = await new UpdateProduct(store).Handle(
            1,
            new ProductUpdateFields("Coke Zero", "SKU-1", null, 5, 10, "can", 9, true),
            CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.Success, result.Outcome);
        Assert.Equal(("Coke Zero", "can", (long?)9), (
            store.LastUpdated!.Name, store.LastUpdated.Unit, store.LastUpdated.CategoryId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Handle_RejectsAMissingName_WithoutPersisting(string? name)
    {
        var store = new FakeProductStore([1]);

        var result = await new UpdateProduct(store).Handle(1, ValidFields(name: name), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.Invalid, result.Outcome);
        Assert.Equal(Inventory.Domain.Products.ProductCataloguePolicy.NameRequiredMessage, result.ValidationError);
        Assert.Null(store.LastUpdatedId);
    }

    /// <summary>
    /// A refusal the store decided from stored data - a provider-managed field, a category of
    /// another business - reaches the caller as its own message, not as a generic failure and not as
    /// a success. What each refusal means is proved against the real adapter in
    /// <c>InventoryApi.Tests.Adapters.Persistence.EfProductCatalogueWriteTests</c>.
    /// </summary>
    [Fact]
    public async Task Handle_ReportsTheStoresOwnRefusal()
    {
        var store = new FakeProductStore([1]) { RefusalToReturn = "This product's name is managed by Nayax." };

        var result = await new UpdateProduct(store).Handle(1, ValidFields(), CancellationToken.None);

        Assert.Equal(UpdateProductOutcome.Invalid, result.Outcome);
        Assert.Equal("This product's name is managed by Nayax.", result.ValidationError);
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
