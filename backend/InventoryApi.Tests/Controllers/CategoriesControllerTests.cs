using Inventory.Application.Categories;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Categories;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class CategoriesControllerTests
{
    private static CategoriesController CreateController(FakeCategoryStore store) =>
        new(new ListCategories(store), new GetCategory(store));

    [Fact]
    public async Task GetAll_returns_ok_with_categories_ordered_by_name()
    {
        var store = new FakeCategoryStore(
        [
            new CategoryRecord(1, "Zebra", null),
            new CategoryRecord(2, "Apple", "Fruit"),
        ]);
        var controller = CreateController(store);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var categories = Assert.IsAssignableFrom<IEnumerable<CategoryResponse>>(ok.Value).ToList();
        Assert.Equal(new[] { "Apple", "Zebra" }, categories.Select(c => c.Name));
    }

    [Fact]
    public async Task Get_returns_ok_for_existing_category()
    {
        var store = new FakeCategoryStore([new CategoryRecord(1, "Snacks", "Salty snacks")]);
        var controller = CreateController(store);

        var result = await controller.Get(1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CategoryResponse>(ok.Value);
        Assert.Equal(1, response.Id);
        Assert.Equal("Snacks", response.Name);
        Assert.Equal("Salty snacks", response.Description);
    }

    [Fact]
    public async Task Get_returns_not_found_for_missing_category()
    {
        var store = new FakeCategoryStore();
        var controller = CreateController(store);

        var result = await controller.Get(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
