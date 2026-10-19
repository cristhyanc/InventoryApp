using Inventory.Application.Categories;
using Inventory.Domain.Categories;
using Xunit;

namespace InventoryApi.Tests.Application.Categories;

/// <summary>
/// The category write use cases (issue #736, parent #329 decision P2): what they validate
/// themselves, and what they leave to the store because it is a statement about stored data.
///
/// <para>The refusals the store owns - a duplicate name, a provider-managed rename, a category
/// products still use - are proved against the relational schema in
/// <c>InventoryApi.Tests.Adapters.Persistence.EfCategoryStoreTests</c>.</para>
/// </summary>
public class CategoryWriteUseCaseTests
{
    [Fact]
    public async Task Create_persists_the_category_and_returns_its_id()
    {
        var store = new FakeCategoryStore();

        var attempt = await new CreateCategory(store).Handle(
            new CategoryWriteFields("Drinks", "Cold"), CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Succeeded, attempt.Outcome);
        Assert.Equal(1, attempt.CategoryId);
        Assert.Equal(("Drinks", "Cold"), (store.LastWritten!.Name, store.LastWritten.Description));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Create_refuses_a_missing_name_without_writing(string? name)
    {
        var store = new FakeCategoryStore();

        var attempt = await new CreateCategory(store).Handle(
            new CategoryWriteFields(name, null), CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(CategoryCataloguePolicy.NameRequiredMessage, attempt.Refusal);
        Assert.Null(store.LastWritten);
    }

    [Fact]
    public async Task Create_reports_the_stores_own_refusal()
    {
        var duplicate = CategoryCataloguePolicy.DuplicateNameMessage("Drinks");
        var store = new FakeCategoryStore { RefusalToReturn = duplicate };

        var attempt = await new CreateCategory(store).Handle(
            new CategoryWriteFields("Drinks", null), CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(duplicate, attempt.Refusal);
    }

    [Fact]
    public async Task Update_writes_the_submitted_name_and_description()
    {
        var store = new FakeCategoryStore([new CategoryRecord(5, "Drinks", null)]);

        var attempt = await new UpdateCategory(store).Handle(
            5, new CategoryWriteFields("Cold drinks", "Chilled"), CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Succeeded, attempt.Outcome);
        var stored = await store.FindByIdAsync(5, CancellationToken.None);
        Assert.Equal(("Cold drinks", "Chilled"), (stored!.Name, stored.Description));
    }

    [Fact]
    public async Task Update_refuses_a_missing_name_without_writing()
    {
        var store = new FakeCategoryStore([new CategoryRecord(5, "Drinks", null)]);

        var attempt = await new UpdateCategory(store).Handle(
            5, new CategoryWriteFields(" ", null), CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(CategoryCataloguePolicy.NameRequiredMessage, attempt.Refusal);
        Assert.Null(store.LastWrittenId);
    }

    [Fact]
    public async Task Update_reports_not_found_for_an_unknown_category()
    {
        var attempt = await new UpdateCategory(new FakeCategoryStore()).Handle(
            999, new CategoryWriteFields("Drinks", null), CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.NotFound, attempt.Outcome);
    }

    [Fact]
    public async Task Delete_removes_the_category()
    {
        var store = new FakeCategoryStore([new CategoryRecord(5, "Drinks", null)]);

        var attempt = await new DeleteCategory(store).Handle(5, CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Succeeded, attempt.Outcome);
        Assert.Null(await store.FindByIdAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_reports_not_found_for_an_unknown_category()
    {
        var attempt = await new DeleteCategory(new FakeCategoryStore()).Handle(999, CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.NotFound, attempt.Outcome);
    }

    [Fact]
    public async Task Delete_reports_the_stores_own_refusal_and_keeps_the_category()
    {
        var inUse = CategoryCataloguePolicy.DeletionRefusal(3);
        var store = new FakeCategoryStore([new CategoryRecord(5, "Drinks", null)])
        {
            RefusalToReturn = inUse,
        };

        var attempt = await new DeleteCategory(store).Handle(5, CancellationToken.None);

        Assert.Equal(CategoryWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(inUse, attempt.Refusal);
        Assert.NotNull(await store.FindByIdAsync(5, CancellationToken.None));
    }
}
