using Inventory.Application.Categories;
using Xunit;

namespace InventoryApi.Tests.Application.Categories;

public class ListCategoriesTests
{
    [Fact]
    public async Task Returns_categories_ordered_by_name()
    {
        var store = new FakeCategoryStore(
        [
            new CategoryRecord(1, "Zebra", null),
            new CategoryRecord(2, "Apple", "Fruit"),
        ]);

        var result = await new ListCategories(store).Handle(CancellationToken.None);

        Assert.Equal(new[] { "Apple", "Zebra" }, result.Select(x => x.Name));
    }
}
