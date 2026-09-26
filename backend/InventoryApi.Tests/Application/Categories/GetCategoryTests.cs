using Inventory.Application.Categories;
using Xunit;

namespace InventoryApi.Tests.Application.Categories;

public class GetCategoryTests
{
    [Fact]
    public async Task Returns_existing_category()
    {
        var store = new FakeCategoryStore([new CategoryRecord(1, "Snacks", null)]);

        var result = await new GetCategory(store).Handle(1, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Snacks", result!.Name);
    }

    [Fact]
    public async Task Returns_null_for_missing_category()
    {
        var store = new FakeCategoryStore();

        var result = await new GetCategory(store).Handle(999, CancellationToken.None);

        Assert.Null(result);
    }
}
